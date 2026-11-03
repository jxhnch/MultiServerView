# MultiServerView

Eigene Server-Verwaltung für Windows: SSH, RDP und VNC in Tabs, schnelles Wechseln per `Ctrl+K`, verschlüsselter Vault mit späterem Cloud-Sync.

## Aufbau

| Ordner | Inhalt |
|---|---|
| `src/MultiServerView.Core` | Modelle, Vault-Verschlüsselung (PBKDF2-SHA256 + AES-256-GCM), Speicherung. Plattformunabhängig (net8.0). |
| `src/MultiServerView.App` | WinForms-Host mit WebView2 (net8.0-windows). Bridge zwischen UI und Core. |
| `ui/` | React + TypeScript (Vite). Läuft in WebView2, im Browser mit Mock-Daten. |
| `tests/` | xUnit-Tests für Vault und Crypto. |

## Starten (Windows)

Voraussetzungen: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), [Node.js 18+](https://nodejs.org), WebView2-Runtime (auf Windows 11 schon drauf).

```bash
dotnet run --project src/MultiServerView.App
```

Beim ersten Build wird das UI automatisch gebaut (`npm ci` + `npm run build`).

Tests:

```bash
dotnet test tests/MultiServerView.Core.Tests
```

## UI-Entwicklung mit Hot Reload

```bash
cd ui
npm install
npm run dev
```

Im Browser unter http://localhost:5173 läuft eine Mock-Version (localStorage, **unverschlüsselt**, nur zum Entwickeln). Um das echte Fenster gegen den Dev-Server laufen zu lassen:

```bash
set MSV_DEV_URL=http://localhost:5173
dotnet run --project src/MultiServerView.App
```

## Shortcuts

| Taste | Aktion |
|---|---|
| `Ctrl+K` | Schnell verbinden (Suche über alle Server) |
| `Ctrl+Tab` / `Ctrl+Shift+Tab` | Nächster / vorheriger Tab |
| `Ctrl+1…9` | Direkt zu Tab springen (1 = Übersicht) |
| `Ctrl+W` | Tab schliessen |
| `Ctrl+N` | Neuer Server |

## Roadmap

1. ✅ Gerüst, Server-Liste, Command Palette, verschlüsselter lokaler Vault
2. SSH-Terminal (xterm.js + SSH.NET), Key-Auth
3. Cloud-Sync (verschlüsselter Vault-Blob, Backend z. B. Supabase)
4. VNC (noVNC)
5. RDP (natives Microsoft-RDP-Control, wie mRemoteNG)
6. Import aus Termius / mRemoteNG, SFTP, Jump-Hosts

## Status

Phase 1 ist geschrieben. Das UI wurde im Browser getestet (Anlegen, Suchen, Tabs, Sperren/Entsperren). Der C#-Teil (Core, Bridge, WinForms-Host) wurde **noch nicht kompiliert oder ausgeführt**, weil die Entwicklungsumgebung weder .NET noch Windows hatte. Der erste `dotnet build` und `dotnet test` auf Windows ist der erste echte Test.
