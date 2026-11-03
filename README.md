# MultiServerView

Eigene Server-Verwaltung für Windows – ein Fenster für **SSH, RDP und VNC**, schnelles Wechseln per `Ctrl+K`, verschlüsselter Vault und optionaler Cloud-Sync (Supabase).

| | |
|---|---|
| **SSH** | Echtes Terminal (xterm.js), Passwort **oder SSH-Key**, Host-Key-Prüfung, 10 Farbschemata, Schriftart und -grösse, Snippets per Klick |
| **SFTP** | Zwei Fenster (dieser PC / Server), Drag & Drop, Upload/Download von Dateien und ganzen Ordnern mit Fortschritt und Abbrechen, Ordner anlegen, umbenennen, löschen |
| **RDP** | Natives Windows-RDP-Control (wie mstsc / mRemoteNG) direkt im Tab |
| **VNC** | noVNC im Tab, inkl. Strg+Alt+Entf und Zwischenablage |
| **Port Forwarding** | Lokal (-L), Remote (-R) und SOCKS5 (-D) über SSH-Hosts, Start/Stopp per Klick |
| **Schnell wechseln** | Suchzeile oben (auch `ssh user@host` für neue Hosts), `Ctrl+K`, Tabs, Favoriten, Gruppen, Betriebssystem-Logos. Hell/Dunkel umschaltbar |
| **Zugangsdaten** | Keychain für SSH-Keys, Identities (Benutzer + Passwort/Key für mehrere Hosts) |
| **Snippets** | Häufige Befehle speichern, im Terminal mit einem Klick ausführen |
| **Known Hosts / Logs** | Gespeicherte Host-Keys ansehen und zurücksetzen; Verbindungsverlauf (nur lokal, ohne Passwörter) |
| **Cloud** | Ende-zu-Ende verschlüsselter Sync zwischen PCs (Supabase), Merge bei Änderungen auf beiden Seiten |
| **Import** | mRemoteNG (`confCons.xml`) |

## Starten (Windows)

**Variante A – fertig gebaut über GitHub:** Im Repo unter *Actions → Build (Windows) → Artifacts* die Datei `MultiServerView-win-x64` herunterladen, entpacken, `MultiServerView.exe` starten. Es muss nichts installiert werden (.NET ist enthalten).

**Variante B – selber bauen:** Voraussetzungen: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) und [Node.js 20+](https://nodejs.org).

```bash
scripts\publish.cmd
```

Das führt Tests aus, baut die Oberfläche und legt die App nach `publish\MultiServerView.exe`. Zum schnellen Ausprobieren reicht auch:

```bash
dotnet run --project src/MultiServerView.App
```

Beim ersten Start legst du ein **Master-Passwort** an. Es gibt keine Wiederherstellung – wer es vergisst, kommt nicht mehr an den Vault. Die WebView2-Runtime ist auf Windows 10/11 normalerweise schon da.

## Tastenkürzel

In einem Terminal gehören einfache Strg-Kombinationen der Shell (`Ctrl+K`, `Ctrl+W`, …). Deshalb laufen die App-Befehle mit `Ctrl+Shift`:

| Taste | Aktion |
|---|---|
| `Ctrl+K` | Schnell verbinden (ausserhalb des Terminals) |
| `Ctrl+Shift+K` | Schnell verbinden (**überall**, auch im Terminal) |
| `Ctrl+Tab` / `Ctrl+Shift+Tab` | Nächster / vorheriger Tab |
| `Ctrl+1 … 9` | Tab direkt wählen (1 = Start) |
| `Ctrl+Shift+W` | Tab schliessen (Mittelklick geht auch) |
| `Ctrl+Shift+N` | Neuer Host |
| `Ctrl+Shift+D` | Zweite Session zum selben Host |
| `Shift+Enter` / `Shift+Klick` | Beim Verbinden immer eine neue Session öffnen |
| `Ctrl+Shift+C` / `Ctrl+Shift+V` | Terminal: kopieren / einfügen (Rechtsklick geht auch) |

Wenn der Fokus in einer **RDP-Session** ist, gehen alle Tasten an den Remote-PC (so ist das bei jedem RDP-Client). Dann mit der Maus auf einen anderen Tab klicken.

## Cloud-Sync einrichten (einmalig, ca. 3 Minuten)

Der Sync braucht ein kostenloses [Supabase](https://supabase.com)-Projekt. In der Cloud liegt **nur der verschlüsselte Vault** – Supabase sieht weder Hosts noch Passwörter noch Keys.

1. Projekt auf supabase.com erstellen.
2. **SQL Editor** → Inhalt von [`docs/supabase.sql`](docs/supabase.sql) einfügen → *Run*.
3. **Project Settings → API**: *Project URL* und den *anon public* Key kopieren.
4. In der App links unten **Cloud** → URL, Key, E-Mail und ein Cloud-Passwort eintragen → *Konto erstellen* → *Verbinden*.
   (Wenn Supabase eine Bestätigungs-Mail verlangt: bestätigen, dann *Verbinden*. Oder unter *Authentication → Providers → Email* «Confirm email» ausschalten.)
5. **Zweiter PC:** Beim ersten Start unter dem Master-Passwort-Feld «Neues Gerät? Vault aus der Cloud laden» wählen, Supabase-Zugangsdaten eingeben, danach mit dem **gleichen Master-Passwort** entsperren.

Danach läuft der Sync automatisch ca. 3 Sekunden nach jeder Änderung und nach dem Entsperren. Ändern beide PCs gleichzeitig etwas, wird pro Eintrag der neuere Stand übernommen, Löschungen bleiben gelöscht.

Das Cloud-Passwort wird lokal mit Windows DPAPI geschützt (nur dein Windows-Benutzer kann es lesen). Das Master-Passwort verlässt den PC nie.

## Sicherheit in Kürze

- Vault: PBKDF2-SHA256 (600 000 Iterationen) → AES-256-GCM, Datei `%APPDATA%\MultiServerView\vault.msv`. Passwörter und private Keys stehen nie im Klartext auf der Platte oder in der Cloud.
- Private SSH-Keys werden nach dem Import nicht mehr ans UI geschickt; die Verbindung läuft komplett im Host-Prozess.
- SSH-Host-Keys: «Trust on first use». Ändert sich der Key eines bekannten Hosts, wird die Verbindung **abgelehnt** (im Host-Dialog lässt sich der Key zurücksetzen).
- VNC läuft über eine lokale Brücke, die nur auf `127.0.0.1` lauscht und jede Verbindung mit einem Einmal-Token absichert.
- Server-Passwörter sind im UI sichtbar, wenn du einen Host bearbeitest (Passwortfeld). Der Browser-Entwicklungsmodus (`npm run dev`) speichert unverschlüsselt und ist nur für die UI-Entwicklung gedacht.

## Aufbau

| Ordner | Inhalt |
|---|---|
| `src/MultiServerView.Core` | Vault + Crypto, Merge, Cloud-Sync, SSH-Session (SSH.NET), VNC-WebSocket-Brücke, mRemoteNG-Import (net8.0, plattformunabhängig) |
| `src/MultiServerView.App` | WinForms-Fenster mit WebView2, Bridge zum UI, RDP-Hosting, DPAPI, Auto-Sync (net8.0-windows) |
| `ui/` | React + TypeScript (Vite): Oberfläche, xterm.js, noVNC |
| `tests/` | xUnit-Tests (Crypto, Vault, Merge, Sync gegen simulierte Supabase-API, VNC-Brücke, Import, SSH gegen echten sshd) |
| `docs/` | `supabase.sql` |

## Entwickeln

```bash
dotnet test tests/MultiServerView.Core.Tests    # Core-Tests (laufen auch unter Linux/macOS)

cd ui && npm install && npm run dev             # UI im Browser mit Demo-Daten und Demo-Shell
```

Die SSH-Integrationstests laufen nur, wenn `MSV_SSH_TEST_DIR` auf einen Ordner mit Testkeys zeigt und lokal ein sshd auf `127.0.0.1:2222` läuft (Benutzer `msvtest`); sonst tun sie nichts.

UI gegen das echte Fenster mit Hot Reload:

```bash
set MSV_DEV_URL=http://localhost:5173
dotnet run --project src/MultiServerView.App
```

## Teststatus (ehrlich)

| Teil | Status |
|---|---|
| Vault, Crypto, Merge, Sync-Logik, SSH, SFTP, Port Forwarding, VNC-Brücke, Import | **Automatisch getestet** (76 Tests; SSH, SFTP und alle drei Weiterleitungs-Arten gegen einen echten sshd, Sync gegen simulierte Supabase-API) |
| UI (Tabs, Palette, Keychain, SFTP, Snippets, Weiterleitungen, Formulare, Terminal) | Im Browser mit Playwright durchgespielt (Demo-Daten und Demo-Shell) |
| VNC Ende-zu-Ende | Getestet: noVNC → C#-Brücke → echter VNC-Server |
| Windows-Fenster (WebView2), **RDP-Control**, DPAPI | **Kompiliert, aber noch nie auf Windows gelaufen.** RDP ist der riskanteste Teil (ActiveX-Hosting). Falls es dort hakt: Button «In Windows-RDP öffnen» startet mstsc als Ausweg |
| Echter Supabase-Server | Noch nicht gegen ein echtes Projekt getestet (nur gegen die Simulation) |

## Bekannte Einschränkungen

- Solange eine Palette oder ein Dialog offen ist, wird ein laufendes RDP-Fenster kurz ausgeblendet (natives Fenster liegt sonst über dem UI). Die Session läuft weiter.
- PuTTY-Keys (`.ppk`) vorher in PuTTYgen nach OpenSSH exportieren.
- mRemoteNG-Import übernimmt keine Passwörter (mRemoteNG verschlüsselt sie selber). Einen Termius-Import gibt es nicht.
- Keine Jump-Hosts/Host-Chains, kein Split-View, kein Telnet/Serial/Mosh (siehe [`docs/termius-vergleich.md`](docs/termius-vergleich.md)).
- Die Titelleiste ist die normale Windows-Titelleiste (Tabs sitzen darunter).
