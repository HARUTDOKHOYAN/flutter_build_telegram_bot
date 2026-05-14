#!/usr/bin/env bash
# BuildChatBot installer — dispatches to the right OS-specific script.
# Usage:
#   bash install.sh                   # interactive
#   bash install.sh --non-interactive # accept all defaults
#   bash install.sh --skip-deps       # re-run env wizard only
#   bash install.sh --with-ndk        # also install Android NDK
#   bash install.sh --force           # overwrite existing env.local
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OS="$(uname -s)"

case "$OS" in
  Darwin)
    exec bash "$HERE/install/install-macos.sh" "$@"
    ;;
  Linux)
    exec bash "$HERE/install/install-linux.sh" "$@"
    ;;
  MINGW*|MSYS*|CYGWIN*)
    echo "Detected Git-Bash/MSYS on Windows. Please run install.ps1 from PowerShell instead:"
    echo "  powershell -ExecutionPolicy Bypass -File install.ps1"
    exit 1
    ;;
  *)
    echo "Unsupported OS: $OS"
    echo "Supported: macOS, Linux, Windows (via install.ps1)."
    exit 1
    ;;
esac
