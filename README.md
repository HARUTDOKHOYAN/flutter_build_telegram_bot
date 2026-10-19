# BuildChatBot

Telegram bot that drives the mobile release process for a configured local Flutter
repository. It builds test APKs on demand and delivers the signed artifact back to the
requester, ships iOS builds to **TestFlight**, and submits a processed TestFlight build to
the **App Store** for review — all from chat. Built on .NET 8 with `Telegram.Bot` for the
standard Bot API and `WTelegramBot` for >50 MB uploads (up to 2 GB).

The iOS release build itself is run by the Flutter repo's own `tool/release.sh`, which
stays the single source of truth for preflight checks, the version bump and the clean
rules. The bot only passes flags to it, uploads the result, and talks to App Store Connect.

Driven by the spec in
[`AirCaseNots/ChatBot for Android build.md`](../AirCaseNots/ChatBot%20for%20Android%20build.md).

## Quick start

### macOS / Linux
```bash
bash install.sh
# answer the prompts; the script installs dependencies and writes env.local
dotnet run --project src/BuildChatBot.csproj
```

### Windows (PowerShell)
```powershell
powershell -ExecutionPolicy Bypass -File install.ps1
dotnet run --project src\BuildChatBot.csproj
```

### Docker (Android builds only)
```bash
cp docker/compose.env.example .env   # set BOT_REPO_DIR to a separate clone of the Flutter repo
docker compose up -d --build
```
The image brings .NET 8, JDK 17, Flutter and the Android SDK, and reads the same `env.local`
(host paths in it are replaced at start). The iOS commands do not work in the container.
Full guide: [`docker/README.md`](docker/README.md).

The iOS release commands need macOS, Xcode and `xcrun`. For a dedicated headless build
machine follow [`install/macos/MAC_MINI_SETUP.md`](install/macos/MAC_MINI_SETUP.md), which
also covers running the bot as a launchd LaunchAgent.

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
3. On macOS, checks the optional **iOS toolchain**: full Xcode, `xcodebuild`, CocoaPods and
   an `Apple Distribution` signing identity. Missing pieces warn with instructions instead
   of failing, so an Android-only machine still installs.
4. Optionally generates an SSH key, prints the public key, and copies it to your clipboard
   (`pbcopy` / `xclip` / `Set-Clipboard`).
5. Optionally clones a fresh Flutter repo into a target folder.
6. Runs an interactive `env.local` wizard covering the required keys plus the optional
   App Store Connect key.
7. `dotnet restore` + `dotnet build -c Release`.

## Configuration

Everything is driven by `env.local` (loaded via `DotNetEnv`) with documented defaults in
[`install/common/env.local.example`](install/common/env.local.example). Only five keys are
strictly required:

- `TELEGRAM_BOT_TOKEN`
- `TELEGRAM_API_ID`
- `TELEGRAM_API_HASH`
- `REPO_PATH`
- `REPO_BRANCH`

All other keys (Flutter flavor, target file, target platform, threshold, queue size,
heartbeat, cache paths, Gradle clean cadence, SSH credentials, Java/Android paths, etc.)
fall back to defaults baked into
[`src/Config/BotConfig.cs`](src/Config/BotConfig.cs) — that file is the single source of
truth for defaults. Override any of them by adding the matching variable to `env.local`.

### Release pipeline keys

| Key | Default | Meaning |
| --- | --- | --- |
| `RELEASE_SCRIPT_PATH` | `tool/release.sh` | Release script, relative to `REPO_PATH` (absolute also works) |
| `RELEASE_PROMPT_TIMEOUT_SECONDS` | `300` | How long a pending version / build-number / confirm question stays open |
| `RELEASE_COMMIT_VERSION_BUMP` | `true` | Commit + push `pubspec.yaml` after a successful release build |
| `RELEASE_GIT_AUTHOR_NAME` | `BuildChatBot` | Author of that commit |
| `RELEASE_GIT_AUTHOR_EMAIL` | `buildbot@local` | Author email of that commit |

### Apple / App Store Connect keys

Optional at startup — the bot boots without them. The `/release_ios_*` commands refuse to
run and list exactly which ones are missing.

| Key | Default | Meaning |
| --- | --- | --- |
| `APPLE_API_KEY_ID` | — | 10-character Key ID of the App Store Connect API key |
| `APPLE_API_ISSUER_ID` | — | Issuer ID (UUID) of the team |
| `APPLE_API_KEY_PATH` | — | Absolute path to `AuthKey_<KEYID>.p8` (keep Apple's file name) |
| `APPLE_BUNDLE_ID` | — | e.g. `com.airexys.app`; resolves the App Store Connect app id |
| `APPLE_PROCESSING_TIMEOUT_SECONDS` | `2400` | How long to wait for Apple to process an upload |
| `APPLE_PROCESSING_POLL_SECONDS` | `30` | Poll interval while waiting |
| `APPLE_SET_EXPORT_COMPLIANCE_EXEMPT` | `true` | Mark builds as using no non-exempt encryption, so TestFlight doesn't stall on "Missing Compliance" |

Create the key at App Store Connect → Users and Access → Integrations → App Store Connect
API → Team Keys → Generate, with the **App Manager** role. The `.p8` can be downloaded
only once; store it outside the repo (e.g. `~/.appstoreconnect/private_keys/`). Key ID and
Issuer ID are both shown on that page.

## Telegram setup

1. **Bot token** — talk to [@BotFather](https://t.me/BotFather) → `/newbot`.
2. **API ID + Hash** — log in at [my.telegram.org](https://my.telegram.org) → "API development
   tools" → create an application. Required by `WTelegramBot` to bypass the 50 MB Bot API
   upload limit.

## Commands

| Command | Effect |
| --- | --- |
| `/test_build_android` | Pull configured branch, build a test APK, verify the signature, send it back for side-load install |
| `/release_ios_testflight` | Ask for version + build number, build the IPA via `tool/release.sh`, upload it, wait for processing, report "in TestFlight" |
| `/release_ios_appstore` | List processed TestFlight builds, pick one, create/attach the App Store version and submit it for review |
| `/cancel` | Cancel a pending question (version, build number, confirmation) |
| `/status` | Show test-build state and release state |
| `/version` | Show bot version + currently-configured branch/flavor/platform |
| `/help` | Brief usage |

Questions asked by the release commands expire after `RELEASE_PROMPT_TIMEOUT_SECONDS`.
Android test builds and iOS release builds share the same single build slot, so only one
of them runs at a time.

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
   duration, size, success, `Kind: test_apk`).

## How a release runs

### `/release_ios_testflight`

1. The bot asks for the **marketing version** (`X.Y.Z`, or answer `Keep` to reuse the one in
   `pubspec.yaml`), then for the **build number** (a number, or `Next` to increment).
2. It shows what it is about to do and waits for a **Yes / No** confirmation.
3. Acquire the build slot, `git pull --ff-only` the configured branch.
4. Run `tool/release.sh --platform ios --version X.Y.Z --build-number N` in the Flutter
   repo. The script does the preflight checks, writes `version: X.Y.Z+N` into
   `pubspec.yaml`, cleans the caches, and runs
   `flutter build ipa --export-method app-store`. Its `==>` step lines are relayed to chat;
   the full output goes to `logs/release-<buildId>.log`. Artifacts land in
   `dist/<version>+<build>/` inside the Flutter repo.
   **If the script fails, the `pubspec.yaml` bump is reverted** so the next attempt starts
   from the same version.
5. Upload the IPA with `xcrun altool --upload-app`, authenticating with the App Store
   Connect API key. The IPA is not sent to chat — it cannot be side-loaded, so it is not
   useful there.
6. On success, commit and push `pubspec.yaml` with the message
   `release: X.Y.Z+N (iOS TestFlight)` (skip with `RELEASE_COMMIT_VERSION_BUMP=false`).
7. Poll App Store Connect every `APPLE_PROCESSING_POLL_SECONDS` until the build leaves
   `PROCESSING`, up to `APPLE_PROCESSING_TIMEOUT_SECONDS`.
8. Mark the build export-compliance exempt (unless
   `APPLE_SET_EXPORT_COMPLIANCE_EXEMPT=false`) and report that the build is in TestFlight.

### `/release_ios_appstore`

1. The bot lists the processed TestFlight builds as inline buttons.
2. You tap one, then confirm.
3. It creates the App Store version for that marketing version — or reuses the existing
   editable one — with `releaseType` `AFTER_APPROVAL`, so the app goes live automatically
   once Apple approves it.
4. It attaches the chosen build to that version.
5. It submits the version for review through the Review Submissions API and reports the
   result.

Apple rejects a submission when the store listing is incomplete (missing screenshots,
missing metadata, no privacy answers, and so on). Those errors are shown **verbatim** in
chat — fix them in App Store Connect and run the command again.

## Project layout

```
BuildChatBot/
├── Dockerfile            # Android-only image: .NET 8 + JDK 17 + Flutter + Android SDK
├── docker-compose.yml
├── docker/
│   ├── entrypoint.sh     # SSH copy, optional clone, overlay, container env file
│   ├── compose.env.example
│   └── README.md
├── install.sh            # dispatcher (macOS/Linux)
├── install.ps1           # dispatcher (Windows)
├── install/
│   ├── install-macos.sh
│   ├── install-linux.sh
│   ├── install-windows.ps1
│   ├── macos/
│   │   ├── com.airexys.buildchatbot.plist   # launchd LaunchAgent template
│   │   └── MAC_MINI_SETUP.md                # headless build server checklist
│   └── common/
│       ├── env-wizard.sh
│       └── env.local.example
└── src/
    ├── Program.cs
    ├── Config/BotConfig.cs
    ├── Telegram/
    │   ├── ITelegramSender.cs
    │   ├── HybridTelegramSender.cs
    │   ├── UpdateRouter.cs
    │   ├── BotCommandRegistrar.cs
    │   └── ProgressReporter.cs
    ├── Build/
    │   ├── BuildOrchestrator.cs
    │   ├── BuildQueue.cs
    │   ├── GitService.cs
    │   ├── FlutterRunner.cs
    │   ├── ApkSigner.cs
    │   ├── ApkCache.cs
    │   ├── ArtifactNamer.cs
    │   ├── GradleCleaner.cs
    │   ├── IReleaseScriptRunner.cs
    │   └── ReleaseScriptRunner.cs            # runs the Flutter repo's tool/release.sh
    ├── Release/
    │   ├── ReleaseOrchestrator.cs            # TestFlight + App Store flows
    │   └── ReleaseConversation.cs            # pending questions per chat
    ├── Apple/
    │   ├── IIpaUploader.cs
    │   ├── AltoolIpaUploader.cs              # xcrun altool --upload-app
    │   ├── IAppStoreConnectClient.cs
    │   ├── AppStoreConnectClient.cs          # builds / appStoreVersions / reviewSubmissions
    │   ├── AscJwtFactory.cs                  # ES256 JWT from the .p8 key
    │   └── AscJson.cs
    └── Persistence/BuildLogStore.cs
```

## Verifying

After install, smoke-test end-to-end:

```bash
dotnet run --project src/BuildChatBot.csproj
# in Telegram, send: /test_build_android
# expect: 🚀 → 📥 → 🛠 → ✅ → APK delivered
# send /test_build_android again — expect ♻️ cached response
```

Failure paths to confirm:
- Break `pubspec.yaml` → bot reports last 30 log lines and releases the lock.
- Set `TELEGRAM_LARGE_FILE_THRESHOLD_MB=1` → forces the WTelegramBot path.
- Send two `/test_build_android` commands quickly → second receives "Queued, position 1".
- Release flow: send `/release_ios_testflight`, answer `Keep` then `Next`, confirm, and
  watch `logs/release-<id>.log`; the build should appear in TestFlight. Then send it again
  and answer `/cancel` at the first question — the pending prompt must clear. Remove one
  `APPLE_*` key from `env.local` and restart → the command must refuse and name that key.

Build records land in `logs/builds.jsonl` (each line carries a `Kind` of `test_apk`,
`ios_testflight` or `ios_appstore`); per-build Flutter logs in `logs/build-<id>.log`;
per-release logs in `logs/release-<id>.log`. Release artifacts stay in
`dist/<version>+<build>/` inside the Flutter repo.
