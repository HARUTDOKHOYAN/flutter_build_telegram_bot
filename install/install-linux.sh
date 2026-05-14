#!/usr/bin/env bash
# BuildChatBot installer for Linux (apt/dnf/pacman auto-detected).
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
Usage: install-linux.sh [--non-interactive] [--skip-deps] [--force] [--with-ndk]
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

if [[ "$(uname -s)" != "Linux" ]]; then
    echo "This script is for Linux." >&2
    exit 1
fi

step() { printf '\n\033[1;34m▶ %s\033[0m\n' "$*"; }
ok()   { printf '  \033[1;32m✓\033[0m %s\n' "$*"; }
warn() { printf '  \033[1;33m!\033[0m %s\n' "$*"; }

prompt_yn() {
    local q="$1" default="${2:-N}" ans
    if [[ "$NON_INTERACTIVE" == "1" ]]; then
        [[ "$default" == "Y" ]]; return $?
    fi
    read -r -p "$q " ans </dev/tty || true
    ans="${ans:-$default}"
    [[ "$ans" =~ ^[Yy] ]]
}

SUDO=""
if [[ "$EUID" -ne 0 ]]; then SUDO="sudo"; fi

detect_pm() {
    if command -v apt-get >/dev/null 2>&1; then echo apt
    elif command -v dnf >/dev/null 2>&1; then echo dnf
    elif command -v pacman >/dev/null 2>&1; then echo pacman
    else echo "unknown"; fi
}

PM="$(detect_pm)"

step "Preflight"
ok "Linux detected — package manager: $PM"

if [[ "$SKIP_DEPS" == "1" ]]; then
    warn "--skip-deps set; jumping to env wizard."
else
    step "Core dependencies"
    case "$PM" in
        apt)
            $SUDO apt-get update
            $SUDO apt-get install -y \
                ca-certificates curl wget unzip git xclip xz-utils \
                openjdk-17-jdk dotnet-sdk-8.0 || true
            # If Microsoft repo not present, try install via packages-microsoft-prod
            if ! command -v dotnet >/dev/null 2>&1; then
                warn "dotnet not installed via apt; fetching install script."
                curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0 --install-dir /usr/share/dotnet
                $SUDO ln -sf /usr/share/dotnet/dotnet /usr/local/bin/dotnet
            fi
            ;;
        dnf)
            $SUDO dnf install -y dotnet-sdk-8.0 java-17-openjdk-devel git unzip wget xclip xz || true
            ;;
        pacman)
            $SUDO pacman -Sy --noconfirm dotnet-sdk jdk17-openjdk git unzip wget xclip xz || true
            ;;
        *)
            warn "Unknown package manager — please install dotnet-sdk-8, openjdk-17, git, unzip manually."
            ;;
    esac
    ok "dotnet: $(dotnet --version 2>/dev/null || echo 'missing')"
    ok "java:   $(java -version 2>&1 | head -n1)"
    ok "git:    $(git --version)"

    # Flutter (tarball under /opt/flutter)
    if ! command -v flutter >/dev/null 2>&1; then
        step "Installing Flutter (tarball)"
        FLUTTER_VERSION="${FLUTTER_VERSION:-3.24.5}"
        TMP="$(mktemp -d)"
        wget -qO "$TMP/flutter.tar.xz" \
            "https://storage.googleapis.com/flutter_infra_release/releases/stable/linux/flutter_linux_${FLUTTER_VERSION}-stable.tar.xz"
        $SUDO tar -xf "$TMP/flutter.tar.xz" -C /opt
        $SUDO chown -R "$USER":"$USER" /opt/flutter
        echo 'export PATH="/opt/flutter/bin:$PATH"' | $SUDO tee /etc/profile.d/buildchatbot-flutter.sh >/dev/null
        export PATH="/opt/flutter/bin:$PATH"
    fi
    ok "flutter: $(flutter --version 2>/dev/null | head -n1)"

    # Android command-line tools
    step "Android SDK packages"
    export ANDROID_SDK_ROOT="${ANDROID_SDK_ROOT:-$HOME/Android/Sdk}"
    mkdir -p "$ANDROID_SDK_ROOT/cmdline-tools"
    if [[ ! -d "$ANDROID_SDK_ROOT/cmdline-tools/latest" ]]; then
        TMP="$(mktemp -d)"
        wget -qO "$TMP/cmdline.zip" \
            "https://dl.google.com/android/repository/commandlinetools-linux-11076708_latest.zip"
        unzip -q "$TMP/cmdline.zip" -d "$TMP"
        mv "$TMP/cmdline-tools" "$ANDROID_SDK_ROOT/cmdline-tools/latest"
    fi
    SDKMANAGER="$ANDROID_SDK_ROOT/cmdline-tools/latest/bin/sdkmanager"
    yes 2>/dev/null | "$SDKMANAGER" --sdk_root="$ANDROID_SDK_ROOT" --licenses >/dev/null || true
    "$SDKMANAGER" --sdk_root="$ANDROID_SDK_ROOT" "platform-tools" "platforms;android-35" "build-tools;35.0.0"
    if [[ "$WITH_NDK" == "1" ]]; then
        "$SDKMANAGER" --sdk_root="$ANDROID_SDK_ROOT" "ndk;26.1.10909125"
    fi
    ok "SDK packages installed at $ANDROID_SDK_ROOT"

    step "flutter doctor"
    flutter doctor || true
fi

# Optional SSH setup
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
    if command -v xclip >/dev/null 2>&1; then
        xclip -selection clipboard < "$KEY_PATH.pub" && ok "Copied to clipboard (xclip)"
    fi
    echo ""
    echo "  → Paste it into GitHub / GitLab → SSH keys, then press ENTER."
    [[ "$NON_INTERACTIVE" == "1" ]] || read -r _ </dev/tty || true
fi

# Optional clone
step "Clone Git project (optional)"
if prompt_yn "Clone a Flutter project into a new folder now? [y/N]" "N"; then
    read -r -p "  Repo URL: " REPO_URL </dev/tty || REPO_URL=""
    DEFAULT_TARGET="$PROJECT_ROOT/../mobile_aplication"
    read -r -p "  Target folder [$DEFAULT_TARGET]: " TARGET </dev/tty || TARGET=""
    TARGET="${TARGET:-$DEFAULT_TARGET}"
    read -r -p "  Branch [master]: " BRANCH </dev/tty || BRANCH=""
    BRANCH="${BRANCH:-master}"
    if [[ -n "$REPO_URL" ]]; then
        mkdir -p "$(dirname "$TARGET")"
        if [[ -d "$TARGET/.git" ]]; then
            (cd "$TARGET" && git fetch && git checkout "$BRANCH" && git pull --ff-only)
        else
            git clone --branch "$BRANCH" "$REPO_URL" "$TARGET"
        fi
        export REPO_PATH_OVERRIDE="$TARGET"
        export REPO_BRANCH_OVERRIDE="$BRANCH"
        ok "Project ready at $TARGET"
    fi
fi

step "env.local wizard"
export ENV_FILE EXAMPLE_FILE NON_INTERACTIVE FORCE
export DEFAULT_JAVA_HOME="${JAVA_HOME:-/usr/lib/jvm/java-17-openjdk}"
export DEFAULT_ANDROID_SDK_ROOT="${ANDROID_SDK_ROOT:-$HOME/Android/Sdk}"
# shellcheck source=common/env-wizard.sh
source "$HERE/common/env-wizard.sh"

step "dotnet restore + build"
if command -v dotnet >/dev/null 2>&1; then
    (cd "$PROJECT_ROOT" && dotnet restore && dotnet build -c Release)
else
    warn "dotnet not on PATH — open a new shell and re-run with --skip-deps."
fi

step "Done"
cat <<EOF
  Run the bot:
    cd "$PROJECT_ROOT"
    dotnet run --project src/BuildChatBot
EOF
