# BuildChatBot installer for Windows (winget-driven).
# Installs .NET 8, OpenJDK 17, Git, Flutter, Android command-line tools.
# Optionally sets up an SSH key and optionally clones a Flutter project.
# Finally runs the env.local wizard and builds the bot.

[CmdletBinding()]
param(
    [switch]$NonInteractive,
    [switch]$SkipDeps,
    [switch]$Force,
    [switch]$WithNdk
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $here
$envFile = Join-Path $projectRoot 'env.local'
$exampleFile = Join-Path $here 'common\env.local.example'

function Step($msg)  { Write-Host ""; Write-Host "▶ $msg" -ForegroundColor Cyan }
function Ok($msg)    { Write-Host "  ✓ $msg" -ForegroundColor Green }
function Warn($msg)  { Write-Host "  ! $msg" -ForegroundColor Yellow }

function Prompt-YN([string]$question, [string]$default = 'N') {
    if ($NonInteractive) { return ($default -eq 'Y') }
    $answer = Read-Host "$question"
    if ([string]::IsNullOrWhiteSpace($answer)) { $answer = $default }
    return ($answer -match '^[Yy]')
}

function Ask([string]$prompt, [string]$default = '') {
    if ($NonInteractive) { return $default }
    if ($default) { $answer = Read-Host "  $prompt [$default]" }
    else          { $answer = Read-Host "  $prompt" }
    if ([string]::IsNullOrWhiteSpace($answer)) { return $default }
    return $answer
}

function Ask-Secret([string]$prompt, [string]$default = '') {
    if ($NonInteractive) { return $default }
    $secure = Read-Host -AsSecureString "  $prompt"
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try {
        $val = [Runtime.InteropServices.Marshal]::PtrToStringAuto($bstr)
    } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
    if ([string]::IsNullOrWhiteSpace($val)) { return $default }
    return $val
}

# ─── Step 1: preflight ───────────────────────────────────────────────
Step "Preflight"
$os = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
Ok "Windows detected: $os"

if (-not $SkipDeps) {
    # ─── Step 2: winget ──────────────────────────────────────────────
    Step "winget"
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        throw "winget is required. Install 'App Installer' from the Microsoft Store and re-run."
    }
    Ok "winget present"

    # ─── Step 3: core deps ───────────────────────────────────────────
    Step "Core dependencies"
    $packages = @(
        'Microsoft.DotNet.SDK.8',
        'EclipseAdoptium.Temurin.17.JDK',
        'Git.Git'
    )
    foreach ($pkg in $packages) {
        Write-Host "  installing $pkg…"
        winget install --id $pkg --accept-source-agreements --accept-package-agreements --silent --exact `
            --source winget --disable-interactivity 2>$null | Out-Null
    }
    Ok "dotnet: $(dotnet --version 2>$null)"
    Ok "java:   $(java -version 2>&1 | Select-Object -First 1)"
    Ok "git:    $(git --version)"

    # Flutter — official recommendation: git clone to C:\src\flutter
    Step "Flutter SDK"
    $flutterPath = 'C:\src\flutter'
    if (-not (Test-Path $flutterPath)) {
        New-Item -ItemType Directory -Force -Path 'C:\src' | Out-Null
        git clone --branch stable https://github.com/flutter/flutter.git $flutterPath
    }
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    if ($userPath -notmatch [regex]::Escape("$flutterPath\bin")) {
        [Environment]::SetEnvironmentVariable('Path', "$userPath;$flutterPath\bin", 'User')
        $env:Path = "$env:Path;$flutterPath\bin"
    }
    Ok "flutter: $(& "$flutterPath\bin\flutter.bat" --version | Select-Object -First 1)"

    # ─── Step 4: Android SDK ─────────────────────────────────────────
    Step "Android SDK packages"
    $sdkRoot = "$env:LOCALAPPDATA\Android\Sdk"
    New-Item -ItemType Directory -Force -Path "$sdkRoot\cmdline-tools" | Out-Null
    $latestDir = Join-Path $sdkRoot 'cmdline-tools\latest'
    if (-not (Test-Path $latestDir)) {
        $tmpZip = Join-Path $env:TEMP "cmdline-tools.zip"
        Invoke-WebRequest -UseBasicParsing -Uri "https://dl.google.com/android/repository/commandlinetools-win-11076708_latest.zip" -OutFile $tmpZip
        Expand-Archive -Path $tmpZip -DestinationPath (Join-Path $env:TEMP 'cmdline-extract') -Force
        Move-Item -Path (Join-Path $env:TEMP 'cmdline-extract\cmdline-tools') -Destination $latestDir -Force
    }
    $sdkmanager = Join-Path $latestDir 'bin\sdkmanager.bat'
    & cmd /c "echo y | `"$sdkmanager`" --sdk_root=`"$sdkRoot`" --licenses" | Out-Null
    & $sdkmanager --sdk_root="$sdkRoot" "platform-tools" "platforms;android-35" "build-tools;35.0.0"
    if ($WithNdk) { & $sdkmanager --sdk_root="$sdkRoot" "ndk;26.1.10909125" }
    [Environment]::SetEnvironmentVariable('ANDROID_SDK_ROOT', $sdkRoot, 'User')
    Ok "SDK packages installed at $sdkRoot"

    # JAVA_HOME
    $javaHome = (Get-ChildItem 'C:\Program Files\Eclipse Adoptium' -Directory -ErrorAction SilentlyContinue |
                 Where-Object Name -match '17' |
                 Select-Object -First 1 -ExpandProperty FullName)
    if ($javaHome) {
        [Environment]::SetEnvironmentVariable('JAVA_HOME', $javaHome, 'User')
        $env:JAVA_HOME = $javaHome
        Ok "JAVA_HOME=$javaHome"
    }

    Step "flutter doctor"
    & "$flutterPath\bin\flutter.bat" doctor
}

# ─── Step 5: optional Git/SSH setup ──────────────────────────────────
Step "Git / SSH setup (optional)"
if (Prompt-YN "Set up a Git SSH key now? [y/N]" 'N') {
    $email = Ask "Email for the key" "buildbot@local"
    $keyPath = "$env:USERPROFILE\.ssh\id_ed25519"
    if (-not (Test-Path $keyPath)) {
        New-Item -ItemType Directory -Force -Path "$env:USERPROFILE\.ssh" | Out-Null
        ssh-keygen -t ed25519 -C $email -f $keyPath -N '""'
        Ok "Created $keyPath"
    } else {
        Warn "Key exists at $keyPath — leaving it alone."
    }
    Write-Host ""
    Write-Host "  Public key:"
    Get-Content "$keyPath.pub" | ForEach-Object { "    $_" }
    Get-Content "$keyPath.pub" | Set-Clipboard
    Ok "Public key copied to clipboard"
    if (-not $NonInteractive) { Read-Host "  Paste it into GitHub/GitLab → SSH keys, then press ENTER" | Out-Null }
}

# ─── Step 6: optional clone ─────────────────────────────────────────
Step "Clone Git project (optional)"
$repoOverride = $null; $branchOverride = $null
if (Prompt-YN "Clone a Flutter project into a new folder now? [y/N]" 'N') {
    $repoUrl = Ask "Repo URL"
    $defaultTarget = Join-Path (Split-Path -Parent $projectRoot) 'mobile_aplication'
    $target = Ask "Target folder" $defaultTarget
    $branch = Ask "Branch" 'master'
    if ($repoUrl) {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        if (Test-Path (Join-Path $target '.git')) {
            Push-Location $target
            git fetch; git checkout $branch; git pull --ff-only
            Pop-Location
        } else {
            git clone --branch $branch $repoUrl $target
        }
        $repoOverride = $target; $branchOverride = $branch
        Ok "Project ready at $target"
    }
}

# ─── Step 7: env.local wizard ───────────────────────────────────────
# Philosophy: env.local should only contain keys the user actually sets
# (secrets + per-machine paths). Defaults live in BotConfig.cs; the example
# file documents every optional key the user can add by hand later.
Step "env.local wizard"
if ((Test-Path $envFile) -and -not $Force) {
    Warn "env.local already exists at $envFile — skipping wizard (-Force to regenerate)."
} else {
    $defaultJava = if ($env:JAVA_HOME) { $env:JAVA_HOME } else { '' }
    $defaultSdk  = if ($env:ANDROID_SDK_ROOT) { $env:ANDROID_SDK_ROOT } else { "$env:LOCALAPPDATA\Android\Sdk" }
    $defaultRepoPath = if ($repoOverride) { $repoOverride } else { Join-Path (Split-Path -Parent $projectRoot) 'mobile_aplication' }
    $defaultBranch   = if ($branchOverride) { $branchOverride } else { 'master' }

    Write-Host ""
    Write-Host "▶ Telegram (required)"
    $tgToken   = Ask-Secret "TELEGRAM_BOT_TOKEN" ''
    $tgApiId   = Ask        "TELEGRAM_API_ID"    ''
    $tgApiHash = Ask-Secret "TELEGRAM_API_HASH"  ''

    Write-Host ""
    Write-Host "▶ Repository (required)"
    $repoPath   = Ask "REPO_PATH"   $defaultRepoPath
    $repoBranch = Ask "REPO_BRANCH" $defaultBranch

    Write-Host ""
    Write-Host "▶ Android toolchain (recommended)"
    $javaHome = Ask "JAVA_HOME"        $defaultJava
    $sdkRoot  = Ask "ANDROID_SDK_ROOT" $defaultSdk

    Write-Host ""
    Write-Host "▶ Optional — SSH for private repo (blank to skip)"
    $repoSshKey = Ask "REPO_SSH_KEY_PATH" ''

    $lines = @()
    $lines += "# Generated by install-windows.ps1 on $(Get-Date -Format 'yyyy-MM-ddTHH:mm:ssK')"
    $lines += "# Only required + user-set keys are written here. Defaults live in BotConfig.cs."
    $lines += "# See $exampleFile for every optional key you can add."
    $lines += ""
    $lines += "# --- Required ---"
    $lines += "TELEGRAM_BOT_TOKEN=$tgToken"
    $lines += "TELEGRAM_API_ID=$tgApiId"
    $lines += "TELEGRAM_API_HASH=$tgApiHash"
    $lines += "REPO_PATH=$repoPath"
    $lines += "REPO_BRANCH=$repoBranch"

    if ($javaHome -or $sdkRoot -or $repoSshKey) {
        $lines += ""
        $lines += "# --- Optional overrides ---"
        if ($javaHome)   { $lines += "JAVA_HOME=$javaHome" }
        if ($sdkRoot)    { $lines += "ANDROID_SDK_ROOT=$sdkRoot" }
        if ($repoSshKey) { $lines += "REPO_SSH_KEY_PATH=$repoSshKey" }
    }

    ($lines -join "`r`n") | Out-File -FilePath $envFile -Encoding UTF8
    Ok "Wrote $envFile"
    Write-Host "   To override more knobs, copy lines from $exampleFile into $envFile."
}

# ─── Step 8: dotnet restore + build ─────────────────────────────────
Step "dotnet restore + build"
if (Get-Command dotnet -ErrorAction SilentlyContinue) {
    Push-Location $projectRoot
    dotnet restore
    dotnet build -c Release
    Pop-Location
} else {
    Warn "dotnet not on PATH — open a new shell and re-run with -SkipDeps."
}

# ─── Step 9: summary ────────────────────────────────────────────────
Step "Done"
Write-Host @"

  Run the bot:
    cd $projectRoot
    dotnet run --project src\BuildChatBot

  Config: $envFile
"@
