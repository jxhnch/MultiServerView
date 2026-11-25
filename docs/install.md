# Installation

[← Back to the README](../README.md)

All downloads are on the [Releases page](https://github.com/jxhnch/MultiServerView/releases/latest). Each release includes `SHA256SUMS.txt` with checksums.

## Windows

| File | For |
|---|---|
| `MultiServerView-Setup-<version>.exe` | Installer wizard: no admin rights needed, Start menu entry, uninstaller. The app checks GitHub for a newer release at startup and installs it on request (switch off in *Settings → About*) |
| `MultiServerView-<version>-portable-win-x64.zip` | Portable: unpack anywhere and run `MultiServerView.exe` |

Nothing else has to be installed: .NET is bundled, and the WebView2 runtime is part of Windows 10/11.

### SmartScreen warning

If you see **"Windows protected your PC"**: the downloads are not code-signed yet. Code-signing certificates cost money and are tied to a verified identity, and SmartScreen also distrusts every new, little-downloaded program. The warning by itself does not mean the file is malware.

Click **More info → Run anyway**. To be sure you have the original, download only from this repository's Releases page. The whole source and the [build workflow](../.github/workflows/release.yml) are public, so you can also build it yourself.

## Linux

All Linux builds are `linux-x64`.

### Debian / Ubuntu

Installs the needed libraries automatically:

```bash
sudo apt install ./MultiServerView-<version>-linux-x64.deb
```

### Fedora / RHEL-based

Installs the needed libraries automatically:

```bash
sudo dnf install ./MultiServerView-<version>-linux-x64.rpm
```

### Arch-based

For Arch, Manjaro, EndeavourOS and CachyOS, use the [`PKGBUILD`](../packaging/linux/PKGBUILD) from a clone of this repository:

```bash
cd packaging/linux && makepkg -si
```

### AppImage or tar.gz

Run the AppImage:

```bash
chmod +x MultiServerView-*.AppImage && ./MultiServerView-*.AppImage
```

Or unpack the `.tar.gz` and run `multiserverview`.

Both need these packages from your distribution:

| Distribution | Packages |
|---|---|
| Debian / Ubuntu | `libwebkit2gtk-4.1-0 libgtk-3-0 libnotify4 xdg-utils` |
| Fedora / RHEL | `webkit2gtk4.1 gtk3 libnotify xdg-utils` |
| Arch | `webkit2gtk-4.1 gtk3 libnotify xdg-utils` (plus `fuse2` for AppImages, or run with `--appimage-extract-and-run`) |

### Differences to Windows

It is the same vault and UI, with these differences:

- RDP is drawn inside the app.
- The window uses the system title bar.
- Secrets are protected by a key file (mode 600) instead of DPAPI.
- There is no in-app self-update. Download new versions from the Releases page.

`multiserverview --selftest <folder>` checks vault, vault sources, MCP and (with `MSV_SMOKE_SSH=host:port:user:password`) SSH/SFTP without opening a window.

## First start

You create a **master password** with at least 8 characters. **There is no recovery:** if you forget it, nobody can open your vault.

To remove your data completely, see [Start from zero](vault.md#start-from-zero).
