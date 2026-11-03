# Vergleich mit Termius

Stand: November 2026. Grundlage sind die öffentlich auffindbaren Termius-Seiten (Dokumentation, Preisseite, Changelog) über eine Websuche;
die Seiten selbst liessen sich aus der Entwicklungsumgebung nicht direkt abrufen. Angaben zu Tarifen bitte auf termius.com prüfen.

## Haben wir (gleichwertig oder vergleichbar)
SSH-Terminal, SFTP mit zwei Fenstern und Drag & Drop, Port Forwarding (lokal/remote/dynamisch), Snippets, Identities, SSH-Key-Verwaltung,
Known Hosts, Verbindungsverlauf, Terminal-Themes und Schrift, Gruppen und Tags, Betriebssystem-Icons, verschlüsselter Cloud-Sync,
Hell/Dunkel, **Jump Hosts (mehrstufig)**, **Geteilte Ansicht mit Broadcast**, Import aus mRemoteNG, OpenSSH-Config, PuTTY und CSV,
Rechtsklick-Menü, Einstellungsseite (inkl. Master-Passwort ändern, Auto-Sperre, Backup).
**Zusätzlich gegenüber Termius (in den Quellen nicht erwähnt): RDP und VNC.**

## Termius kann, wir noch nicht
| Bereich | Termius | Bei uns |
|---|---|---|
| Protokolle | Mosh, Telnet, Serial, lokales Terminal | fehlt |
| Jump Hosts | Vererbung der Kette pro Gruppe | Kette pro Host (mehrstufig) ja, pro Gruppe nein |
| Terminal | KI-Autocomplete | fehlt |
| Split View | bis 16 Panes, Layouts per Drag | Raster mit bis zu mehreren Panes, Broadcast ja; keine freien Layouts |
| SFTP | Remote-Datei mit eigenem Editor bearbeiten und automatisch hochladen, Rechte ändern, zwei Server nebeneinander | fehlt |
| Snippets | Pakete (Gruppen), Variablen | nur einfache Snippets |
| Schlüssel | SSH-Zertifikate, FIDO2-Keys, Key-Erzeugung in der App, Agent Forwarding | nur Import von Keys |
| Import | known_hosts, ~/.ssh-Ordner komplett inkl. Keys | ssh_config, PuTTY, CSV, mRemoteNG (ohne Keys und Passwörter) |
| Logs | Terminal-Ausgabe pro Host aufzeichnen, Logs synchronisieren | nur Verbindungsverlauf |
| Teams | Geteilte Vaults mit Rechten, SSO, Zusammenarbeit | nur persönlicher Vault |
| Plattformen | Windows, macOS, Linux, iOS, Android, iPadOS | nur Windows |
| Umgebungsvariablen pro Host | ja (Pro) | fehlt |
