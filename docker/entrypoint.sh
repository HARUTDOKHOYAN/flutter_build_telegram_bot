#!/usr/bin/env bash
# Container start-up for BuildChatBot (guide: docker/README.md).
#
#   1. Copies the mounted SSH folder (/ssh) into ~/.ssh with the strict modes ssh wants.
#   2. Clones REPO_CLONE_URL into /repo when /repo is still empty.
#   3. Copies the overlay folder (/repo-overlay) over the repo: gitignored files the build
#      needs, such as android/key.properties, the upload keystore and android/local.properties.
#   4. Writes the container env file: the mounted env.local, then the container paths. DotNetEnv
#      applies the lines in order, so the container paths win over the host paths in env.local
#      (which cannot work in here). The same env.local serves the host and the container.
#   5. Starts the bot.
set -euo pipefail

ENV_LOCAL=/config/env.local
RUNTIME_ENV=/tmp/env.container
REPO_DIR=/repo
SSH_SRC=/ssh
OVERLAY=/repo-overlay

log() { printf '[entrypoint] %s\n' "$*"; }
die() { printf '[entrypoint] ERROR: %s\n' "$*" >&2; exit 1; }

# Last value of KEY in env.local, without quotes or a trailing comment. Empty when missing.
env_value() {
    sed -nE "s/^[[:space:]]*(export[[:space:]]+)?$1[[:space:]]*=[[:space:]]*//p" "$ENV_LOCAL" \
        | tail -n 1 \
        | tr -d '\r' \
        | sed -E 's/[[:space:]]+#.*$//; s/^"(.*)"$/\1/; s/^'\''(.*)'\''$/\1/'
}

[ -f "$ENV_LOCAL" ] || die "env.local is not mounted at $ENV_LOCAL (docker run: -v \"\$PWD/env.local:$ENV_LOCAL:ro\")."
[ -w /data ] || die "/data is not writable by uid $(id -u). Use a named volume, or chown the host folder."

# --- 1. SSH -----------------------------------------------------------------------------
if [ -d "$SSH_SRC" ] && [ -n "$(ls -A "$SSH_SRC" 2>/dev/null)" ]; then
    mkdir -p "$HOME/.ssh"
    # Agent sockets and other special files cannot be copied; the regular files are enough.
    cp -RL "$SSH_SRC"/. "$HOME/.ssh/" 2>/dev/null || true
    find "$HOME/.ssh" -type d -exec chmod 700 {} +
    find "$HOME/.ssh" -type f -exec chmod 600 {} +

    if [ -f "$HOME/.ssh/config" ]; then
        # Host paths to keys become ~/.ssh/ paths, and the macOS-only UseKeychain option
        # (Linux ssh stops with "Bad configuration option") is ignored.
        {
            grep -qiE '^[[:space:]]*IgnoreUnknown' "$HOME/.ssh/config" || echo 'IgnoreUnknown UseKeychain'
            sed -E 's#(/Users|/home)/[^/[:space:]]+/\.ssh/#~/.ssh/#g' "$HOME/.ssh/config"
        } > "$HOME/.ssh/config.tmp"
        mv "$HOME/.ssh/config.tmp" "$HOME/.ssh/config"
        chmod 600 "$HOME/.ssh/config"
    fi
    log "SSH folder copied ($(find "$HOME/.ssh" -type f | wc -l | tr -d ' ') files)."
else
    log "No SSH folder mounted at $SSH_SRC; only public HTTPS remotes will work."
fi

# --- 2. Repository ----------------------------------------------------------------------
if [ ! -d "$REPO_DIR/.git" ]; then
    [ -n "${REPO_CLONE_URL:-}" ] \
        || die "$REPO_DIR is not a Git repository. Mount your Flutter repo there (BOT_REPO_DIR) or set REPO_CLONE_URL."
    [ -z "$(ls -A "$REPO_DIR" 2>/dev/null)" ] \
        || die "$REPO_DIR is not empty and not a Git repository; refusing to clone into it."

    remote="$(env_value REPO_REMOTE_NAME)"
    branch="$(env_value REPO_BRANCH)"
    log "Cloning $REPO_CLONE_URL into $REPO_DIR ..."
    GIT_TERMINAL_PROMPT=0 GIT_SSH_COMMAND="ssh -o BatchMode=yes" \
        git clone --origin "${remote:-origin}" ${branch:+--branch "$branch"} "$REPO_CLONE_URL" "$REPO_DIR"
fi

# git itself trusts every folder (system config); LibGit2Sharp reads the global config too.
git config --global --get-all safe.directory 2>/dev/null | grep -qxF "$REPO_DIR" \
    || git config --global --add safe.directory "$REPO_DIR"

# --- 3. Overlay -------------------------------------------------------------------------
if [ -d "$OVERLAY" ]; then
    copied=0
    while IFS= read -r -d '' rel; do
        rel="${rel#./}"
        mkdir -p "$REPO_DIR/$(dirname "$rel")"
        cp "$OVERLAY/$rel" "$REPO_DIR/$rel"
        log "overlay -> $rel"
        copied=$((copied + 1))
    done < <(cd "$OVERLAY" && find . -type f ! -name '.DS_Store' ! -name '.gitkeep' -print0)
    [ "$copied" -gt 0 ] || log "Overlay folder is empty; the APK is signed with debug keys if android/key.properties is missing."
fi

# --- 4. Container env file --------------------------------------------------------------
# Keep a configured SSH key or apksigner only when it exists in here.
ssh_key=""
host_key="$(env_value REPO_SSH_KEY_PATH)"
if [ -n "$host_key" ]; then
    if [ -f "$host_key" ]; then
        ssh_key="$host_key"
    elif [ -f "$HOME/.ssh/$(basename "$host_key")" ]; then
        ssh_key="$HOME/.ssh/$(basename "$host_key")"
    else
        log "REPO_SSH_KEY_PATH=$host_key is not in the mounted SSH folder; ssh uses its defaults."
    fi
fi
apksigner=""
host_signer="$(env_value APKSIGNER_PATH)"
if [ -n "$host_signer" ] && [ -f "$host_signer" ]; then apksigner="$host_signer"; fi
# The Play key is a host path too; blank it unless it is visible in here, so
# /release_android_play answers "not configured" instead of failing mid-release.
play_json=""
host_play="$(env_value GOOGLE_PLAY_SERVICE_ACCOUNT_JSON)"
if [ -n "$host_play" ] && [ -f "$host_play" ]; then play_json="$host_play"; fi

umask 077
{
    cat "$ENV_LOCAL"
    printf '\n\n# --- container overrides (docker/entrypoint.sh) ---\n'
    printf 'REPO_PATH=%s\n' "$REPO_DIR"
    printf 'FLUTTER_BIN=%s\n' "$FLUTTER_ROOT/bin/flutter"
    printf 'JAVA_HOME=%s\n' "$JAVA_HOME"
    printf 'ANDROID_SDK_ROOT=%s\n' "$ANDROID_SDK_ROOT"
    printf 'APKSIGNER_PATH=%s\n' "$apksigner"
    printf 'REPO_SSH_KEY_PATH=%s\n' "$ssh_key"
    printf 'GOOGLE_PLAY_SERVICE_ACCOUNT_JSON=%s\n' "$play_json"
    printf 'EXTRA_PATH=\n'
    printf 'LOG_DIR=/data/logs\n'
    printf 'BUILD_RECORD_PATH=/data/logs/builds.jsonl\n'
    printf 'APK_CACHE_DIR=/data/cache/apks\n'
    printf 'TEMP_DIR_BASE=/data/tmp\n'
} > "$RUNTIME_ENV"

log "$(flutter --version 2>/dev/null | head -n 1)"
log "$(java -version 2>&1 | head -n 1)"
log "iOS release commands are not available in this container (they need macOS + Xcode)."

# --- 5. Start ---------------------------------------------------------------------------
exec dotnet /app/BuildChatBot.dll --env "$RUNTIME_ENV" "$@"
