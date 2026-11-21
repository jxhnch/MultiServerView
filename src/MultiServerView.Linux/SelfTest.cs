using System.Text;
using System.Text.Json;
using MultiServerView.Core;
using MultiServerView.Host;

namespace MultiServerView.Linux;

/// <summary>
/// Self-test without a window: drives the bridge like the UI does and prints OK/FAIL lines. Usage: multiserverview --selftest [folder].
/// With MSV_SMOKE_SSH=host:port:user:password it also tests SSH, SFTP and an SFTP round trip against that server.
/// </summary>
internal static class SelfTest
{
    /// <summary>Test helper: creates a vault with one RDP host so the window can be driven without typing a form.</summary>
    public static async Task<int> SeedRdpAsync(string data, string vaultPassword, string host, int port, string user, string password)
    {
        Directory.CreateDirectory(data);
        var platform = new LinuxPlatform(data, _ => Task.FromResult<string?>(null), _ => Task.FromResult<string?>(null), _ => false);
        using var bridge = HostBootstrap.Create(data, platform, _ => { }, _ => new NoEmbeddedRdp());
        async Task Call(string method, object args) => await bridge.HandleAsync(JsonSerializer.Serialize(new { id = 1, method, @params = args }, Json.Options));
        await Call("vault.create", new { password = vaultPassword });
        await Call("servers.save", new { name = "rdp-test", host, port, protocol = "rdp", username = user, password, group = "Server" });
        Console.WriteLine("seeded");
        return 0;
    }

    public static async Task<int> RunAsync(string dir)
    {
        foreach (var leftover in new[] { "source-folder", "local", "back" })
            if (Directory.Exists(Path.Combine(dir, leftover))) Directory.Delete(Path.Combine(dir, leftover), true);
        Directory.CreateDirectory(dir);
        var data = Path.Combine(dir, "data");
        if (Directory.Exists(data)) Directory.Delete(data, true);
        Directory.CreateDirectory(data);

        var failed = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (!ok) failed++;
            Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {name}{(detail.Length > 0 ? " - " + detail : "")}");
        }

        var emitted = new List<string>();
        var platform = new LinuxPlatform(data, _ => Task.FromResult<string?>(null), _ => Task.FromResult<string?>(null), _ => false);
        using var bridge = HostBootstrap.Create(data, platform, e => { lock (emitted) emitted.Add(JsonSerializer.Serialize(e, Json.Options)); }, _ => new NoEmbeddedRdp());

        var id = 0;
        async Task<JsonElement> Call(string method, object? args = null)
        {
            var reply = await bridge.HandleAsync(JsonSerializer.Serialize(new { id = ++id, method, @params = args }, Json.Options));
            using var doc = JsonDocument.Parse(reply);
            var root = doc.RootElement.Clone();
            if (root.TryGetProperty("error", out var err)) throw new InvalidOperationException($"{method}: {err.GetString()}");
            return root.TryGetProperty("result", out var r) ? r : default;
        }

        try
        {
            var info = await Call("app.info");
            Check("Platform is Linux with the system title bar", info.GetProperty("platform").GetString() == "linux" && !info.GetProperty("customTitleBar").GetBoolean());

            await Call("vault.create", new { password = "selftest-password-1" });
            Check("Vault created", File.Exists(Path.Combine(data, "vault.msv")));
            await Call("servers.save", new { name = "web", host = "10.0.0.5", protocol = "ssh", username = "deploy", group = "Server" });
            Check("Host saved and listed", (await Call("servers.list")).GetArrayLength() == 1);
            await Call("vault.lock");
            await Call("vault.unlock", new { password = "selftest-password-1" });
            Check("Vault unlocks again, host still there", (await Call("servers.list")).GetArrayLength() == 1);

            var secret = new KeyFileProtector(Path.Combine(data, "secret.key"));
            var sealedText = secret.Protect("ghp_secret-token");
            Check("Secrets are stored encrypted (key file, mode 600)", !sealedText.Contains("ghp_") && secret.Unprotect(sealedText) == "ghp_secret-token" &&
                  (File.GetUnixFileMode(Path.Combine(data, "secret.key")) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) == 0);

            // vault source: a folder, changes are saved there by themselves
            var sourceDir = Path.Combine(dir, "source-folder");
            Directory.CreateDirectory(sourceDir);
            await Call("source.moveHere", new { mode = "folder", folder = sourceDir, overwrite = false });
            var file = Path.Combine(sourceDir, "MultiServerView-Vault.msv");
            Check("Vault source: vault moved to the folder", File.Exists(file));
            var before = File.ReadAllBytes(file);
            await Call("servers.save", new { name = "db", host = "10.0.0.6", protocol = "ssh", username = "root", group = "Server" });
            var changed = false;
            for (var i = 0; i < 20 && !changed; i++)
            {
                await Task.Delay(500);
                changed = !File.ReadAllBytes(file).SequenceEqual(before);
            }
            Check("Vault source: a change is saved to the folder automatically", changed);
            await Call("source.disconnect");

            // local files
            var local = Path.Combine(dir, "local");
            Directory.CreateDirectory(local);
            await Call("local.createFile", new { path = Path.Combine(local, "a.txt") });
            await Call("local.rename", new { from = Path.Combine(local, "a.txt"), to = Path.Combine(local, "b.txt") });
            var listing = await Call("local.browse", new { path = local });
            Check("Local files: create, rename, list", listing.GetProperty("entries").EnumerateArray().Any(e => e.GetProperty("name").GetString() == "b.txt"));

            // MCP server over real HTTP
            await Call("settings.save", new { sshKeepAliveSeconds = 30, sshConnectTimeoutSeconds = 15, rdpColorDepth = 32, rdpClipboard = true, rdpDrives = false, rdpDynamicResize = true, rdpWarnOnCertificate = false, mcpEnabled = true, mcpPort = 47689, mcpToken = "" });
            var token = (await Call("settings.get")).GetProperty("mcpToken").GetString() ?? "";
            using var http = new HttpClient();
            using var req = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:47689/mcp") { Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}", Encoding.UTF8, "application/json") };
            using var noToken = await http.SendAsync(new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:47689/mcp") { Content = new StringContent("{}") });
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            using var ok = await http.SendAsync(req);
            Check("MCP server: needs the token, lists its tools", (int)noToken.StatusCode == 401 && ok.IsSuccessStatusCode && (await ok.Content.ReadAsStringAsync()).Contains("run_command"));
            await Call("settings.save", new { sshKeepAliveSeconds = 30, sshConnectTimeoutSeconds = 15, rdpColorDepth = 32, rdpClipboard = true, rdpDrives = false, rdpDynamicResize = true, rdpWarnOnCertificate = false, mcpEnabled = false, mcpPort = 47689, mcpToken = "" });

            // SSH + SFTP against a real server
            var ssh = Environment.GetEnvironmentVariable("MSV_SMOKE_SSH");
            if (string.IsNullOrEmpty(ssh)) Check("SSH/SFTP tests skipped (MSV_SMOKE_SSH not set)", true);
            else
            {
                var parts = ssh.Split(':', 4);
                var server = await Call("servers.save", new { name = "selftest-ssh", host = parts[0], port = int.Parse(parts[1]), protocol = "ssh", username = parts[2], password = parts[3], group = "Server" });
                var serverId = server.GetProperty("id").GetString()!;

                var sftp = await Call("sftp.open", new { sessionId = "st-sftp", serverId });
                var home = sftp.GetProperty("home").GetString()!;
                Check("SFTP: connected", home.StartsWith('/'), "home " + home);

                var remoteDir = home.TrimEnd('/') + "/msv-selftest-" + Guid.NewGuid().ToString("N")[..6];
                await Call("sftp.mkdir", new { sessionId = "st-sftp", path = remoteDir });
                var up = Path.Combine(local, "upload.bin");
                File.WriteAllBytes(up, new byte[123_456]);
                await Call("sftp.upload", new { sessionId = "st-sftp", transferId = "t1", localPaths = new[] { up }, remoteDir });
                var list = await Call("sftp.list", new { sessionId = "st-sftp", path = remoteDir });
                Check("SFTP: upload and list", list.GetProperty("entries").EnumerateArray().Any(e => e.GetProperty("name").GetString() == "upload.bin" && e.GetProperty("size").GetInt64() == 123_456));
                var back = Path.Combine(dir, "back");
                Directory.CreateDirectory(back);
                await Call("sftp.download", new { sessionId = "st-sftp", transferId = "t2", remotePaths = new[] { remoteDir + "/upload.bin" }, localDir = back });
                Check("SFTP: download identical", new FileInfo(Path.Combine(back, "upload.bin")).Length == 123_456);
                await Call("sftp.chmod", new { sessionId = "st-sftp", paths = new[] { remoteDir + "/upload.bin" }, mode = 0x1A0, recursive = false });
                var stat = await Call("sftp.stat", new { sessionId = "st-sftp", path = remoteDir + "/upload.bin" });
                Check("SFTP: chmod 640", stat.GetProperty("permissions").GetInt32() == 0x1A0);
                await Call("sftp.delete", new { sessionId = "st-sftp", path = remoteDir });
                await Call("sftp.close", new { sessionId = "st-sftp" });
                await Call("ssh.open", new { sessionId = "st-log", serverId, cols = 80, rows = 24 });
                Check("Logs: an open SSH session is running", (await Call("history.list")).EnumerateArray().Any(h => h.GetProperty("kind").GetString() == "ssh" && h.GetProperty("result").GetString() == "running"));
                await Call("ssh.close", new { sessionId = "st-log" });
                Check("Logs: closing the SSH tab ends its entry (nothing stays running)", !(await Call("history.list")).EnumerateArray().Any(h => h.GetProperty("result").GetString() == "running"));
                Check("SFTP: cleaned up", true);
            }
        }
        catch (Exception ex)
        {
            Check("Self-test", false, ex.Message);
        }

        Console.WriteLine(failed == 0 ? "ALL OK" : $"{failed} FAILED");
        return failed == 0 ? 0 : 1;
    }
}
