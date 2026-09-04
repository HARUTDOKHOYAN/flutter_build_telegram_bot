# Mac mini build server setup

Checklist for turning a headless Mac mini into the machine that runs BuildChatBot and
builds iOS releases. Do the steps in order. You need physical access (or Screen Sharing)
for the Xcode and keychain steps — they cannot be done over SSH.

Words used below: **bot repo** = this repository. **Flutter repo** = the app repo that
`REPO_PATH` points at.

## 1. Install the toolchain

```bash
cd /path/to/flutter_build_telegram_bot
bash install.sh
```

This installs .NET 8, JDK 17, Git, Flutter, the Android SDK and CocoaPods, checks the iOS
toolchain, runs the `env.local` wizard, and builds the bot.

## 2. Install Xcode

1. Install Xcode from the App Store. The Command Line Tools are not enough.
2. Point the tools at it and finish first launch:

```bash
sudo xcode-select -s /Applications/Xcode.app/Contents/Developer
sudo xcodebuild -license accept
sudo xcodebuild -runFirstLaunch
xcodebuild -version   # should print a version, not an error
```

## 3. Let Xcode sign the app

The Flutter project uses **automatic signing** for team `QNL62XBD46`. Xcode needs an
account so it can fetch the provisioning profile. Do one of these:

- Xcode → Settings → Accounts → `+` → **Apple ID**, sign in with an account that is in the
  team, or
- Xcode → Settings → Accounts → `+` → **App Store Connect API Key**, and add the same key
  you put in `env.local`.

Then open Xcode → Settings → Accounts → Manage Certificates and confirm an **Apple
Distribution** certificate is listed.

## 4. Import the distribution certificate

If the certificate was created on another Mac, export it there as a `.p12` (certificate
**plus** private key) and on the mini:

1. Copy the `.p12` over and double-click it. Import it into the **login** keychain.
2. Type the `.p12` password.
3. Check it landed:

```bash
security find-identity -v -p codesigning | grep "Apple Distribution"
```

If that prints nothing, signing will fail. Do not continue until it prints a line.

## 5. Keep the keychain unlocked

Code signing reads the private key from the login keychain. A locked keychain, or no login
session at all, breaks every iOS build. Set all three:

1. **Automatic login** — System Settings → Users & Groups → Automatically log in as →
   the build user. (Requires FileVault to be off.)
2. **No keychain lock timeout**:

   ```bash
   security set-keychain-settings ~/Library/Keychains/login.keychain-db
   ```

   With no `-t` and no `-l`, the keychain never locks on a timer or on sleep.
3. **No screen-lock password** — System Settings → Lock Screen → "Require password after
   screensaver begins" → Never, and turn off the screen saver / display sleep lock.

Also set Energy: System Settings → Energy → "Start up automatically after a power failure"
on, and "Prevent automatic sleeping" on.

## 6. Copy the gitignored secret files

The release script refuses to build without these. They are not in git — copy them from a
developer machine or your password manager into the **Flutter repo**:

| File | What it holds |
| --- | --- |
| `ios/Flutter/Secrets.xcconfig` | `GOOGLE_MAPS_API_KEY` for iOS map tiles |
| `android/local.properties` | `googleMapsApiKey` for Android map tiles |
| `android/key.properties` | Play upload signing config |
| the keystore file it names | the upload key itself |

`release.env` is committed — you do not need to copy it, but the script checks that both
hosts in it answer `GET /health`, so the mini needs network access to them.

## 7. Place the App Store Connect API key

Put the `.p8` **outside** any git repo, and keep Apple's file name:

```bash
mkdir -p ~/.appstoreconnect/private_keys
cp /path/to/AuthKey_ABCDE12345.p8 ~/.appstoreconnect/private_keys/
chmod 600 ~/.appstoreconnect/private_keys/AuthKey_ABCDE12345.p8
```

Then in the bot's `env.local`:

```
APPLE_API_KEY_ID=ABCDE12345
APPLE_API_ISSUER_ID=<uuid from App Store Connect>
APPLE_API_KEY_PATH=/Users/<builduser>/.appstoreconnect/private_keys/AuthKey_ABCDE12345.p8
APPLE_BUNDLE_ID=com.airexys.app
```

Create the key at App Store Connect → Users and Access → Integrations → App Store Connect
API → Team Keys → Generate, role **App Manager**. The `.p8` can be downloaded only once.

## 8. Git SSH key with push rights

After a successful release build the bot commits and pushes the `pubspec.yaml` version
bump, so a read-only deploy key is not enough.

```bash
ssh -T git@github.com          # must succeed
cd "$REPO_PATH" && git push --dry-run
```

Give the key write access on the repo, and point `REPO_SSH_KEY_PATH` at it in `env.local`
if it is not the default `~/.ssh/id_ed25519`.

## 9. Install the LaunchAgent

The bot runs as a **LaunchAgent** in the build user's login session, not as a
LaunchDaemon: a daemon has no access to the login keychain and every signing step fails.

```bash
cd /path/to/flutter_build_telegram_bot
mkdir -p ~/Library/LaunchAgents logs
sed -e "s|__BOT_DIR__|$PWD|g" -e "s|__HOME__|$HOME|g" \
    install/macos/com.airexys.buildchatbot.plist \
    > ~/Library/LaunchAgents/com.airexys.buildchatbot.plist
```

Open the copy and check two things: the absolute path to `dotnet` (`which dotnet`) and the
Flutter bin dir in `PATH` (`dirname "$(which flutter)"`). Then load it:

```bash
launchctl bootstrap gui/$(id -u) ~/Library/LaunchAgents/com.airexys.buildchatbot.plist
launchctl kickstart -k gui/$(id -u)/com.airexys.buildchatbot   # (re)start now
launchctl print gui/$(id -u)/com.airexys.buildchatbot | head -20
```

Logs:

```bash
tail -f logs/launchd.err.log            # startup problems
tail -f logs/build-*.log                # a running Android build
tail -f logs/release-*.log              # a running release
```

After `dotnet build -c Release`, restart the agent with the same `kickstart -k` line.
To remove it: `launchctl bootout gui/$(id -u)/com.airexys.buildchatbot`.

## 10. First-run test

1. In Telegram send `/status` — the bot should answer.
2. Send `/test_build_android`. Expect 🚀 → 📥 → 🛠 → 🔐 → 📤 → ✅ and an APK.
3. Send `/release_ios_testflight`. Answer with a small version bump (e.g. keep the
   marketing version and take the next build number) and confirm. Expect the build to
   reach TestFlight. Watch `logs/release-<id>.log` while it runs; the first iOS build on a
   fresh machine takes a long time (pods download, full compile).
4. Only after step 3 works, try `/release_ios_appstore`.

## Troubleshooting

| Symptom | Cause / fix |
| --- | --- |
| `errSecInternalComponent` during signing | The login keychain is locked, or there is no login session. Redo step 5 and make sure the build user is logged in (automatic login). |
| `No signing certificate "iOS Distribution" found` | The Apple Distribution certificate + private key are not in the **login** keychain of the build user. Redo step 4 as that user (not via `sudo`). |
| altool: `Unable to authenticate` / `NOT_AUTHORIZED` | Wrong `APPLE_API_KEY_ID` or `APPLE_API_ISSUER_ID`, or the file is not named `AuthKey_<KEYID>.p8`. The name matters — `xcrun altool` derives the key id from it. |
| The bot says which `APPLE_*` keys are missing | Those keys are absent from `env.local`. Add them (step 7) and restart the agent. |
| `flutter: command not found` in a release log | The `PATH` in the plist is wrong. Fix the Flutter bin dir and `kickstart -k`. |
| Preflight fails on `GET /health` | The backend host in `release.env` is unreachable from the mini, or moved. Fix the host — do not skip the check. |
| Preflight fails on `Secrets.xcconfig` / `local.properties` / `key.properties` | Step 6 was skipped. |
| Build number rejected | The number must be greater than the one in `pubspec.yaml`. Use "Next", or pick a higher number. |
