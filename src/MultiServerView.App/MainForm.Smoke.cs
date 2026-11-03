using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using MultiServerView.Core;

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

        // 9. Zurück, Fenster bleibt bedienbar
        await Js("[...document.querySelectorAll('.tab')].find(e => e.textContent.includes('Hosts')).click()");
        Check("Zurück zur Hosts-Ansicht", await WaitFor("document.querySelector('.searchbar')"));
        await Js("document.querySelector('.tab:not(.home):not(.tab-add) .x')?.click()");
        Check("Tab schliessen räumt das RDP-Control auf", await WaitFor("document.querySelectorAll('.tab:not(.home):not(.tab-add)').length === 0", 8));
    }
}
