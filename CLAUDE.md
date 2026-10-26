# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

BuildChatBot: a .NET 8 console app (single project, `src/BuildChatBot.csproj`) that runs a Telegram bot
against one configured local Flutter repo. `/test_build_android` pulls the repo, runs
`flutter build apk --release --dart-define-from-file=release.env`, verifies the signature with `apksigner`, and sends the APK back to the
chat. `/release_ios_testflight` and `/release_ios_appstore` drive the iOS release: they run the Flutter
repo's own `tool/release.sh`, upload the IPA with `xcrun altool`, and then talk to App Store Connect
(processing, export compliance, version + review submission). `/release_android_play` runs the same
script with `--platform android` and puts the `.aab` on a Google Play testing track (default `internal`)
through the Play Developer API. There are no tests in this repo.

## Commands

```bash
dotnet restore
dotnet build -c Release                     # from repo root (solution) or src/
dotnet run --project src/BuildChatBot.csproj
dotnet run --project src/BuildChatBot.csproj -- --env /abs/path/env.local
```

Installer (installs toolchain, runs the `env.local` wizard, then `dotnet build -c Release`):

```bash
bash install.sh                 # macOS/Linux dispatcher -> install/install-<os>.sh
bash install.sh --skip-deps     # only re-run env wizard + build
bash install.sh --force         # overwrite env.local
```

Manual smoke test (no automated tests exist): run the bot, send `/test_build_android` in Telegram,
expect 🚀 → 📥 → 🛠 → 🔐 → 📤 → ✅. Send it again and expect the ♻️ cached path. Set
`TELEGRAM_LARGE_FILE_THRESHOLD_MB=1` to force the WTelegramBot upload path.

Release smoke test (macOS + Xcode only, and it really ships a build): send
`/release_ios_testflight`, answer `Keep` then `Next`, confirm, and follow
`logs/release-<buildId>.log`; the build must land in TestFlight. Cheaper checks that do not ship
anything: `/cancel` at the first question must clear the pending prompt; removing an `APPLE_*` key
from `env.local` must make the command refuse and name that key; `tool/release.sh --dry-run` in the
Flutter repo exercises the preflight without building. For Google Play, `/release_android_play` with the
key set and answered `Yes` runs the access check first: a wrong package name or a service account
without Play Console rights must fail there ("nothing was built"), before the build slot is taken.

## Configuration model

Config precedence, highest first: process env vars (populated from `env.local` by DotNetEnv at
startup) → optional `"Bot"` section in `appsettings.json` (not shipped) → C# property
initializers in `src/Config/BotConfig.cs`. **BotConfig.cs is the single source of truth for
defaults.** `appsettings.json` only holds Serilog config.

`Program.cs` finds `env.local` by walking up from both the CWD and the assembly directory, so
`dotnet run` works from any folder. `BotConfig.Validate()` throws at startup if the five
required keys (`TELEGRAM_BOT_TOKEN`, `TELEGRAM_API_ID`, `TELEGRAM_API_HASH`, `REPO_PATH`,
`REPO_BRANCH`) are missing.

Release keys: `RELEASE_SCRIPT_PATH` (default `tool/release.sh`, resolved against `RepoPath` by
`ResolvedReleaseScriptPath`), `RELEASE_PROMPT_TIMEOUT_SECONDS`, `RELEASE_COMMIT_VERSION_BUMP`,
`RELEASE_GIT_AUTHOR_NAME`/`_EMAIL`.

Apple keys (`APPLE_API_KEY_ID`, `APPLE_API_ISSUER_ID`, `APPLE_API_KEY_PATH`, `APPLE_BUNDLE_ID`,
plus `APPLE_PROCESSING_TIMEOUT_SECONDS`, `APPLE_PROCESSING_POLL_SECONDS`,
`APPLE_SET_EXPORT_COMPLIANCE_EXEMPT`) are **not** checked by `Validate()` — the bot must boot on an
Android-only machine. They are validated lazily: `BotConfig.AppleConfigured` gates the release
commands and `BotConfig.MissingAppleKeys()` produces the list the bot sends back to the chat
(it also flags an `APPLE_API_KEY_PATH` that points at a nonexistent file). Keep that pattern for any
future optional-credential group. The Google Play keys follow it: `GOOGLE_PLAY_SERVICE_ACCOUNT_JSON`
(the service account JSON key; `GooglePlayConfigured` / `MissingGooglePlayKeys()`, which also flags a
missing file), `GOOGLE_PLAY_PACKAGE_NAME` (default `com.airexys.app`), `GOOGLE_PLAY_TRACK` (default
`internal`), `GOOGLE_PLAY_UPLOAD_TIMEOUT_SECONDS`.

When adding a config knob, touch all four places: property + `Env*()` line in `BotConfig.cs`,
the documented key in `install/common/env.local.example`, and (if required) the wizard in
`install/common/env-wizard.sh` and `install/install-windows.ps1`.

`env.local` contains real secrets. It is gitignored; never commit it, and never let it into a
Docker build context (`.dockerignore` is an allowlist for that reason).

## Architecture

Everything is wired as singletons in `Program.cs` via `Microsoft.Extensions.Hosting`.
`TelegramPollingService` (in `Program.cs`) is the only hosted service: it publishes the
slash-command list, then starts long polling with `UpdateRouter` as the handler.

**Request flow:** `UpdateRouter` (parses `/command`, strips `@botname`) → for `/test_build_android`
fires `BuildOrchestrator.RunAsync` with `Task.Run` so polling is never blocked → orchestrator
drives the pipeline and reports progress by editing a single status message. `UpdateRouter` also
routes plain text messages and `CallbackQuery` updates into `ReleaseConversation` when that chat has
a pending release question.

**Build pipeline** (`src/Build/`), in order inside `BuildOrchestrator.RunAsync`:
1. `BuildQueue` — single-slot `SemaphoreSlim`; callers waiting get a "Queued, position N" edit.
2. `GitService` — shells out to the system `git` binary for fetch/checkout/pull `--ff-only`
   (so SSH agents and credential helpers work); uses LibGit2Sharp only to read HEAD SHA.
   Honors `REPO_SSH_KEY_PATH` via `GIT_SSH_COMMAND`.
3. `FlutterRunner.Plan()` — the build args (incl. `--dart-define-from-file` from
   `FLUTTER_DART_DEFINE_FILE`, default `release.env`; missing file = build refused, because the
   app's release mode refuses to start without those defines) and a cache tag hashing args +
   define file. `ApkCache` — if HEAD did not move and `cache/apks/app-<sha>-<tag>.apk` exists,
   skip the build. Old `app-<sha>.apk` files (pre-2026-10-19, built without defines) never match.
4. `FlutterRunner` — `flutter clean` → `flutter pub get` → `flutter build apk <plan args>`,
   streaming stdout/stderr to `logs/build-<id>.log`; on failure returns the last 30 lines.
   Passes `JAVA_HOME`/`ANDROID_SDK_ROOT` into the child env.
5. `ApkSigner` — `apksigner verify --verbose --print-certs`; flags `CN=Android Debug` (Gradle's
   fallback without `android/key.properties`) as a chat warning + caption line; resolves the binary from `APKSIGNER_PATH`, then
   newest `build-tools/*`, then PATH.
6. `ArtifactNamer` — reads `version:` from the repo's `pubspec.yaml`, names the file
   `app-<flavor|main>-<name>+<code>-<sha7>.apk`.
7. `ITelegramSender.SendDocumentAsync` — upload.
8. `GradleCleaner` — every N successful non-cached builds runs `./gradlew clean`; counter is
   persisted in `logs/.build-counter`.
9. `BuildLogStore` — appends one JSON line per build to `logs/builds.jsonl`, in `finally`.

**Release pipeline** (`src/Release/`, `src/Apple/`, plus `src/Build/ReleaseScriptRunner.cs`):

- `ReleaseConversation` holds at most one pending prompt per chat (version → build number →
  Yes/No confirm), expires it after `RELEASE_PROMPT_TIMEOUT_SECONDS`, and is what `/cancel` clears.
  Answers arrive either as text or as inline-keyboard `CallbackQuery` updates (build picker,
  Yes/No), both routed from `UpdateRouter`.
- `ReleaseOrchestrator` runs the flows after confirmation (TestFlight and Play share
  `AskVersionAndBuildAsync`, `PullAndBuildAsync` and `CommitVersionBumpAsync`): TestFlight (pull → script → altool
  upload → commit/push pubspec → poll processing → export compliance) and App Store (list processed
  builds → create/reuse the `appStoreVersion` with `releaseType=AFTER_APPROVAL` → attach the build →
  `reviewSubmissions`).
- `IReleaseScriptRunner` / `ReleaseScriptRunner` shells out to the Flutter repo's `tool/release.sh`
  with `--platform ios --version X.Y.Z --build-number N`, streams everything to
  `logs/release-<buildId>.log`, and parses the script's `==>` step lines to drive the chat status
  message.
- `IIpaUploader` / `AltoolIpaUploader` runs `xcrun altool --upload-app` with the API key.
- `IGooglePlayClient` / `GooglePlayClient` (`src/GooglePlay/`) is the Play Developer API v3 client:
  one edit per release (insert → `bundles` media upload → `tracks.update` with a `completed` release →
  `commit`). `CheckAccessAsync` opens and deletes an edit — `/release_android_play` calls it **before**
  taking the build slot, so a bad key/permission/package fails before a version bump is spent. Two
  automatic retries: a never-published ("draft") app only accepts `status: draft`, so the release is
  retried as a draft and the chat is told to roll it out once by hand; a commit that asks for
  `changesNotSentForReview=true` is retried with it. `GoogleServiceAccountToken` does the OAuth
  JWT-bearer grant by hand (RS256 from the JSON's `private_key`) instead of adding Google.Apis.*.
  The API cannot make an app's first upload; that one is manual in Play Console.
- `IAppStoreConnectClient` / `AppStoreConnectClient` is the App Store Connect REST client;
  `AscJwtFactory` signs the ES256 JWT from the `.p8` key, `AscJson` holds the DTOs.

**The release script, not C#, owns the build rules.** Preflight (release.env hosts + `/health`,
Maps keys, keystore, signing identity), the `pubspec.yaml` version bump and the clean/DerivedData
logic all live in the Flutter repo's `tool/release.sh` — read its header comment for why
(`pubspec.yaml` is the single source of truth for the version on both platforms, and a hand-typed
build once shipped a broken TestFlight release). So a change to how releases are built goes into
`tool/release.sh` in the Flutter repo; the bot only passes flags and reads back the output. Do not
reimplement any of it in C#.

Two rules the orchestrator must keep: on a failed script run the `pubspec.yaml` bump is **reverted**
(`git checkout -- pubspec.yaml`) so the next attempt starts clean; on success the bump is committed
and pushed as `release: X.Y.Z+N (iOS TestFlight)` (or `(Android Play internal)`) unless `RELEASE_COMMIT_VERSION_BUMP=false` — which
means the bot's git credentials need **push** rights. The IPA is never sent to chat.

iOS and Android releases take the **same single `BuildQueue` slot** as `/test_build_android`, so a test build and
a release never run concurrently on the machine (Gradle/Xcode caches and the pubspec would collide).
`BuildLogStore` records both: each `logs/builds.jsonl` line carries a `Kind` of `test_apk`,
`ios_testflight`, `ios_appstore` or `android_play`.

**Telegram layer** (`src/Telegram/`): `HybridTelegramSender` implements `ITelegramSender`.
Text and files under `TELEGRAM_LARGE_FILE_THRESHOLD_MB` go through `Telegram.Bot`; larger
files lazily create a `WTelegram.Bot` (MTProto, 2 GB limit) whose session is stored in
`logs/wt-session.sqlite`. `ProgressReporter` edits the status message every
`HEARTBEAT_SECONDS` with the current label plus elapsed time.

`BotCommandRegistrar.Commands` must be kept in sync with the `switch` in `UpdateRouter`
when adding or renaming a command.

## Docker

`Dockerfile` + `docker-compose.yml` + `docker/` run the bot in a `linux/amd64` container for
**Android test builds only** (the iOS commands already refuse on non-macOS in
`ReleaseOrchestrator`). Guide: `docker/README.md`. How it fits the config model:

- The host's `env.local` is mounted at `/config/env.local`. `docker/entrypoint.sh` writes
  `/tmp/env.container` = that file + container overrides appended (`REPO_PATH=/repo`,
  `FLUTTER_BIN`, `JAVA_HOME`, `ANDROID_SDK_ROOT`, `LOG_DIR=/data/logs`, …) and starts the bot
  with `--env` on it. DotNetEnv applies lines in order, so the appended lines win and an empty
  value unsets the key. No C# change was needed; keep the bot unaware of Docker.
- A new config key that holds a **host path** must also get a container override in
  `entrypoint.sh`, or the container will try to use the host path. `GOOGLE_PLAY_SERVICE_ACCOUNT_JSON`
  is blanked there unless the file exists inside the container.
- The entrypoint also copies `/ssh` → `~/.ssh` (mode 600, `IgnoreUnknown UseKeychain`),
  clones `REPO_CLONE_URL` into an empty `/repo`, and copies `/repo-overlay` (gitignored files
  like `android/key.properties` + keystore) over the repo. Host-side mount settings live in a
  compose `.env` (`docker/compose.env.example`), not in `env.local`.
- Runtime state is the `/data` volume (WORKDIR); Gradle and pub caches are their own volumes.
- Toolchain versions are build args (`FLUTTER_VERSION`, `ANDROID_PLATFORM`, `ANDROID_NDK`, …);
  keep `FLUTTER_VERSION` equal to the Flutter version the app repo uses.

## Conventions worth knowing

- External tools (`git`, `flutter`, `apksigner`, `gradlew`, `tool/release.sh`, `xcrun altool`) are
  invoked via `ProcessStartInfo` with `ArgumentList`, never via a shell string. Keep it that way —
  version strings and paths come from chat input.
- Failure messages sent to chat are trimmed to fit Telegram's ~4096-char limit (3500–3800 chars).
- Runtime state lives under the working directory: `logs/`, `cache/apks/`, `tmp/`. All are
  gitignored except `cache/` and `tmp/`, which are created only at runtime. Release logs are
  `logs/release-<buildId>.log`; release artifacts land in `dist/<version>+<build>/` **inside the
  Flutter repo** (the script creates them), not under this repo.
- macOS deployment: the bot runs as a launchd **LaunchAgent**, never a LaunchDaemon — code signing
  needs the login keychain of a logged-in user. Template + checklist in `install/macos/`.
