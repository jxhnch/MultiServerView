#!/usr/bin/env bash
# Builds .deb, .rpm, AppImage and .tar.gz packages from a published linux-x64 folder.
# usage: build-packages.sh <publish-dir> <version> <out-dir>
set -euo pipefail
PUB=$(realpath "$1"); VER="$2"; OUT=$(realpath -m "$3")
HERE=$(cd "$(dirname "$0")" && pwd)
ICON=$(realpath "$HERE/../../docs/screenshots/icon.png")
mkdir -p "$OUT"
WORK=$(mktemp -d)

# ---------------- .deb (apt installs the needed libraries by itself)
D="$WORK/deb"
mkdir -p "$D/DEBIAN" "$D/opt/multiserverview" "$D/usr/bin" "$D/usr/share/applications" "$D/usr/share/icons/hicolor/512x512/apps"
cp -r "$PUB"/. "$D/opt/multiserverview/"
chmod 755 "$D/opt/multiserverview/multiserverview"
ln -s /opt/multiserverview/multiserverview "$D/usr/bin/multiserverview"
cp "$HERE/multiserverview.desktop" "$D/usr/share/applications/"
cp "$ICON" "$D/usr/share/icons/hicolor/512x512/apps/multiserverview.png"
cat > "$D/DEBIAN/control" <<EOF
Package: multiserverview
Version: $VER
Section: net
Priority: optional
Architecture: amd64
Maintainer: jxhnch <123569094+jxhnch@users.noreply.github.com>
Depends: libwebkit2gtk-4.1-0, libgtk-3-0, libnotify4, xdg-utils
Recommends: freerdp2-x11
Description: SSH, SFTP, RDP and VNC in one window
 Server manager with an end-to-end encrypted vault.
EOF
find "$D" -type d -exec chmod 755 {} +
dpkg-deb --root-owner-group --build "$D" "$OUT/MultiServerView-$VER-linux-x64.deb"

# ---------------- .rpm (Fedora / RHEL-based: dnf installs the needed libraries by itself)
R="$WORK/rpm"
RPM_VER="${VER//-/_}"
mkdir -p "$R/BUILD" "$R/RPMS" "$R/SOURCES" "$R/SPECS" "$R/SRPMS" "$R/TMP"
cat > "$R/SPECS/multiserverview.spec" <<EOF
%global debug_package %{nil}
%global __strip /bin/true
%define _build_id_links none

Name:           multiserverview
Version:        $RPM_VER
Release:        1
Summary:        SSH, SFTP, RDP and VNC in one window
License:        LicenseRef-MultiServerView
URL:            https://github.com/jxhnch/MultiServerView
BuildArch:      x86_64
AutoReqProv:    no
Requires:       webkit2gtk4.1, gtk3, libnotify, xdg-utils
Recommends:     freerdp

%description
Server manager with an end-to-end encrypted vault.

%install
mkdir -p %{buildroot}/opt/multiserverview
mkdir -p %{buildroot}/usr/bin
mkdir -p %{buildroot}/usr/share/applications
mkdir -p %{buildroot}/usr/share/icons/hicolor/512x512/apps
cp -r %{pub_dir}/. %{buildroot}/opt/multiserverview/
chmod 755 %{buildroot}/opt/multiserverview/multiserverview
ln -s /opt/multiserverview/multiserverview %{buildroot}/usr/bin/multiserverview
cp %{here_dir}/multiserverview.desktop %{buildroot}/usr/share/applications/multiserverview.desktop
cp %{icon_file} %{buildroot}/usr/share/icons/hicolor/512x512/apps/multiserverview.png

%files
%defattr(-,root,root,0755)
/opt/multiserverview
/usr/bin/multiserverview
/usr/share/applications/multiserverview.desktop
/usr/share/icons/hicolor/512x512/apps/multiserverview.png
EOF
rpmbuild -bb --target x86_64 \
  --define "_topdir $R" \
  --define "_tmppath $R/TMP" \
  --define "pub_dir $PUB" \
  --define "here_dir $HERE" \
  --define "icon_file $ICON" \
  "$R/SPECS/multiserverview.spec"
cp "$R/RPMS/x86_64/multiserverview-$RPM_VER-1.x86_64.rpm" "$OUT/MultiServerView-$VER-linux-x64.rpm"

# ---------------- AppImage (the system still provides WebKitGTK, see the release notes)
A="$WORK/MultiServerView.AppDir"
mkdir -p "$A/usr/lib/msv"
cp -r "$PUB"/. "$A/usr/lib/msv/"
chmod 755 "$A/usr/lib/msv/multiserverview"
cp "$HERE/multiserverview.desktop" "$A/multiserverview.desktop"
cp "$ICON" "$A/multiserverview.png"
cat > "$A/AppRun" <<'EOF'
#!/bin/sh
HERE="$(dirname "$(readlink -f "$0")")"
exec "$HERE/usr/lib/msv/multiserverview" "$@"
EOF
chmod 755 "$A/AppRun"
TOOL="$WORK/appimagetool"
curl -fsSL -o "$TOOL" "https://github.com/AppImage/appimagetool/releases/download/1.9.0/appimagetool-x86_64.AppImage"
chmod +x "$TOOL"
ARCH=x86_64 "$TOOL" --appimage-extract-and-run --no-appstream "$A" "$OUT/MultiServerView-$VER-linux-x64.AppImage"

# ---------------- plain archive
tar -C "$(dirname "$PUB")" -czf "$OUT/MultiServerView-$VER-linux-x64.tar.gz" --transform "s|^$(basename "$PUB")|multiserverview|" "$(basename "$PUB")"
ls -la "$OUT"
rm -rf "$WORK"
