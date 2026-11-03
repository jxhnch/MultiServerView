# Vergleich mit Termius

Stand: November 2026. Grundlage sind die öffentlich auffindbaren Termius-Seiten (Dokumentation, Preisseite, Changelog) über eine Websuche;
die Seiten selbst liessen sich aus der Entwicklungsumgebung nicht direkt abrufen. Angaben zu Tarifen bitte auf termius.com prüfen.

## Haben wir (gleichwertig oder vergleichbar)
SSH-Terminal, SFTP mit zwei Fenstern und Drag & Drop, Port Forwarding (lokal/remote/dynamisch), Snippets, Identities, SSH-Key-Verwaltung,
Known Hosts, Verbindungsverlauf, Terminal-Themes und Schrift, Gruppen und Tags, Betriebssystem-Icons, verschlüsselter Cloud-Sync,
Hell/Dunkel. **Zusätzlich gegenüber Termius (in den Quellen nicht erwähnt): RDP und VNC, mRemoteNG-Import.**

## Termius kann, wir noch nicht
| Bereich | Termius | Bei uns |
|---|---|---|
| Protokolle | Mosh, Telnet, Serial, lokales Terminal | fehlt |
| Jump Hosts | Host-Chains (auch pro Gruppe vererbt) | fehlt |
| Terminal | Split View (bis 16 Panes), Broadcast-Eingabe, KI-Autocomplete | fehlt |
| SFTP | Remote-Datei mit eigenem Editor bearbeiten und automatisch hochladen, Rechte ändern, zwei Server nebeneinander | fehlt |
| Snippets | Pakete (Gruppen), Variablen | nur einfache Snippets |
| Schlüssel | SSH-Zertifikate, FIDO2-Keys, Key-Erzeugung in der App, Agent Forwarding | nur Import von Keys |
| Import | ~/.ssh (ssh_config, known_hosts, Keys), PuTTY-Sitzungen, CSV | nur mRemoteNG |
| Logs | Terminal-Ausgabe pro Host aufzeichnen, Logs synchronisieren | nur Verbindungsverlauf |
| Teams | Geteilte Vaults mit Rechten, SSO, Zusammenarbeit | nur persönlicher Vault |
| Plattformen | Windows, macOS, Linux, iOS, Android, iPadOS | nur Windows |
| Umgebungsvariablen pro Host | ja (Pro) | fehlt |
