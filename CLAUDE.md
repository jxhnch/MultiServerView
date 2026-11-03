# MultiServerView – Notes for Claude Code

Own server manager for Windows (SSH, SFTP, RDP, VNC, port forwarding, encrypted sync), inspired by Termius. Owner: jxhnch.

## Layout
| Folder | Contents |
|---|---|
| `src/MultiServerView.Core` | Vault + crypto (PBKDF2 → AES-GCM), merge/sync (shared folder or Supabase), SSH/SFTP/tunnels (SSH.NET), VNC proxy, importers (mRemoteNG, ssh_config, PuTTY, CSV, Proxmox, VMware). net8.0, platform independent, easy to test |
| `src/MultiServerView.App` | WinForms + WebView2: `Bridge.cs` (JSON messages UI ↔ C#), RDP hosting (`Rdp/`), DPAPI, auto-sync, custom title bar, **self-test** (`MainForm.Smoke.cs`) |
| `ui/` | React + TypeScript (Vite): xterm.js, noVNC. `src/bridge.ts` contains the API **and** a browser mock (demo data, demo shell) |
| `tests/` | xUnit (Core). SSH/SFTP/tunnel integration tests only run with `MSV_SSH_TEST_DIR` + a local sshd on 127.0.0.1:2222 (user `msvtest`), otherwise they do nothing |
| `.github/workflows/` | `build.yml`: Windows build, tests, self-test on real Windows, artifact `MultiServerView-win-x64`. `release.yml`: installer + portable ZIP on version tags |
| `installer/` | Inno Setup script for the Windows installer |

## Commands
```bash
dotnet test tests/MultiServerView.Core.Tests        # Core tests
cd ui && npm install && npm run dev                  # UI in the browser with demo data
cd ui && npm run build                               # builds into src/MultiServerView.App/wwwroot
dotnet run --project src/MultiServerView.App         # Windows: the real app
src\MultiServerView.App\bin\...\MultiServerView.exe --smoke <folder>   # self-test + screenshots + report.txt
```
Environment variables: `MSV_DEV_URL` (load the UI from the Vite dev server), `MSV_DATA_DIR` (own data folder, e.g. for testing).

## Owner rules
- **Commits only under the name jxhnch** (`123569094+jxhnch@users.noreply.github.com`), **no** "Co-Authored-By" / Claude lines in commits or PRs.
- **No force-push without explicit approval.**
- UI, messages, comments, tests and docs are written in **English** (the project is open source).
- Report the test status honestly: what ran, what was only compiled, what is untested.

## Status
Done and confirmed on Windows (CI self-test): vault, master password change, DPAPI, settings, RDP control in a tab, SSH terminal, SFTP (two panes, folders, remote text editor), jump hosts, port forwarding (-L/-R/-D), snippets (with variables), identities, SSH key generator + install on host, known hosts, logs, split view with broadcast, workspaces, imports (mRemoteNG, ssh_config, PuTTY, CSV, Proxmox, VMware Workstation), folder sync with conflict copies, custom title bar, light/dark, 10 terminal themes, OS logos.

**Untested** (only the owner can do this): their own servers, RDP with a saved password to a real target, VNC against a real server on Windows, a real Supabase project, a real Proxmox server.

**Ideas / open:** Telnet/Serial/Mosh, snippet packages, `known_hosts` import, SSH certificates/FIDO2, recording terminal logs, code signing, auto-update. Comparison with Termius: `docs/comparison-with-termius.md`. First-run checklist: `docs/first-run-checklist.md`.

## Pitfalls (already happened once)
- SSH.NET calls progress callbacks on the thread pool: **never throw exceptions there** (it kills the process). Use the async API with a `CancellationToken` to cancel.
- Remote probe commands without redirections like `2>/dev/null` – `cmd.exe` (Windows OpenSSH) does not understand them (see `OsDetector.ProbeCommand`). The German Windows phrase in `OsDetector` is intentional.
- RDP: ActiveX via late binding (`dynamic`), password only via `IMsTscNonScriptable`; everything on the UI thread. Cleanup can block on close → watchdog in `MainForm.OnFormClosed`, hard exit after 3 s.
- Vite: `build.target = 'esnext'` (noVNC uses top-level await); noVNC is imported as `@novnc/novnc`.
- UI without `<StrictMode>` (it would open sessions twice in dev mode).
- **`dotnet publish` does not rebuild the UI**: run `cd ui && npm run build` first, otherwise the published app contains a stale UI.
- Some strings are coupled between UI and C# (e.g. `PasswordPrompt.tsx` checks the message prefix `Authentication failed`, `SftpView.tsx` checks `Cancelled.`, `RemoteEditor.tsx` checks the "changed on the server" text). Change both sides together.
- Sync: merge per entry by `UpdatedAt`, deletions as tombstones. Changing the master password → new salt → overwrite the remote with `ForceUpload`, other devices use `AdoptCloudPassword`.
- The Windows self-test needs an OpenSSH server on the runner (set up in the workflow, including the `AllowGroups` tweak). Screenshots are also in the build log as base64 (`PNGDATA`) because artifact downloads are blocked in some environments.
