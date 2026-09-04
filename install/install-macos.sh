#!/usr/bin/env bash
# BuildChatBot installer for macOS.
# Installs .NET 8, OpenJDK 17, Git, Flutter, Android command-line tools via Homebrew,
# checks the optional iOS toolchain (Xcode, CocoaPods, signing identity) used by the
# /release_ios_* commands, then optionally sets up an SSH key, optionally clones a Git
# project, then runs the env.local wizard and builds the bot.
set -euo pipefail

NON_INTERACTIVE=0
SKIP_DEPS=0
FORCE=0
WITH_NDK=0

while [[ $# -gt 0 ]]; do
    case "$1" in
        --non-interactive) NON_INTERACTIVE=1 ;;
        --skip-deps)       SKIP_DEPS=1 ;;
        --force)           FORCE=1 ;;
        --with-ndk)        WITH_NDK=1 ;;
        -h|--help)
            cat <<EOF
Usage: install-macos.sh [--non-interactive] [--skip-deps] [--force] [--with-ndk]
  --non-interactive  use defaults, do not prompt
  --skip-deps        skip Homebrew installs, only run env wizard
  --force            overwrite existing env.local
  --with-ndk         also install Android NDK
EOF
            exit 0
            ;;
        *) echo "Unknown flag: $1" >&2; exit 2 ;;
    esac
    shift
done

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$HERE/.." && pwd)"
ENV_FILE="$PROJECT_ROOT/env.local"
EXAMPLE_FILE="$HERE/common/env.local.example"

if [[ "$(uname -s)" != "Darwin" ]]; then
    echo "This script is for macOS. Run install-linux.sh or install-windows.ps1 instead." >&2
    exit 1
fi

step() { printf '\n\033[1;34m▶ %s\033[0m\n' "$*"; }
ok()   { printf '  \033[1;32m✓\033[0m %s\n' "$*"; }
warn() { printf '  \033[1;33m!\033[0m %s\n' "$*"; }

prompt_yn() {
    local q="$1" default="${2:-N}" ans
    if [[ "$NON_INTERACTIVE" == "1" ]]; then
        [[ "$default" == "Y" ]]
        return $?
    fi
    read -r -p "$q " ans </dev/tty || true
    ans="${ans:-$default}"
    [[ "$ans" =~ ^[Yy] ]]
}

# ─── Step 1: preflight ───────────────────────────────────────────────
step "Preflight"
ARCH="$(uname -m)"
ok "macOS detected (arch=$ARCH)"

if [[ "$SKIP_DEPS" == "1" ]]; then
    warn "--skip-deps set; jumping to env wizard."
else
    # ─── Step 2: Homebrew ────────────────────────────────────────────
    step "Homebrew"
    if ! command -v brew >/dev/null 2>&1; then
        warn "Homebrew not found — installing."
        /bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"
        if [[ "$ARCH" == "arm64" ]]; then
            eval "$(/opt/homebrew/bin/brew shellenv)"
        else
            eval "$(/usr/local/bin/brew shellenv)"
        fi
    fi
    ok "brew: $(brew --version | head -n1)"

    # ─── Step 3: core deps via brew ──────────────────────────────────
    step "Core dependencies"
    brew update
    brew install --quiet git || true
    brew install --quiet openjdk@17 || true
    brew install --quiet --cask dotnet-sdk || true
    brew install --quiet --cask flutter || true
    brew install --quiet --cask android-commandlinetools || true

    # Symlink OpenJDK so /usr/libexec/java_home can see it
    if [[ -d "$(brew --prefix openjdk@17)/libexec/openjdk.jdk" && ! -d /Library/Java/JavaVirtualMachines/openjdk-17.jdk ]]; then
        warn "Linking JDK17 system-wide (sudo)"
        sudo ln -sfn "$(brew --prefix openjdk@17)/libexec/openjdk.jdk" /Library/Java/JavaVirtualMachines/openjdk-17.jdk || true
    fi
    ok "dotnet: $(dotnet --version 2>/dev/null || echo 'not found')"
    ok "java:   $(java -version 2>&1 | head -n1)"
    ok "git:    $(git --version)"
    ok "flutter: $(flutter --version 2>/dev/null | head -n1 || echo 'not found')"

    # ─── Step 4: Android SDK packages ────────────────────────────────
    step "Android SDK packages"
    export ANDROID_SDK_ROOT="${ANDROID_SDK_ROOT:-$HOME/Library/Android/sdk}"
    mkdir -p "$ANDROID_SDK_ROOT"
    SDKMANAGER="$(command -v sdkmanager || true)"
    if [[ -z "$SDKMANAGER" ]]; then
        warn "sdkmanager not on PATH; trying brew cask location"
        SDKMANAGER="/opt/homebrew/share/android-commandlinetools/cmdline-tools/latest/bin/sdkmanager"
        [[ -x "$SDKMANAGER" ]] || SDKMANAGER="/usr/local/share/android-commandlinetools/cmdline-tools/latest/bin/sdkmanager"
    fi
    if [[ -x "$SDKMANAGER" ]]; then
        yes 2>/dev/null | "$SDKMANAGER" --sdk_root="$ANDROID_SDK_ROOT" --licenses >/dev/null || true
        "$SDKMANAGER" --sdk_root="$ANDROID_SDK_ROOT" "platform-tools" "platforms;android-35" "build-tools;35.0.0"
        if [[ "$WITH_NDK" == "1" ]]; then
            "$SDKMANAGER" --sdk_root="$ANDROID_SDK_ROOT" "ndk;26.1.10909125"
        fi
        ok "SDK packages installed at $ANDROID_SDK_ROOT"
    else
        warn "sdkmanager unavailable — install Android Studio manually, then re-run with --skip-deps."
    fi

    # ─── Step 5: iOS toolchain (optional) ────────────────────────────
    # Needed only for /release_ios_testflight and /release_ios_appstore. Everything
    # here warns instead of failing, so an Android-only machine still installs fine.
    step "iOS toolchain (optional — needed for /release_ios_*)"
    XCODE_PATH="$(xcode-select -p 2>/dev/null || true)"
    if [[ -z "$XCODE_PATH" || "$XCODE_PATH" == */CommandLineTools ]]; then
        warn "Full Xcode not selected (xcode-select -p = ${XCODE_PATH:-<none>})."
        cat <<'EOF'
    iOS release builds need the full Xcode, not just the Command Line Tools:
      1. Install Xcode from the App Store (or developer.apple.com/download).
      2. sudo xcode-select -s /Applications/Xcode.app/Contents/Developer
      3. sudo xcodebuild -license accept
      4. sudo xcodebuild -runFirstLaunch
    Then re-run this installer with --skip-deps, or just continue for Android-only use.
EOF
    else
        ok "xcode-select: $XCODE_PATH"
        if XCODEBUILD_VER="$(xcodebuild -version 2>/dev/null | head -n1)"; then
            ok "xcodebuild: $XCODEBUILD_VER"
        else
            warn "xcodebuild failed — run: sudo xcodebuild -license accept && sudo xcodebuild -runFirstLaunch"
        fi
    fi

    brew install --quiet cocoapods || true
    ok "cocoapods: $(pod --version 2>/dev/null || echo 'not found')"

    if security find-identity -v -p codesigning 2>/dev/null | grep -q "Apple Distribution"; then
        ok "An 'Apple Distribution' signing identity is in the keychain."
    else
        warn "No 'Apple Distribution' identity in the login keychain — import the .p12 (double-click it) or sign in via Xcode → Settings → Accounts → Manage Certificates."
    fi
    echo "    Headless build server? See install/macos/MAC_MINI_SETUP.md"

    # ─── Step 6: flutter doctor ──────────────────────────────────────
    step "flutter doctor"
    flutter doctor || true
fi

# ─── Step 7: optional Git/SSH setup ──────────────────────────────────
step "Git / SSH setup (optional)"
if prompt_yn "Set up a Git SSH key now? [y/N]" "N"; then
    if [[ "$NON_INTERACTIVE" == "1" ]]; then
        EMAIL="${GIT_USER_EMAIL:-buildbot@local}"
    else
        read -r -p "  Email for the key: " EMAIL </dev/tty || EMAIL="buildbot@local"
    fi
    KEY_PATH="$HOME/.ssh/id_ed25519"
    if [[ ! -f "$KEY_PATH" ]]; then
        mkdir -p "$HOME/.ssh" && chmod 700 "$HOME/.ssh"
        ssh-keygen -t ed25519 -C "$EMAIL" -f "$KEY_PATH" -N "" </dev/tty
        ok "Created $KEY_PATH"
    else
        warn "Key exists at $KEY_PATH — leaving it alone."
    fi
    echo ""
    echo "  Public key:"
    sed 's/^/    /' "$KEY_PATH.pub"
    if command -v pbcopy >/dev/null 2>&1; then
        pbcopy < "$KEY_PATH.pub"
        ok "Public key copied to clipboard (pbcopy)"
    fi
    echo ""
    echo "  → Paste it into GitHub / GitLab → SSH keys, then press ENTER to continue."
    [[ "$NON_INTERACTIVE" == "1" ]] || read -r _ </dev/tty || true
fi

# ─── Step 8: optional clone of a Git project ────────────────────────
step "Clone Git project (optional)"
if prompt_yn "Clone a Flutter project into a new folder now? [y/N]" "N"; then
    read -r -p "  Repo URL (e.g. git@github.com:org/repo.git): " REPO_URL </dev/tty || REPO_URL=""
    DEFAULT_TARGET="$PROJECT_ROOT/../mobile_aplication"
    read -r -p "  Target folder [$DEFAULT_TARGET]: " TARGET </dev/tty || TARGET=""
    TARGET="${TARGET:-$DEFAULT_TARGET}"
    read -r -p "  Branch [master]: " BRANCH </dev/tty || BRANCH=""
    BRANCH="${BRANCH:-master}"

    if [[ -n "$REPO_URL" ]]; then
        mkdir -p "$(dirname "$TARGET")"
        if [[ -d "$TARGET/.git" ]]; then
            warn "$TARGET already contains a git repo — pulling instead of cloning."
            (cd "$TARGET" && git fetch && git checkout "$BRANCH" && git pull --ff-only)
        else
            git clone --branch "$BRANCH" "$REPO_URL" "$TARGET"
        fi
        ok "Project ready at $TARGET (branch $BRANCH)"
        export REPO_PATH_OVERRIDE="$TARGET"
        export REPO_BRANCH_OVERRIDE="$BRANCH"
    else
        warn "Empty repo URL — skipping clone."
    fi
fi

# ─── Step 9: env.local wizard ───────────────────────────────────────
step "env.local wizard"
export ENV_FILE EXAMPLE_FILE NON_INTERACTIVE FORCE
export DEFAULT_JAVA_HOME="${JAVA_HOME:-$(/usr/libexec/java_home -v 17 2>/dev/null || echo /opt/homebrew/opt/openjdk@17)}"
export DEFAULT_ANDROID_SDK_ROOT="${ANDROID_SDK_ROOT:-$HOME/Library/Android/sdk}"
if [[ -n "${REPO_PATH_OVERRIDE:-}" ]]; then
    # Inject as defaults into the wizard via env vars that the example file documents.
    # The wizard reads its prompts with `ask`, so we let the user accept these defaults.
    sed -i.bak "s|^REPO_PATH=.*|REPO_PATH=$REPO_PATH_OVERRIDE|" "$EXAMPLE_FILE" 2>/dev/null || true
fi
# shellcheck source=common/env-wizard.sh
source "$HERE/common/env-wizard.sh"

# ─── Step 10: dotnet restore + build ─────────────────────────────────
step "dotnet restore + build"
if command -v dotnet >/dev/null 2>&1; then
    (cd "$PROJECT_ROOT" && dotnet restore && dotnet build -c Release)
    ok "Bot compiled."
else
    warn "dotnet CLI not found — re-run after .NET installs, or use --skip-deps."
fi

# ─── Step 11: summary ───────────────────────────────────────────────
step "Done"
cat <<EOF

  Run the bot with:
    cd "$PROJECT_ROOT"
    dotnet run --project src/BuildChatBot.csproj

  Config: $ENV_FILE
  Logs:   $PROJECT_ROOT/logs/

  Don't forget to fill TELEGRAM_BOT_TOKEN / TELEGRAM_API_ID / TELEGRAM_API_HASH if left blank.

  For iOS releases (/release_ios_testflight, /release_ios_appstore) you also need the
  APPLE_* keys in env.local — see install/common/env.local.example.
  Setting up a headless Mac mini as the build server: install/macos/MAC_MINI_SETUP.md
EOF
