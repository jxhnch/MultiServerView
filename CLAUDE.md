# MultiServerView – Hinweise für Claude Code

Eigene Server-Verwaltung für Windows (SSH, SFTP, RDP, VNC, Port Forwarding, Cloud-Sync), Vorbild ist Termius. Besitzer: jxhnch.

## Aufbau
| Ordner | Inhalt |
|---|---|
| `src/MultiServerView.Core` | Vault + Crypto (PBKDF2 → AES-GCM), Merge/Cloud-Sync (Supabase), SSH/SFTP/Tunnel (SSH.NET), VNC-Proxy, Importer. net8.0, plattformunabhängig, gut testbar |
| `src/MultiServerView.App` | WinForms + WebView2: `Bridge.cs` (JSON-Nachrichten UI ↔ C#), RDP-Hosting (`Rdp/`), DPAPI, Auto-Sync, **Selbsttest** (`MainForm.Smoke.cs`) |
| `ui/` | React + TypeScript (Vite): xterm.js, noVNC. `src/bridge.ts` enthält die API **und** einen Browser-Mock (Demo-Daten, Demo-Shell) |
| `tests/` | xUnit (Core). SSH-/SFTP-/Tunnel-Integrationstests laufen nur mit `MSV_SSH_TEST_DIR` + lokalem sshd auf 127.0.0.1:2222 (Benutzer `msvtest`), sonst tun sie nichts |
| `.github/workflows/build.yml` | Windows-Build, Tests, Selbsttest auf echtem Windows, Artefakt `MultiServerView-win-x64` |

## Befehle
```bash
dotnet test tests/MultiServerView.Core.Tests        # Core-Tests
cd ui && npm install && npm run dev                  # UI im Browser mit Demo-Daten
cd ui && npm run build                               # baut nach src/MultiServerView.App/wwwroot
dotnet run --project src/MultiServerView.App         # Windows: echte App (baut das UI bei Bedarf)
src\MultiServerView.App\bin\...\MultiServerView.exe --smoke <ordner>   # Selbsttest + Screenshots + report.txt
```
Umgebungsvariablen: `MSV_DEV_URL` (UI vom Vite-Devserver laden), `MSV_DATA_DIR` (eigener Datenordner, z. B. zum Testen).

## Regeln des Besitzers
- **Commits nur unter dem Namen jxhnch** (`123569094+jxhnch@users.noreply.github.com`), **keine** «Co-Authored-By»-/Claude-Zeilen in Commits oder PRs.
- **Kein Force-Push ohne ausdrückliches Okay.**
- Oberfläche und Texte auf Deutsch, Schweizer Schreibweise (ss statt ß), Anführungszeichen «so».
- Ehrlich über den Teststatus berichten: Was gelaufen ist, was nur kompiliert wurde, was ungetestet ist.

## Stand
Fertig und auf Windows (CI-Selbsttest, rund 40 Prüfungen) bestätigt: Vault, Master-Passwort ändern, DPAPI, Einstellungen, RDP-Control im Tab, SSH-Terminal, SFTP (Doppelfenster, Ordner), Jump Hosts, Port Forwarding (-L/-R/-D), Snippets, Identities, Known Hosts, Logs, geteilte Ansicht mit Broadcast, Importe (mRemoteNG, ssh_config, PuTTY, CSV), Hell/Dunkel, 10 Terminal-Themes, OS-Logos.

**Ungetestet** (nur der Besitzer kann das): seine eigenen Server, RDP mit gespeichertem Passwort zu einem echten Ziel, VNC gegen echten Server unter Windows, echtes Supabase-Projekt.

**Ideen / offen:** integrierte Titelleiste (WebView2 `IsNonClientRegionSupportEnabled`, Fensterränder selbst steuern – riskant, erst nach Test des Besitzers), Telnet/Serial/Mosh, Remote-Dateien mit eigenem Editor bearbeiten, Snippet-Variablen und -Pakete, `known_hosts`-Import, SSH-Zertifikate/FIDO2, Terminal-Logs aufzeichnen. Vergleich mit Termius: `docs/termius-vergleich.md`. Checkliste für den ersten Start: `docs/ERSTER-START.md`.

## Stolpersteine (schon einmal passiert)
- SSH.NET ruft Fortschritts-Callbacks auf dem Threadpool auf: **dort nie Exceptions werfen** (reisst den Prozess). Für Abbruch die async-API mit `CancellationToken` benutzen.
- Befehle für Fernabfragen ohne Umleitungen wie `2>/dev/null` – `cmd.exe` (Windows-OpenSSH) versteht sie nicht (siehe `OsDetector.ProbeCommand`).
- RDP: ActiveX per Late-Binding (`dynamic`), Passwort nur über `IMsTscNonScriptable`; alles auf dem UI-Thread. Beim Schliessen kann das Aufräumen blockieren → Wächter in `MainForm.OnFormClosed`, nach 3 s harter Exit.
- Vite: `build.target = 'esnext'` (noVNC nutzt Top-Level-await); noVNC wird als `@novnc/novnc` importiert.
- UI ohne `<StrictMode>` (würde im Dev-Modus Sessions doppelt öffnen).
- Cloud-Sync: Merge pro Eintrag nach `UpdatedAt`, Löschungen als Tombstones. Master-Passwort ändern → neuer Salt → Cloud mit `ForceUpload` überschreiben, andere Geräte nutzen `AdoptCloudPassword`.
- Der Windows-Selbsttest braucht auf dem Runner einen OpenSSH-Server (Setup im Workflow, inkl. `AllowGroups`-Anpassung). Screenshots stehen zusätzlich als Base64 im Build-Log (`PNGDATA`), weil Artefakt-Downloads in manchen Umgebungen gesperrt sind.
