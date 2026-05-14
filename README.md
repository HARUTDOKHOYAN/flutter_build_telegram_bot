# BuildChatBot

Telegram bot that builds a Flutter Android APK from a configured local repository on demand and
delivers the signed artifact back to the requester. Built on .NET 8 with `Telegram.Bot` for the
standard Bot API and `WTelegramBot` for >50 MB uploads (up to 2 GB).

Driven by the spec in
[`AirCaseNots/ChatBot for Android build.md`](../AirCaseNots/ChatBot%20for%20Android%20build.md).

## Quick start

### macOS / Linux
```bash
bash install.sh
# answer the prompts; the script installs dependencies and writes env.local
dotnet run --project src/BuildChatBot
```

### Windows (PowerShell)
```powershell
powershell -ExecutionPolicy Bypass -File install.ps1
dotnet run --project src\BuildChatBot
```

Installer flags (all platforms):
| Flag | Effect |
| --- | --- |
| `--non-interactive` / `-NonInteractive` | accept all defaults |
| `--skip-deps` / `-SkipDeps` | skip toolchain install; only re-run env wizard + dotnet build |
| `--force` / `-Force` | overwrite existing `env.local` |
| `--with-ndk` / `-WithNdk` | also install Android NDK |

The installer:
1. Installs **.NET 8 SDK, OpenJDK 17, Git, Flutter, Android command-line tools**
   (Homebrew on macOS, apt/dnf/pacman on Linux, winget on Windows).
2. Pulls Android SDK packages (`platform-tools`, `platforms;android-35`, `build-tools;35.0.0`).
3. Optionally generates an SSH key, prints the public key, and copies it to your clipboard
   (`pbcopy` / `xclip` / `Set-Clipboard`).
4. Optionally clones a fresh Flutter repo into a target folder.
5. Runs an interactive `env.local` wizard covering every knob the bot exposes.
6. `dotnet restore` + `dotnet build -c Release`.

## Configuration

Everything is driven by `env.local` (loaded via `DotNetEnv`) with documented defaults in
[`install/common/env.local.example`](install/common/env.local.example). Only five keys are
strictly required:

- `TELEGRAM_BOT_TOKEN`
- `TELEGRAM_API_ID`
- `TELEGRAM_API_HASH`
- `REPO_PATH`
- `REPO_BRANCH`

All other keys (Flutter flavor, target file, target platform, threshold, queue size, heartbeat,
cache paths, Gradle clean cadence, SSH credentials, Java/Android paths, etc.) fall back to
sensible defaults baked into [`appsettings.json`](src/BuildChatBot/appsettings.json). Override
any of them by adding the matching variable to `env.local`.

## Telegram setup

1. **Bot token** — talk to [@BotFather](https://t.me/BotFather) → `/newbot`.
2. **API ID + Hash** — log in at [my.telegram.org](https://my.telegram.org) → "API development
   tools" → create an application. Required by `WTelegramBot` to bypass the 50 MB Bot API
   upload limit.

## Commands

| Command | Effect |
| --- | --- |
| `/build_android` | Pull configured branch, build release APK, send it back |
| `/status` | Show whether a build is currently running |
| `/version` | Show bot version + currently-configured branch/flavor/platform |
| `/help` | Brief usage |

## How a build runs

1. Acknowledge in chat and acquire the single-slot build semaphore (queue position reported if
   contended).
2. `LibGit2Sharp` checks out and fast-forwards `REPO_BRANCH`.
3. If the branch HEAD hadn't moved and a cached APK exists for that commit SHA, the bot
   re-sends the cached file.
4. Otherwise: `flutter clean` → `flutter pub get` → `flutter build apk --release
   --target-platform=<configured>` (plus `--flavor` / `-t` / `FLUTTER_EXTRA_ARGS`).
5. `apksigner verify --verbose` confirms the APK is signed.
6. The APK is sent via the Bot API when under the threshold, otherwise routed through
   WTelegramBot.
7. Every N builds the bot runs `./gradlew clean` to free disk space.
8. Each build appends a JSON line to `logs/builds.jsonl` (chat id, branch, sha, version,
   duration, size, success).

## Project layout

```
BuildChatBot/
├── install.sh            # dispatcher (macOS/Linux)
├── install.ps1           # dispatcher (Windows)
├── install/
│   ├── install-macos.sh
│   ├── install-linux.sh
│   ├── install-windows.ps1
│   └── common/
│       ├── env-wizard.sh
│       └── env.local.example
└── src/BuildChatBot/
    ├── Program.cs
    ├── Config/BotConfig.cs
    ├── Telegram/
    │   ├── ITelegramSender.cs
    │   ├── HybridTelegramSender.cs
    │   ├── UpdateRouter.cs
    │   └── ProgressReporter.cs
    ├── Build/
    │   ├── BuildOrchestrator.cs
    │   ├── BuildQueue.cs
    │   ├── GitService.cs
    │   ├── FlutterRunner.cs
    │   ├── ApkSigner.cs
    │   ├── ApkCache.cs
    │   ├── ArtifactNamer.cs
    │   └── GradleCleaner.cs
    └── Persistence/BuildLogStore.cs
```

## Verifying

After install, smoke-test end-to-end:

```bash
dotnet run --project src/BuildChatBot
# in Telegram, send: /build_android
# expect: 🚀 → 📥 → 🛠 → ✅ → APK delivered
# send /build_android again — expect ♻️ cached response
```

Failure paths to confirm:
- Break `pubspec.yaml` → bot reports last 30 log lines and releases the lock.
- Set `TELEGRAM_LARGE_FILE_THRESHOLD_MB=1` → forces the WTelegramBot path.
- Send two `/build_android` commands quickly → second receives "Queued, position 1".

Build records land in `logs/builds.jsonl`; per-build Flutter logs in `logs/build-<id>.log`.
