#!/bin/sh
# Taskrail installer for Linux and macOS.
#   curl -fsSL https://github.com/taskrail/taskrail/releases/latest/download/install.sh | sh
# Environment:
#   TASKRAIL_VERSION      version to install (default: latest), e.g. 0.1.0
#   TASKRAIL_INSTALL_DIR  target directory (default: $HOME/.local/bin)
set -eu

REPO="${TASKRAIL_REPO:-taskrail/taskrail}"
INSTALL_DIR="${TASKRAIL_INSTALL_DIR:-$HOME/.local/bin}"

err() { echo "taskrail-install: $*" >&2; exit 1; }
need() { command -v "$1" >/dev/null 2>&1 || err "required tool '$1' not found"; }

need curl
need tar
need uname

os="$(uname -s)"
arch="$(uname -m)"
case "$arch" in
  x86_64|amd64) arch="x64" ;;
  aarch64|arm64) arch="arm64" ;;
  *) err "unsupported architecture: $arch" ;;
esac

case "$os" in
  Linux)
    if [ -f /etc/alpine-release ] || (ldd --version 2>&1 | grep -qi musl); then
      rid="linux-musl-$arch"
    else
      rid="linux-$arch"
    fi
    ;;
  Darwin) rid="osx-$arch" ;;
  *) err "unsupported OS: $os (use install.ps1 on Windows)" ;;
esac

if [ -z "${TASKRAIL_VERSION:-}" ]; then
  # The /latest redirect target ends in /tag/vX.Y.Z.
  url="$(curl -fsSLI -o /dev/null -w '%{url_effective}' "https://github.com/$REPO/releases/latest")"
  TASKRAIL_VERSION="${url##*/v}"
  [ -n "$TASKRAIL_VERSION" ] && [ "$TASKRAIL_VERSION" != "$url" ] || err "could not determine latest version"
fi
version="${TASKRAIL_VERSION#v}"

archive="taskrail-$version-$rid.tar.gz"
base="https://github.com/$REPO/releases/download/v$version"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT INT TERM

echo "Downloading taskrail $version ($rid)..."
curl -fsSL "$base/$archive" -o "$tmp/$archive" || err "download failed: $base/$archive"
curl -fsSL "$base/SHA256SUMS" -o "$tmp/SHA256SUMS" || err "checksum download failed"

expected="$(grep " $archive\$" "$tmp/SHA256SUMS" | awk '{print $1}')"
[ -n "$expected" ] || err "no checksum for $archive"
if command -v sha256sum >/dev/null 2>&1; then
  actual="$(sha256sum "$tmp/$archive" | awk '{print $1}')"
elif command -v shasum >/dev/null 2>&1; then
  actual="$(shasum -a 256 "$tmp/$archive" | awk '{print $1}')"
else
  err "need sha256sum or shasum to verify the download"
fi
[ "$expected" = "$actual" ] || err "checksum mismatch for $archive (expected $expected, got $actual)"

tar -xzf "$tmp/$archive" -C "$tmp"
mkdir -p "$INSTALL_DIR"
mv "$tmp/taskrail" "$INSTALL_DIR/taskrail"
chmod +x "$INSTALL_DIR/taskrail"

echo "Installed taskrail $version to $INSTALL_DIR/taskrail"
case ":$PATH:" in
  *":$INSTALL_DIR:"*) ;;
  *) echo "Note: $INSTALL_DIR is not on your PATH. Add it to your shell profile." ;;
esac
