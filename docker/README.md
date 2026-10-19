# Running BuildChatBot in Docker

The container runs the **Android test build** (`/test_build_android`) only. The iOS
commands (`/release_ios_testflight`, `/release_ios_appstore`) need macOS and Xcode; in the
container they answer that they cannot run. Keep iOS releases on the Mac
([`install/macos/MAC_MINI_SETUP.md`](../install/macos/MAC_MINI_SETUP.md)).

The image has .NET 8, OpenJDK 17, Flutter and the Android SDK. You do not install any of
them on the host.

## Before you start

- **One bot, one place.** Telegram allows only one poller per bot token. Stop the bot on
  the host (or the launchd agent) before you start the container, or use a second bot token.
- **Memory.** The Flutter app sets `org.gradle.jvmargs=-Xmx8G`. Give Docker at least
  10–12 GB of memory (Docker Desktop → Settings → Resources).
- **CPU type.** The Android build tools exist for x86_64 Linux only, so the image is
  `linux/amd64`. On a Linux x86_64 server it runs at full speed. On Apple Silicon Docker
  Desktop emulates it: it works, but builds are much slower.
- **A separate clone.** The bot runs `git checkout`, `git pull`, `flutter clean` and
  rewrites `android/local.properties` in the repo. Do not point it at the folder you work in.

## Quick start (docker compose)

```bash
cp docker/compose.env.example .env      # then edit BOT_REPO_DIR (and the rest)
mkdir -p ../flutter-repo-for-bot        # the folder BOT_REPO_DIR points to
docker compose up -d --build
docker compose logs -f bot
```

Then send `/test_build_android` in Telegram. The first build is slow (Gradle and pub
download everything); later builds use the cache volumes.

### What you configure

| Where | What |
| --- | --- |
| `env.local` | The bot settings, the **same file** as on the host. Host paths in it (`REPO_PATH`, `FLUTTER_BIN`, `JAVA_HOME`, `ANDROID_SDK_ROOT`, `LOG_DIR`, …) are replaced with container paths at start, so you do not change it. |
| `.env` (from `docker/compose.env.example`) | Host folders for the mounts: repo, SSH, overlay; your UID/GID; Flutter version. |

### Mounts

| Container path | Host source | Why |
| --- | --- | --- |
| `/config/env.local` | `BOT_ENV_LOCAL` (default `./env.local`) | bot settings, read-only |
| `/repo` | `BOT_REPO_DIR` | the Flutter repo |
| `/ssh` | `BOT_SSH_DIR` (default `~/.ssh`) | keys, `config`, `known_hosts`; copied to `~/.ssh` at start, read-only |
| `/repo-overlay` | `BOT_REPO_OVERLAY_DIR` (default `./docker/overlay`) | gitignored files copied over the repo at start |
| `/data` | volume `bot-data` | `logs/`, `cache/apks/`, `tmp/`, WTelegram session |
| `/home/bot/.gradle`, `/home/bot/.pub-cache` | volumes | build caches |

## The repo: mount or clone

- **Mount an existing clone:** set `BOT_REPO_DIR` to it. Its gitignored files
  (`android/key.properties`, the keystore, `android/local.properties`) are used as they are.
- **Clone on first start:** set `BOT_REPO_DIR` to an **empty** folder and `REPO_CLONE_URL`
  to the git URL. The start script clones it with `REPO_REMOTE_NAME` / `REPO_BRANCH` from
  `env.local`. The clone uses SSH without a terminal, so the key must have no passphrase or
  be the one `~/.ssh/config` picks. Later pulls are done by the bot as usual.

## Signing and other gitignored files (overlay)

A fresh clone has no release signing key and no Maps key. Put those files in the overlay
folder, **in the same layout as the repo**:

```
docker/overlay/
└── android/
    ├── key.properties               # storeFile=upload-keystore.jks, passwords, keyAlias
    ├── local.properties             # googleMapsApiKey=...  (Flutter adds sdk.dir itself)
    └── app/
        └── upload-keystore.jks
```

They are copied over the repo at every start. Put only **gitignored** files there; a
changed tracked file would block `git pull --ff-only`. `docker/overlay/` is gitignored in
this repo. Without `key.properties` the APK is signed with debug keys.

## SSH

The start script copies the mounted SSH folder to `~/.ssh` with mode 600, adds
`IgnoreUnknown UseKeychain` (a macOS-only option) to `config`, and rewrites
`/Users/<you>/.ssh/...` paths there to `~/.ssh/...`. So host aliases such as
`git@github.com-live:...` keep working. Two rules:

- The key files must be **inside** the mounted folder.
- `known_hosts` must already list the git host (connect once on the host:
  `ssh -T git@github.com`). The bot uses `BatchMode`, so it cannot accept a new host key.

`REPO_SSH_KEY_PATH` from `env.local` is kept when a file with the same name is in the SSH
folder; otherwise ssh uses `~/.ssh/config` and its default keys. `REPO_SSH_PASSPHRASE`
works as on the host.

## Plain `docker run`

```bash
docker build --platform linux/amd64 -t buildchatbot .
```

```bash
docker run -d --name buildchatbot --platform linux/amd64 --restart unless-stopped \
  -v "$PWD/env.local:/config/env.local:ro" \
  -v /path/to/flutter-repo-for-bot:/repo \
  -v "$HOME/.ssh:/ssh:ro" \
  -v "$PWD/docker/overlay:/repo-overlay:ro" \
  -v buildchatbot-data:/data \
  -v buildchatbot-gradle:/home/bot/.gradle \
  -v buildchatbot-pub:/home/bot/.pub-cache \
  buildchatbot
```

The image already uses `tini` as PID 1, so you do not need `--init`.

## Build arguments

| Arg | Default | Notes |
| --- | --- | --- |
| `FLUTTER_VERSION` | `3.41.1` | keep it equal to the host `flutter --version` |
| `ANDROID_PLATFORM` | `android-36` | Flutter's default `compileSdk` |
| `ANDROID_BUILD_TOOLS` | `35.0.0` | gives `apksigner` |
| `ANDROID_NDK` | `28.2.13676358` | Flutter's default `ndkVersion`; empty skips it |
| `ANDROID_CMDLINE_TOOLS` | `11076708` | Android command-line tools build |
| `BOT_UID` / `BOT_GID` | `1000` | on Linux use `id -u` / `id -g` so repo files stay yours |

Gradle downloads any other SDK part the app asks for on the first build. Those downloads
live in the container: when `docker compose up --build` recreates it, they are downloaded
again. Add them to the `sdkmanager` line in the `Dockerfile` if that is too slow.

## Troubleshooting

- `env.local is not mounted` — the `BOT_ENV_LOCAL` path is wrong.
- `/repo is not a Git repository` — `BOT_REPO_DIR` is empty and `REPO_CLONE_URL` is not set.
- `Host key verification failed` — add the host to `known_hosts` in the SSH folder.
- `Permission denied` writing into `/repo` (Linux) — rebuild with your `BOT_UID`/`BOT_GID`.
- Telegram `409 Conflict` — another copy of the bot is polling with the same token.
- Gradle killed / `Daemon disappeared` — Docker needs more memory.
- Logs: `docker compose logs -f bot`; per-build logs are in the `bot-data` volume
  (`docker compose exec bot ls /data/logs`).
