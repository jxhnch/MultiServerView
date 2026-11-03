# Erster Start auf deinem PC (Checkliste)

## So kommst du an die App
1. Auf GitHub: Repo **jxhnch/MultiServerView** → **Actions** → neuester grüner Lauf «Build (Windows)».
2. Unten bei **Artifacts** `MultiServerView-win-x64` herunterladen und entpacken.
3. `MultiServerView.exe` starten. Es muss nichts installiert werden (.NET ist enthalten; WebView2 ist auf Windows 10/11 meist schon da).
4. Beim ersten Start: **Master-Passwort** festlegen (mindestens 8 Zeichen, keine Wiederherstellung!).

Unter **Artifacts** liegt auch `Selbsttest-Screenshots`: Bilder der App, aufgenommen auf einem echten Windows-Rechner beim automatischen Test.

## Was schon automatisch auf Windows geprüft wurde (bei jedem Build)
Rund 40 Prüfungen auf einem echten Windows-Rechner, unter anderem: Fenster und Oberfläche starten, Vault erstellen/sperren/entsperren,
Master-Passwort ändern, Windows-Verschlüsselung (DPAPI), Einstellungen speichern, **RDP-Control im Tab** (inkl. schnelles Schliessen),
**SSH-Terminal** gegen einen echten OpenSSH-Server (Befehl, Fenstergrösse, Host-Key, Betriebssystem-Erkennung), **SFTP** mit Ordnern und
Windows-Pfaden, **Jump Host**, **Port Forwarding**, Terminal und SFTP in der echten Oberfläche, Schliessen ohne hängenden Prozess.

## Was nur du prüfen kannst (5 Minuten)
- [ ] **Deine echten Server**: Ersten SSH-Host anlegen (Passwort **und** Key probieren), verbinden, Befehle tippen, Fenster verkleinern/vergrössern.
- [ ] **RDP zu deinem Windows-PC/Server**: Passwort speichern, verbinden, Fenstergrösse ändern, Zwischenablage testen.
- [ ] **VNC** (falls du einen hast).
- [ ] **Cloud**: Supabase einrichten (Anleitung im README), auf einem zweiten PC «Aus Cloud laden».
- [ ] **Import**: Deine mRemoteNG-Datei (`confCons.xml`) oder PuTTY-Sitzungen importieren.
- [ ] **Gefühl**: Fühlt sich Hosts → Verbinden → Wechseln flüssig an? Was nervt?

## Wenn etwas nicht klappt
Mach einen Screenshot und schreib mir, **was du angeklickt hast** und **was stattdessen passiert ist**. Bei Verbindungsfehlern steht die Ursache meist im Terminal bzw. im roten Hinweis.
