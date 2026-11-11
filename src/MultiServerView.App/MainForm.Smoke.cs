using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using MultiServerView.Core;
using MultiServerView.Core.Ssh;
using MultiServerView.Core.Sync;

namespace MultiServerView.App;

/// <summary>
/// Self-test on a real Windows: starts the complete app (window, WebView2, bridge, vault, DPAPI, RDP control),
/// drives the interface by script, takes screenshots and writes a report. Usage: MultiServerView.exe --smoke &lt;folder&gt;
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
            catch { /* page is still loading */ }
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

    /// <summary>Photographs the whole window including native controls (e.g. RDP) from the screen.</summary>
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

    /// <summary>Turns the MCP server on, talks to it over real HTTP and turns it off again.</summary>
    private async Task SmokeMcpAsync()
    {
        const int port = 47688;
        try
        {
            await Call("settings.save", new { sshKeepAliveSeconds = 45, sshConnectTimeoutSeconds = 20, rdpColorDepth = 24, rdpClipboard = true, rdpDrives = false, rdpDynamicResize = true, rdpWarnOnCertificate = false, mcpEnabled = true, mcpPort = port, mcpToken = "" });
            var token = (await Call("settings.get")).GetProperty("mcpToken").GetString() ?? "";
            Check("MCP: token generated", token.Length >= 32);
            Check("MCP: server running", (await Call("mcp.status")).GetProperty("running").GetBoolean());

            using var http = new HttpClient();
            async Task<(int Status, string Body)> Post(string json, string? bearer)
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/mcp") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
                if (bearer != null) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer);
                using var res = await http.SendAsync(req);
                return ((int)res.StatusCode, await res.Content.ReadAsStringAsync());
            }

            Check("MCP: request without token is refused", (await Post("{}", null)).Status == 401);
            Check("MCP: wrong token is refused", (await Post("{}", "nope")).Status == 401);
            var list = await Post("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}", token);
            Check("MCP: tools are listed", list.Status == 200 && list.Body.Contains("run_command") && list.Body.Contains("list_directory"), Snip(list.Body, 80));
            var hosts = await Post("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"list_hosts\",\"arguments\":{}}}", token);
            Check("MCP: list_hosts returns the vault hosts", hosts.Status == 200 && hosts.Body.Contains("isError\":false"), Snip(hosts.Body, 120));
        }
        catch (Exception ex)
        {
            Check("MCP server", false, ex.Message);
        }
        finally
        {
            await Call("settings.save", new { sshKeepAliveSeconds = 45, sshConnectTimeoutSeconds = 20, rdpColorDepth = 24, rdpClipboard = true, rdpDrives = false, rdpDynamicResize = true, rdpWarnOnCertificate = false, mcpEnabled = false, mcpPort = port, mcpToken = "" });
        }
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
            Check("Self-test aborted", false, ex.ToString());
        }
        finally
        {
            var failed = _checks.Count(c => !c.Ok);
            var sb = new StringBuilder();
            foreach (var c in _checks) sb.AppendLine($"{(c.Ok ? "OK  " : "FAIL")} {c.Name}{(c.Detail.Length > 0 ? " – " + c.Detail : "")}");
            sb.AppendLine().AppendLine(failed == 0 ? "ALL OK" : $"{failed} FAILED");
            await File.WriteAllTextAsync(Path.Combine(_smokeDir!, "report.txt"), sb.ToString());
            Environment.ExitCode = failed == 0 ? 0 : 1;
            Log("Self-test finished - closing window");
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
            Check("SSH/SFTP tests skipped (no OpenSSH server on the runner)", true);
            return;
        }
        var parts = cfg.Split(':', 4);
        var (host, port, user, pass) = (parts[0], int.Parse(parts[1]), parts[2], parts[3]);

        try
        {
            var target = await Call("servers.save", new { name = "smoke-ssh", host, port, protocol = "ssh", username = user, password = pass, group = "Server" });
            var id = target.GetProperty("id").GetString()!;

            // Terminal: connect, send a command, get the output back
            // On the very first login Windows creates the user profile first - that can take longer, hence up to 3 attempts.
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
            Check("SSH: connected to the OpenSSH server (SSH.NET on Windows)", true);
            await Task.Delay(1500);
            await Call("ssh.input", new { sessionId = "smoke-ssh-1", data = B64("echo SMOKE-%USERNAME%-" + (6 * 7) + "\r\n") });
            Check("SSH: command executed, output comes back", await WaitSsh("smoke-ssh-1", "SMOKE-" + user + "-42"), Snip(SshText("smoke-ssh-1"), 160));
            await Call("ssh.resize", new { sessionId = "smoke-ssh-1", cols = 120, rows = 40 });
            Check("SSH: resize window", true);
            await Call("ssh.close", new { sessionId = "smoke-ssh-1" });

            // Host key was remembered, operating system detected
            Check("Known hosts: host key stored on first contact", (await Call("knownhosts.list")).GetArrayLength() >= 1);
            var os = "";
            for (var i = 0; i < 20 && os == ""; i++)
            {
                await Task.Delay(500);
                foreach (var s in (await Call("servers.list")).EnumerateArray())
                    if (s.GetProperty("id").GetString() == id) os = s.GetProperty("os").GetString() ?? "";
            }
            Check("Operating system detected", os.Length > 0, "detected: " + os);

            // Measurement: what does a Windows OpenSSH server return for the OS detection?
            var probeServer = new ServerEntry { Name = "probe", Host = host, Port = port, Username = user, Password = pass };
            using (var probe = new SshTerminalSession(probeServer, null, (_, _, _) => HostKeyTrust.Trusted))
            {
                await probe.ConnectAsync(80, 24);
                var raw = await probe.RunCommandAsync(OsDetector.ProbeCommand, TimeSpan.FromSeconds(8));
                Check("OS detection: command returned output", raw != null, Snip(raw ?? "(null)", 200));
                Check("OS detection: Windows server is recognized", OsDetector.Parse(raw ?? "") == "windows", "detected: '" + OsDetector.Parse(raw ?? "") + "'");
            }

            // SFTP + local file system on Windows
            var home = (await Call("sftp.open", new { sessionId = "smoke-sftp-1", serverId = id })).GetProperty("home").GetString()!;
            Check("SFTP: connected", home.StartsWith('/'), "Home: " + home);
            var listing = await Call("sftp.list", new { sessionId = "smoke-sftp-1", path = home });
            Check("SFTP: read home folder", listing.GetProperty("entries").ValueKind == JsonValueKind.Array, listing.GetProperty("path").GetString());

            var work = Path.Combine(_smokeDir!, "sftp-work");
            Directory.CreateDirectory(Path.Combine(work, "folder"));
            File.WriteAllText(Path.Combine(work, "folder", "a.txt"), "Hello SFTP");
            File.WriteAllBytes(Path.Combine(work, "large.bin"), new byte[500_000]);
            var local = await Call("local.browse", new { path = work });
            Check("Read local file system (Windows paths)", local.GetProperty("entries").GetArrayLength() == 2, local.GetProperty("path").GetString());
            var roots = await Call("local.roots");
            Check("Read local drives", roots.GetProperty("entries").GetArrayLength() >= 1, roots.GetProperty("entries")[0].GetProperty("name").GetString());

            var remoteDir = home.TrimEnd('/') + "/msv-smoke";
            await Call("sftp.mkdir", new { sessionId = "smoke-sftp-1", path = remoteDir });
            await Call("sftp.upload", new { sessionId = "smoke-sftp-1", transferId = "t1", localPaths = new[] { Path.Combine(work, "folder"), Path.Combine(work, "large.bin") }, remoteDir });
            var up = await Call("sftp.list", new { sessionId = "smoke-sftp-1", path = remoteDir });
            Check("SFTP: folder and file uploaded", up.GetProperty("entries").GetArrayLength() == 2);

            var back = Path.Combine(_smokeDir!, "sftp-back");
            Directory.CreateDirectory(back);
            await Call("sftp.download", new { sessionId = "smoke-sftp-1", transferId = "t2", remotePaths = new[] { remoteDir + "/folder", remoteDir + "/large.bin" }, localDir = back });
            Check("SFTP: download identical",
                File.ReadAllText(Path.Combine(back, "folder", "a.txt")) == "Hello SFTP" && new FileInfo(Path.Combine(back, "large.bin")).Length == 500_000);
            await Call("sftp.rename", new { sessionId = "smoke-sftp-1", from = remoteDir + "/large.bin", to = remoteDir + "/large2.bin" });
            await Call("sftp.delete", new { sessionId = "smoke-sftp-1", path = remoteDir });
            var after = await Call("sftp.list", new { sessionId = "smoke-sftp-1", path = home });
            Check("SFTP: rename and delete recursively", !after.GetProperty("entries").EnumerateArray().Any(e => e.GetProperty("name").GetString() == "msv-smoke"));
            await Call("sftp.close", new { sessionId = "smoke-sftp-1" });

            // Jump host: the same machine as jump host and as target
            var viaJump = await Call("servers.save", new { name = "smoke-via-jump", host, port, protocol = "ssh", username = user, password = pass, jumpHostId = id, group = "Server" });
            await Call("ssh.open", new { sessionId = "smoke-jump-1", serverId = viaJump.GetProperty("id").GetString(), cols = 100, rows = 30 });
            await Task.Delay(1500);
            await Call("ssh.input", new { sessionId = "smoke-jump-1", data = B64("echo JUMP-" + (7 * 6) + "\r\n") });
            Check("Jump host: connection through the SSH chain", await WaitSsh("smoke-jump-1", "JUMP-42"));
            await Call("ssh.close", new { sessionId = "smoke-jump-1" });

            // Port forwarding (local)
            var rule = await Call("forwards.save", new { name = "smoke", serverId = id, type = "local", bindHost = "127.0.0.1", bindPort = 22345, destHost = "127.0.0.1", destPort = port });
            await Call("forwards.start", new { id = rule.GetProperty("id").GetString() });
            using (var tcp = new System.Net.Sockets.TcpClient())
            {
                await tcp.ConnectAsync("127.0.0.1", 22345);
                var buf = new byte[16];
                var n = await tcp.GetStream().ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                Check("Port forwarding: tunnel delivers the SSH banner", Encoding.ASCII.GetString(buf, 0, n).StartsWith("SSH-2.0"));
            }
            await Call("forwards.stop", new { id = rule.GetProperty("id").GetString() });

            // The same connection through the real interface (xterm.js in WebView2)
            Emit(new { @event = "vault.changed" });
            await WaitFor("[...document.querySelectorAll('.hcard')].some(e => e.textContent.includes('smoke-ssh'))", 10);
            await Js("[...document.querySelectorAll('.hcard')].find(e => e.textContent.includes('smoke-ssh')).dispatchEvent(new MouseEvent('dblclick', { bubbles: true }))");
            var ui = await WaitFor("document.querySelector('.xterm-rows') && document.querySelector('.xterm-rows').innerText.includes('" + user + "')", 25);
            Check("Terminal in the interface shows the Windows command prompt", ui);
            await Task.Delay(800);
            await Shot("06-terminal.png");

            await Js("document.querySelector('.float-tools button[title^=\"Manage files of this host\"]').click()");
            var sftpUi = await WaitFor("document.querySelectorAll('.sftp-pane .file-row').length > 0", 25);
            Check("SFTP view shows local and server files", sftpUi);
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
        // 1. Window + WebView2 + UI via the virtual host
        Check("WebView2 started", _web.CoreWebView2 != null, _web.CoreWebView2?.Environment.BrowserVersionString ?? "");
        var loaded = await WaitFor("document.querySelector('.unlock-card')");
        Check("Interface loaded (unlock screen)", loaded);
        if (!loaded)
        {
            await Shot("00-error.png");
            Check("Page content", false, await Js("document.body ? document.body.innerText.slice(0,300) : 'no body'"));
            return;
        }
        Check("Connection to the C# host (no browser mock)", await Js("!document.querySelector('.devbadge') && !!window.chrome.webview") == "true");
        await Shot("01-unlock.png");

        // 2. Create the vault through the real interface + real C# bridge
        await Js(@"(() => {
          const set = (el, v) => { Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(el, v); el.dispatchEvent(new Event('input', { bubbles: true })); };
          const i = document.querySelectorAll('.unlock-card input');
          set(i[0], 'smoke-test-pw-1'); set(i[1], 'smoke-test-pw-1');
          document.querySelector('.unlock-card button.primary').click();
        })()");
        Check("Vault created (PBKDF2 + AES-GCM on Windows)", await WaitFor("document.querySelector('.searchbar')", 30));
        Check("Vault file is on disk and encrypted",
            File.Exists(Path.Combine(_smokeDir!, "data", "vault.msv")) && !File.ReadAllText(Path.Combine(_smokeDir!, "data", "vault.msv"), Encoding.Latin1).Contains("smoke"));

        // 3. Create hosts through the bridge (like the UI does) and let the interface refresh
        foreach (var (name, host, proto, os) in new[] {
            ("web-prod-01", "10.0.10.11", "ssh", "debian"), ("db-prod-01", "10.0.10.21", "ssh", "ubuntu"),
            ("homelab-proxmox", "192.168.1.10", "ssh", "proxmox"), ("smoke-rdp", "127.0.0.1", "rdp", ""), ("desktop-vnc", "10.0.30.8", "vnc", "") })
            await Call("servers.save", new { name, host, protocol = proto, username = "admin", os, group = proto == "ssh" ? "Server" : "Desktops" });
        Emit(new { @event = "vault.changed" });
        Check("5 hosts visible in the interface", await WaitFor("document.querySelectorAll('.hcard').length === 5"));
        await Task.Delay(400);
        await Shot("02-hosts.png");

        // 4. Settings: read, write, on disk
        var settings = await Call("settings.get");
        Check("Read settings", settings.GetProperty("sshKeepAliveSeconds").GetInt32() == 30);
        await Call("settings.save", new { sshKeepAliveSeconds = 45, sshConnectTimeoutSeconds = 20, rdpColorDepth = 24, rdpClipboard = true, rdpDrives = false, rdpDynamicResize = true, rdpWarnOnCertificate = false });
        Check("Settings saved", File.Exists(Path.Combine(_smokeDir!, "data", "settings.json")) && (await Call("settings.get")).GetProperty("rdpColorDepth").GetInt32() == 24);
        await SmokeMcpAsync();
        await Js("[...document.querySelectorAll('.nav-item')].find(e => e.textContent.includes('Settings')).click()");
        Check("Settings page opens", await WaitFor("document.querySelector('.settings .set-card')"));
        await Task.Delay(600);
        await Shot("03-settings.png");

        // 5. Protect cloud credentials with Windows DPAPI
        var dpapiFile = Path.Combine(_smokeDir!, "dpapi-check.json");
        var dpapiStore = new SyncSettingsStore(dpapiFile, new DpapiProtector());
        dpapiStore.Save(new SyncSettings { Url = "https://example.supabase.co", AnonKey = "anon", Email = "a@b.ch", Password = "super-secret-123" });
        Check("DPAPI: cloud password not in plain text", File.Exists(dpapiFile) && !File.ReadAllText(dpapiFile).Contains("super-secret-123"));
        Check("DPAPI: password readable again", dpapiStore.Load().Password == "super-secret-123");
        File.Delete(dpapiFile);

        // 5b. Vault source: a folder as the place of the vault, changes are saved there by themselves
        var sourceDir = Path.Combine(_smokeDir!, "source-folder");
        Directory.CreateDirectory(sourceDir);
        var sourceFile = Path.Combine(sourceDir, FolderRemote.FileName);
        var probe0 = await Call("source.probe", new { mode = "folder", folder = sourceDir });
        Check("Source: empty folder is recognized", probe0.GetProperty("kind").GetString() == "empty");
        await Call("source.moveHere", new { mode = "folder", folder = sourceDir, overwrite = false });
        Check("Source: vault moved to the folder", File.Exists(sourceFile));
        var probe1 = await Call("source.probe", new { mode = "folder", folder = sourceDir });
        Check("Source: the vault there is recognized as the own one", probe1.GetProperty("kind").GetString() == "vault" && probe1.GetProperty("sameVault").GetBoolean());
        var fileBefore = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(sourceFile)));
        var temp = await Call("servers.save", new { name = "saved-by-itself", host = "10.9.9.9", protocol = "ssh", username = "x", group = "Server" });
        var changed = false;
        for (var i = 0; i < 20 && !changed; i++)
        {
            await Task.Delay(500);
            changed = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(sourceFile))) != fileBefore;
        }
        Check("Source: a change is saved to the folder automatically", changed);
        var srcState = (await Call("source.get")).GetProperty("status").GetProperty("state").GetString();
        Check("Source: status is ready", srcState == "ready", srcState ?? "");
        await Call("source.disconnect");
        await Call("servers.delete", new { id = temp.GetProperty("id").GetString() }); // back to the 5 hosts the next steps expect
        Check("Source: back to this PC", !(await Call("source.get")).GetProperty("info").GetProperty("configured").GetBoolean());

        // 6. Change the master password, lock, unlock with the new password
        await Call("vault.changePassword", new { oldPassword = "smoke-test-pw-1", newPassword = "smoke-test-pw-2" });
        await Call("vault.lock");
        await Js("location.reload()");
        Check("After restarting the interface: unlock screen", await WaitFor("document.querySelector('.unlock-card')"));
        await Js(@"(() => {
          const set = (el, v) => { Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(el, v); el.dispatchEvent(new Event('input', { bubbles: true })); };
          set(document.querySelector('.unlock-card input'), 'smoke-test-pw-2');
          document.querySelector('.unlock-card button.primary').click();
        })()");
        Check("Unlock with the new password, hosts still there", await WaitFor("document.querySelectorAll('.hcard').length === 5", 30));

        // 7. History, known hosts, snippets go through the bridge
        await Call("snippets.save", new { name = "Test", command = "ls", autoRun = true });
        Check("Save/read snippets", (await Call("snippets.list")).GetArrayLength() == 1);
        Check("Read history", (await Call("history.list")).ValueKind == JsonValueKind.Array);

        // 8. RDP: create the native Windows control in a tab (no server needed - it is enough that it starts)
        await Js("[...document.querySelectorAll('.hcard')].find(e => e.textContent.includes('smoke-rdp')).dispatchEvent(new MouseEvent('dblclick', { bubbles: true }))");
        Check("RDP tab opened", await WaitFor("document.querySelector('.rdp-box')", 10));
        var gotStatus = false;
        for (var i = 0; i < 40 && !gotStatus; i++)
        {
            lock (_tap) gotStatus = _tap.Any(t => t.Contains("rdp.status"));
            if (!gotStatus) await Task.Delay(250);
        }
        var overlayText = await Js("(document.querySelector('.rdp-box .overlay-msg') || {}).innerText || ''");
        var rdpUnavailable = overlayText.Contains("mstscax");
        Check("RDP control (mstscax) could be created and started a connection", gotStatus && !rdpUnavailable, rdpUnavailable ? overlayText : "Status event received");
        await Task.Delay(1500);
        await Shot("04-rdp-html.png");
        ScreenShot("05-rdp-window.png");


        // 8b. Real SSH server (OpenSSH on Windows): terminal, SFTP, jump host, port forwarding through the complete C# layer
        await SmokeSshAsync();

        // 8c. Close the RDP control while it is still connecting (typical case: closing the tab too early) - how long does the interface block?
        try
        {
            var rdp2 = await Call("servers.save", new { name = "smoke-rdp2", host = "127.0.0.1", port = 3389, protocol = "rdp", username = "admin", password = "x" });
            await Call("rdp.open", new { sessionId = "smoke-rdp-2", serverId = rdp2.GetProperty("id").GetString(), x = 0, y = 100, w = 800, h = 500 });
            await Task.Delay(400);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await Call("rdp.close", new { sessionId = "smoke-rdp-2" });
            sw.Stop();
            Check("RDP: closing while connecting does not block for long", sw.ElapsedMilliseconds < 2500, $"{sw.ElapsedMilliseconds} ms total ({_rdp.LastCloseTimings})");
        }
        catch (Exception ex)
        {
            Check("RDP: closing while connecting", false, ex.Message);
        }

        // 9. Back, the window stays usable
        await Js("[...document.querySelectorAll('.tab')].find(e => e.textContent.includes('Hosts')).click()");
        Check("Back to the hosts view", await WaitFor("document.querySelector('.searchbar')"));
        await Js("document.querySelector('.tab:not(.home):not(.tab-add) .x')?.click()");
        Check("Closing the tab cleans up the RDP control", await WaitFor("document.querySelectorAll('.tab:not(.home):not(.tab-add)').length === 0", 8));

        // 10. Custom title bar: no Windows title bar, maximizing fills the work area, the UI learns about it
        try
        {
            var scale = DeviceDpi / 96.0;
            var chromeHeight = Height - ClientSize.Height;
            Check("Title bar: no Windows title bar any more", chromeHeight < 25 * scale, $"{chromeHeight}px frame top+bottom");
            Check("Title bar: window buttons in the UI", await WaitFor("document.querySelectorAll('.tabbar .win-btn').length === 3"));

            await Call("window.command", new { command = "maximize" });
            await Task.Delay(800);
            var work = Screen.FromControl(this).WorkingArea;
            Check("Maximizing fills the work area", WindowState == FormWindowState.Maximized && Math.Abs(ClientSize.Width - work.Width) <= 1 && Math.Abs(ClientSize.Height - work.Height) <= 1,
                $"Window {ClientSize.Width}x{ClientSize.Height}, work area {work.Width}x{work.Height}");
            Check("UI knows that the window is maximized", await WaitFor("document.documentElement.classList.contains('win-max')", 5));

            await Call("window.command", new { command = "maximize" });
            await Task.Delay(500);
            Check("Restore", WindowState == FormWindowState.Normal && await WaitFor("!document.documentElement.classList.contains('win-max')", 5));
        }
        catch (Exception ex)
        {
            Check("Titelleiste", false, ex.Message);
        }
    }
}
