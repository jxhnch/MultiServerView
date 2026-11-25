# Development

[← Back to the README](../README.md)

Contributions are welcome: open an issue or a pull request. Please note the [license](../LICENSE) terms for contributions.

## Requirements

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Node.js 20+](https://nodejs.org)

## Build

Run the tests, build the UI and publish to `publish\MultiServerView.exe` (Windows):

```bat
scripts\publish.cmd
```

For quick experiments:

```bash
cd ui && npm install && npm run build
cd ..
dotnet run --project src/MultiServerView.App
```

> [!NOTE]
> `dotnet publish` does not rebuild the UI. Always run `npm run build` in `ui/` first.

## UI development

In the browser, with demo data and a demo shell:

```bash
cd ui && npm install && npm run dev
```

Against the real window with hot reload (Command Prompt, while `npm run dev` is running):

```bat
set MSV_DEV_URL=http://localhost:5173
dotnet run --project src/MultiServerView.App
```

## Tests

The tests also run on Linux and macOS:

```bash
dotnet test tests/MultiServerView.Core.Tests
```

The SSH/SFTP/tunnel integration tests only run when `MSV_SSH_TEST_DIR` points to a folder with test keys and a local sshd listens on `127.0.0.1:2222` (user `msvtest`). Otherwise they do nothing.

## Project layout

| Folder | Contents |
|---|---|
| `src/MultiServerView.Core` | Vault and crypto, vault sources (folder, GitHub, Supabase), SSH/SFTP/tunnels (SSH.NET), VNC bridge, importers. Platform independent (.NET 8) |
| `src/MultiServerView.Host` | Shared host logic: bridge to the UI, automatic saving to the vault source, MCP service, web proxy |
| `src/MultiServerView.App` | Windows app: WinForms window with WebView2, RDP hosting, DPAPI, self-test |
| `src/MultiServerView.Linux` | Linux app (Photino) and its self-test |
| `src/MultiServerView.Cli` | The `msv` command line tool |
| `ui/` | Interface in React and TypeScript (Vite), with xterm.js and noVNC |
| `tests/` | xUnit tests |
| `installer/` | Inno Setup script for the Windows installer |
| `packaging/linux/` | Linux packages and `PKGBUILD` |
| `docs/` | Documentation, Supabase SQL, screenshots |

Releases are built by [`release.yml`](../.github/workflows/release.yml) when a version tag (`v*`) is pushed.
