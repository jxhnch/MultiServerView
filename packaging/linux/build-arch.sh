#!/usr/bin/env bash
# Builds the ready-made Arch package and proves that it installs and starts. Runs as root in an "archlinux:base-devel" container
# (see .github/workflows/release.yml).
# usage: build-arch.sh <linux-x64.tar.gz> <version> <out-dir>
set -euo pipefail
TARBALL=$(realpath "$1"); VER="$2"; OUT=$(realpath -m "$3")
HERE=$(cd "$(dirname "$0")" && pwd)
ROOT=$(realpath "$HERE/../..")
mkdir -p "$OUT"

pacman -Syu --noconfirm

# makepkg refuses to run as root
WORK=$(mktemp -d)
id builder >/dev/null 2>&1 || useradd -m builder

# The release does not exist yet while it is being built: put the sources next to the PKGBUILD under the names it expects,
# then makepkg uses them instead of downloading. Their real checksums go into the PKGBUILD.
cp "$TARBALL" "$WORK/MultiServerView-$VER-linux-x64.tar.gz"
cp "$HERE/multiserverview.desktop" "$WORK/multiserverview-$VER.desktop"
cp "$ROOT/docs/screenshots/icon.png" "$WORK/multiserverview-$VER.png"
cp "$ROOT/LICENSE" "$WORK/LICENSE-$VER"
SUMS=""
for f in "MultiServerView-$VER-linux-x64.tar.gz" "multiserverview-$VER.desktop" "multiserverview-$VER.png" "LICENSE-$VER"; do
  SUMS="$SUMS '$(sha256sum "$WORK/$f" | cut -d' ' -f1)'"
done
sed -e "s/^pkgver=.*/pkgver=$VER/" -e "s/^sha256sums=.*/sha256sums=($SUMS)/" "$HERE/PKGBUILD" > "$WORK/PKGBUILD"
chown -R builder "$WORK"
# --nodeps: the libraries are only needed to run it; installing the package below pulls them in
su builder -c "cd '$WORK' && makepkg --force --nodeps"

PKG=$(ls "$WORK"/multiserverview-bin-"$VER"-*-x86_64.pkg.tar.zst)
pacman -U --noconfirm "$PKG"
test -x /usr/bin/multiserverview
msv --version | grep -F "$VER"
multiserverview --selftest "$(mktemp -d)"

# a name without a version, so the "latest release" download link never changes
cp "$PKG" "$OUT/multiserverview-bin-x86_64.pkg.tar.zst"
ls -la "$OUT"
rm -rf "$WORK"
