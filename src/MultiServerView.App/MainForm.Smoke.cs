using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using MultiServerView.Core;
using MultiServerView.Core.Ssh;

namespace MultiServerView.App;

/// <summary>
/// Selbsttest auf einem echten Windows: Startet die komplette App (Fenster, WebView2, Bridge, Vault, DPAPI, RDP-Control),
/// bedient die Oberfläche per Skript, macht Screenshots und schreibt einen Bericht. Aufruf: MultiServerView.exe --smoke &lt;Ordner&gt;
/// </summary>
public sealed partial class MainForm
{
    private readonly List<string> _tap = new();
    private bool _smokeStarted;
    private int _smokeId;
    private readonly List<(string Name, bool Ok, string Detail)> _checks = new();

    private void Check(string name, bool ok, string detail = "") => _checks.Add((name, ok, detail));

    private async Task<string> Js(string script) => await _web.CoreWebView2.ExecuteScriptAsync(script);

    private async Task<bool> WaitFor(string expression, int seconds = 20)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            try
            {
                if (await Js($"!!({expression})") == "true") return true;
            }
            catch { /* Seite lädt noch */ }
            await Task.Delay(250);
        }
        return false;
    }

    private async Task Shot(string name)
    {
        try
        {
            await using var fs = File.Create(Path.Combine(_smokeDir!, name));
            await _web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
        }
        catch (Exception ex) { Check("Screenshot " + name, false, ex.Message); }
    }

    /// <summary>Ganzes Fenster inkl. nativer Controls (z. B. RDP) vom Bildschirm abfotografieren.</summary>
    private void ScreenShot(string name)
    {
        try
        {
            var bounds = RectangleToScreen(ClientRectangle);
            using var bmp = new Bitmap(bounds.Width, bounds.Height);
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            bmp.Save(Path.Combine(_smokeDir!, name), System.Drawing.Imaging.ImageFormat.Png);
        }
        catch (Exception ex) { Check("Bildschirmfoto " + name, false, ex.Message); }
    }

    private async Task<JsonElement> Call(string method, object? args = null)
    {
        var req = JsonSerializer.Serialize(new { id = ++_smokeId, method, @params = args }, Json.Options);
        var reply = await _bridge.HandleAsync(req);
        using var doc = JsonDocument.Parse(reply);
        var root = doc.RootElement.Clone();
        if (root.TryGetProperty("error", out var err)) throw new InvalidOperationException($"{method}: {err.GetString()}");
        return root.TryGetProperty("result", out var r) ? r : default;
    }

    private async Task RunSmokeAsync()
    {
        try
        {
            using var overall = new CancellationTokenSource(TimeSpan.FromSeconds(150));
            await SmokeSteps();
        }
        catch (Exception ex)
        {
            Check("Selbsttest abgebrochen", false, ex.ToString());
        }
        finally
        {
            var failed = _checks.Count(c => !c.Ok);
            var sb = new StringBuilder();
            foreach (var c in _checks) sb.AppendLine($"{(c.Ok ? "OK  " : "FEHL")} {c.Name}{(c.Detail.Length > 0 ? " – " + c.Detail : "")}");
            sb.AppendLine().AppendLine(failed == 0 ? "ALLES OK" : $"{failed} FEHLER");
            await File.WriteAllTextAsync(Path.Combine(_smokeDir!, "report.txt"), sb.ToString());
            Environment.ExitCode = failed == 0 ? 0 : 1;
            Log("Selbsttest fertig - schliesse Fenster");
            BeginInvoke(() => Close());
        }
    }

    private string SshText(string sessionId)
    {
        var sb = new StringBuilder();
        lock (_tap)
            foreach (var json in _tap.Where(t => t.Contains("ssh.data")))
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.GetProperty("sessionId").GetString() == sessionId)
                    sb.Append(Encoding.UTF8.GetString(Convert.FromBase64String(root.GetProperty("data").GetString()!)));
            }
        return sb.ToString();
    }

    private async Task<bool> WaitSsh(string sessionId, string needle, int seconds = 25)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            if (SshText(sessionId).Contains(needle)) return true;
            await Task.Delay(250);
        }
        return false;
    }

    private static string Snip(string s, int n)
    {
        s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length <= n ? s : s[..n];
    }

    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    private async Task SmokeSshAsync()
    {
        var cfg = Environment.GetEnvironmentVariable("MSV_SMOKE_SSH");
        if (string.IsNullOrWhiteSpace(cfg))
        {
            Check("SSH/SFTP-Tests übersprungen (kein OpenSSH-Server auf dem Runner)", true);
            return;
        }
        var parts = cfg.Split(':', 4);
        var (host, port, user, pass) = (parts[0], int.Parse(parts[1]), parts[2], parts[3]);

        try
        {
            var target = await Call("servers.save", new { name = "smoke-ssh", host, port, protocol = "ssh", username = user, password = pass, group = "Server" });
            var id = target.GetProperty("id").GetString()!;

            // Terminal: verbinden, Befehl senden, Ausgabe zurückbekommen
            // Beim allerersten Login legt Windows erst das Benutzerprofil an - das kann länger dauern, darum bis zu 3 Versuche.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await Call("ssh.open", new { sessionId = "smoke-ssh-1", serverId = id, cols = 100, rows = 30 });
                    break;
                }
                catch when (attempt < 3)
                {
                    await Task.Delay(5000);
                }
            }
            Check("SSH: Verbindung zum OpenSSH-Server aufgebaut (SSH.NET unter Windows)", true);
            await Task.Delay(1500);
            await Call("ssh.input", new { sessionId = "smoke-ssh-1", data = B64("echo SMOKE-%USERNAME%-" + (6 * 7) + "\r\n") });
            Check("SSH: Befehl ausgeführt, Ausgabe kommt zurück", await WaitSsh("smoke-ssh-1", "SMOKE-" + user + "-42"), Snip(SshText("smoke-ssh-1"), 160));
            await Call("ssh.resize", new { sessionId = "smoke-ssh-1", cols = 120, rows = 40 });
            Check("SSH: Fenstergrösse ändern", true);
            await Call("ssh.close", new { sessionId = "smoke-ssh-1" });

            // Host-Key wurde gemerkt, Betriebssystem erkannt
            Check("Known Hosts: Host-Key beim ersten Kontakt gespeichert", (await Call("knownhosts.list")).GetArrayLength() >= 1);
            var os = "";
            for (var i = 0; i < 20 && os == ""; i++)
            {
                await Task.Delay(500);
                foreach (var s in (await Call("servers.list")).EnumerateArray())
                    if (s.GetProperty("id").GetString() == id) os = s.GetProperty("os").GetString() ?? "";
            }
            Check("Betriebssystem erkannt", os.Length > 0, "erkannt: " + os);

            // Messung: was liefert ein Windows-OpenSSH-Server auf die OS-Erkennung?
            var probeServer = new ServerEntry { Name = "probe", Host = host, Port = port, Username = user, Password = pass };
            using (var probe = new SshTerminalSession(probeServer, null, (_, _, _) => HostKeyTrust.Trusted))
            {
                await probe.ConnectAsync(80, 24);
                var raw = await probe.RunCommandAsync(OsDetector.ProbeCommand, TimeSpan.FromSeconds(8));
                Check("OS-Erkennung: Befehl lieferte Ausgabe", raw != null, Snip(raw ?? "(null)", 200));
                Check("OS-Erkennung: Windows-Server wird erkannt", OsDetector.Parse(raw ?? "") == "windows", "erkannt: '" + OsDetector.Parse(raw ?? "") + "'");
            }

            // SFTP + lokales Dateisystem unter Windows
            var home = (await Call("sftp.open", new { sessionId = "smoke-sftp-1", serverId = id })).GetProperty("home").GetString()!;
            Check("SFTP: verbunden", home.StartsWith('/'), "Home: " + home);
            var listing = await Call("sftp.list", new { sessionId = "smoke-sftp-1", path = home });
            Check("SFTP: Home-Ordner lesen", listing.GetProperty("entries").ValueKind == JsonValueKind.Array, listing.GetProperty("path").GetString());

            var work = Path.Combine(_smokeDir!, "sftp-work");
            Directory.CreateDirectory(Path.Combine(work, "ordner"));
            File.WriteAllText(Path.Combine(work, "ordner", "a.txt"), "Hallo SFTP");
            File.WriteAllBytes(Path.Combine(work, "gross.bin"), new byte[500_000]);
            var local = await Call("local.browse", new { path = work });
            Check("Lokales Dateisystem (Windows-Pfade) lesen", local.GetProperty("entries").GetArrayLength() == 2, local.GetProperty("path").GetString());
            var roots = await Call("local.roots");
            Check("Lokale Laufwerke lesen", roots.GetProperty("entries").GetArrayLength() >= 1, roots.GetProperty("entries")[0].GetProperty("name").GetString());

            var remoteDir = home.TrimEnd('/') + "/msv-smoke";
            await Call("sftp.mkdir", new { sessionId = "smoke-sftp-1", path = remoteDir });
            await Call("sftp.upload", new { sessionId = "smoke-sftp-1", transferId = "t1", localPaths = new[] { Path.Combine(work, "ordner"), Path.Combine(work, "gross.bin") }, remoteDir });
            var up = await Call("sftp.list", new { sessionId = "smoke-sftp-1", path = remoteDir });
            Check("SFTP: Ordner und Datei hochgeladen", up.GetProperty("entries").GetArrayLength() == 2);

            var back = Path.Combine(_smokeDir!, "sftp-back");
            Directory.CreateDirectory(back);
            await Call("sftp.download", new { sessionId = "smoke-sftp-1", transferId = "t2", remotePaths = new[] { remoteDir + "/ordner", remoteDir + "/gross.bin" }, localDir = back });
            Check("SFTP: Download identisch",
                File.ReadAllText(Path.Combine(back, "ordner", "a.txt")) == "Hallo SFTP" && new FileInfo(Path.Combine(back, "gross.bin")).Length == 500_000);
            await Call("sftp.rename", new { sessionId = "smoke-sftp-1", from = remoteDir + "/gross.bin", to = remoteDir + "/gross2.bin" });
            await Call("sftp.delete", new { sessionId = "smoke-sftp-1", path = remoteDir });
            var after = await Call("sftp.list", new { sessionId = "smoke-sftp-1", path = home });
            Check("SFTP: umbenennen und rekursiv löschen", !after.GetProperty("entries").EnumerateArray().Any(e => e.GetProperty("name").GetString() == "msv-smoke"));
            await Call("sftp.close", new { sessionId = "smoke-sftp-1" });

            // Jump Host: dieselbe Maschine als Jump Host und als Ziel
            var viaJump = await Call("servers.save", new { name = "smoke-via-jump", host, port, protocol = "ssh", username = user, password = pass, jumpHostId = id, group = "Server" });
            await Call("ssh.open", new { sessionId = "smoke-jump-1", serverId = viaJump.GetProperty("id").GetString(), cols = 100, rows = 30 });
            await Task.Delay(1500);
            await Call("ssh.input", new { sessionId = "smoke-jump-1", data = B64("echo JUMP-" + (7 * 6) + "\r\n") });
            Check("Jump Host: Verbindung über SSH-Kette", await WaitSsh("smoke-jump-1", "JUMP-42"));
            await Call("ssh.close", new { sessionId = "smoke-jump-1" });

            // Port Forwarding (lokal)
            var rule = await Call("forwards.save", new { name = "smoke", serverId = id, type = "local", bindHost = "127.0.0.1", bindPort = 22345, destHost = "127.0.0.1", destPort = port });
            await Call("forwards.start", new { id = rule.GetProperty("id").GetString() });
            using (var tcp = new System.Net.Sockets.TcpClient())
            {
                await tcp.ConnectAsync("127.0.0.1", 22345);
                var buf = new byte[16];
                var n = await tcp.GetStream().ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                Check("Port Forwarding: Tunnel liefert SSH-Banner", Encoding.ASCII.GetString(buf, 0, n).StartsWith("SSH-2.0"));
            }
            await Call("forwards.stop", new { id = rule.GetProperty("id").GetString() });

            // Dieselbe Verbindung durch die echte Oberfläche (xterm.js in WebView2)
            Emit(new { @event = "vault.changed" });
            await WaitFor("[...document.querySelectorAll('.hcard')].some(e => e.textContent.includes('smoke-ssh'))", 10);
            await Js("[...document.querySelectorAll('.hcard')].find(e => e.textContent.includes('smoke-ssh')).dispatchEvent(new MouseEvent('dblclick', { bubbles: true }))");
            var ui = await WaitFor("document.querySelector('.xterm-rows') && document.querySelector('.xterm-rows').innerText.includes('" + user + "')", 25);
            Check("Terminal in der Oberfläche zeigt die Windows-Eingabeaufforderung", ui);
            await Task.Delay(800);
            await Shot("06-terminal.png");

            await Js("document.querySelector('.float-tools button[title^=\"Dateien dieses Hosts\"]').click()");
            var sftpUi = await WaitFor("document.querySelectorAll('.sftp-pane .file-row').length > 0", 25);
            Check("SFTP-Ansicht zeigt lokale und Server-Dateien", sftpUi);
            await Task.Delay(800);
            await Shot("07-sftp.png");
            for (var c = 0; c < 2; c++) await Js("[...document.querySelectorAll('.tab:not(.home):not(.tab-add) .x')].at(-1)?.click()");
        }
        catch (Exception ex)
        {
            Check("SSH-/SFTP-Test", false, ex.Message);
        }
    }

    private async Task SmokeSteps()
    {
        // 1. Fenster + WebView2 + UI über den virtuellen Host
        Check("WebView2 gestartet", _web.CoreWebView2 != null, _web.CoreWebView2?.Environment.BrowserVersionString ?? "");
        var loaded = await WaitFor("document.querySelector('.unlock-card')");
        Check("Oberfläche geladen (Entsperr-Bildschirm)", loaded);
        if (!loaded)
        {
            await Shot("00-fehler.png");
            Check("Seiteninhalt", false, await Js("document.body ? document.body.innerText.slice(0,300) : 'kein body'"));
            return;
        }
        Check("Verbindung zum C#-Host (kein Browser-Mock)", await Js("!document.querySelector('.devbadge') && !!window.chrome.webview") == "true");
        await Shot("01-entsperren.png");

        // 2. Vault über die echte Oberfläche + echte C#-Bridge erstellen
        await Js(@"(() => {
          const set = (el, v) => { Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(el, v); el.dispatchEvent(new Event('input', { bubbles: true })); };
          const i = document.querySelectorAll('.unlock-card input');
          set(i[0], 'smoke-test-pw-1'); set(i[1], 'smoke-test-pw-1');
          document.querySelector('.unlock-card button.primary').click();
        })()");
        Check("Vault erstellt (PBKDF2 + AES-GCM auf Windows)", await WaitFor("document.querySelector('.searchbar')", 30));
        Check("Vault-Datei liegt auf der Platte und ist verschlüsselt",
            File.Exists(Path.Combine(_smokeDir!, "data", "vault.msv")) && !File.ReadAllText(Path.Combine(_smokeDir!, "data", "vault.msv"), Encoding.Latin1).Contains("smoke"));

        // 3. Hosts über die Bridge anlegen (wie das UI es tut) und Oberfläche aktualisieren lassen
        foreach (var (name, host, proto, os) in new[] {
            ("web-prod-01", "10.0.10.11", "ssh", "debian"), ("db-prod-01", "10.0.10.21", "ssh", "ubuntu"),
            ("homelab-proxmox", "192.168.1.10", "ssh", "proxmox"), ("smoke-rdp", "127.0.0.1", "rdp", ""), ("desktop-vnc", "10.0.30.8", "vnc", "") })
            await Call("servers.save", new { name, host, protocol = proto, username = "admin", os, group = proto == "ssh" ? "Server" : "Desktops" });
        Emit(new { @event = "vault.changed" });
        Check("5 Hosts in der Oberfläche sichtbar", await WaitFor("document.querySelectorAll('.hcard').length === 5"));
        await Task.Delay(400);
        await Shot("02-hosts.png");

        // 4. Einstellungen: lesen, schreiben, auf Platte
        var settings = await Call("settings.get");
        Check("Einstellungen lesen", settings.GetProperty("sshKeepAliveSeconds").GetInt32() == 30);
        await Call("settings.save", new { sshKeepAliveSeconds = 45, sshConnectTimeoutSeconds = 20, rdpColorDepth = 24, rdpClipboard = true, rdpDrives = false, rdpDynamicResize = true, rdpWarnOnCertificate = false });
        Check("Einstellungen gespeichert", File.Exists(Path.Combine(_smokeDir!, "data", "settings.json")) && (await Call("settings.get")).GetProperty("rdpColorDepth").GetInt32() == 24);
        await Js("[...document.querySelectorAll('.nav-item')].find(e => e.textContent.includes('Einstellungen')).click()");
        Check("Einstellungsseite öffnet", await WaitFor("document.querySelector('.settings .set-card')"));
        await Task.Delay(600);
        await Shot("03-einstellungen.png");

        // 5. Cloud-Zugangsdaten mit Windows DPAPI schützen
        await Call("sync.save", new { url = "https://example.supabase.co", anonKey = "anon", email = "a@b.ch", password = "super-geheim-123" });
        var syncFile = Path.Combine(_smokeDir!, "data", "sync.json");
        Check("DPAPI: Cloud-Passwort nicht im Klartext", File.Exists(syncFile) && !File.ReadAllText(syncFile).Contains("super-geheim-123"));
        Check("DPAPI: Passwort wieder lesbar", (await Call("sync.get")).GetProperty("hasPassword").GetBoolean());

        // 6. Master-Passwort ändern, sperren, mit neuem Passwort entsperren
        await Call("vault.changePassword", new { oldPassword = "smoke-test-pw-1", newPassword = "smoke-test-pw-2" });
        await Call("vault.lock");
        await Js("location.reload()");
        Check("Nach Neustart der Oberfläche: Entsperr-Bildschirm", await WaitFor("document.querySelector('.unlock-card')"));
        await Js(@"(() => {
          const set = (el, v) => { Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(el, v); el.dispatchEvent(new Event('input', { bubbles: true })); };
          set(document.querySelector('.unlock-card input'), 'smoke-test-pw-2');
          document.querySelector('.unlock-card button.primary').click();
        })()");
        Check("Entsperren mit neuem Passwort, Hosts noch da", await WaitFor("document.querySelectorAll('.hcard').length === 5", 30));

        // 7. Verlauf, Known Hosts, Snippets laufen durch die Bridge
        await Call("snippets.save", new { name = "Test", command = "ls", autoRun = true });
        Check("Snippets speichern/lesen", (await Call("snippets.list")).GetArrayLength() == 1);
        Check("Verlauf lesen", (await Call("history.list")).ValueKind == JsonValueKind.Array);

        // 8. RDP: natives Windows-Control im Tab erzeugen (kein Server nötig - es genügt, dass es startet)
        await Js("[...document.querySelectorAll('.hcard')].find(e => e.textContent.includes('smoke-rdp')).dispatchEvent(new MouseEvent('dblclick', { bubbles: true }))");
        Check("RDP-Tab geöffnet", await WaitFor("document.querySelector('.rdp-box')", 10));
        var gotStatus = false;
        for (var i = 0; i < 40 && !gotStatus; i++)
        {
            lock (_tap) gotStatus = _tap.Any(t => t.Contains("rdp.status"));
            if (!gotStatus) await Task.Delay(250);
        }
        var overlayText = await Js("(document.querySelector('.rdp-box .overlay-msg') || {}).innerText || ''");
        var rdpUnavailable = overlayText.Contains("mstscax");
        Check("RDP-Control (mstscax) ließ sich erzeugen und startete eine Verbindung", gotStatus && !rdpUnavailable, rdpUnavailable ? overlayText : "Status-Ereignis empfangen");
        await Task.Delay(1500);
        await Shot("04-rdp-html.png");
        ScreenShot("05-rdp-fenster.png");


        // 8b. Echter SSH-Server (OpenSSH unter Windows): Terminal, SFTP, Jump Host, Port Forwarding durch die komplette C#-Schicht
        await SmokeSshAsync();

        // 8c. RDP-Control schliessen, während es noch verbindet (typischer Fall: Tab vorschnell zumachen) - wie lange blockiert die Oberfläche?
        try
        {
            var rdp2 = await Call("servers.save", new { name = "smoke-rdp2", host = "127.0.0.1", port = 3389, protocol = "rdp", username = "admin", password = "x" });
            await Call("rdp.open", new { sessionId = "smoke-rdp-2", serverId = rdp2.GetProperty("id").GetString(), x = 0, y = 100, w = 800, h = 500 });
            await Task.Delay(400);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await Call("rdp.close", new { sessionId = "smoke-rdp-2" });
            sw.Stop();
            Check("RDP: Schliessen während des Verbindens blockiert nicht lange", sw.ElapsedMilliseconds < 2500, $"{sw.ElapsedMilliseconds} ms gesamt ({_rdp.LastCloseTimings})");
        }
        catch (Exception ex)
        {
            Check("RDP: Schliessen während des Verbindens", false, ex.Message);
        }

        // 9. Zurück, Fenster bleibt bedienbar
        await Js("[...document.querySelectorAll('.tab')].find(e => e.textContent.includes('Hosts')).click()");
        Check("Zurück zur Hosts-Ansicht", await WaitFor("document.querySelector('.searchbar')"));
        await Js("document.querySelector('.tab:not(.home):not(.tab-add) .x')?.click()");
        Check("Tab schliessen räumt das RDP-Control auf", await WaitFor("document.querySelectorAll('.tab:not(.home):not(.tab-add)').length === 0", 8));
    }
}
