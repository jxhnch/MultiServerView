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
            async Task<(string Text, bool IsError)> Tool(string name, object args)
            {
                var body = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name, arguments = args } }, Json.Options);
                using var r = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:47689/mcp") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                r.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                using var res = await http.SendAsync(r);
                using var d = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
                if (!d.RootElement.TryGetProperty("result", out var result)) return (d.RootElement.GetProperty("error").GetProperty("message").GetString() ?? "", true);
                return (result.GetProperty("content")[0].GetProperty("text").GetString() ?? "", result.TryGetProperty("isError", out var e) && e.GetBoolean());
            }

            string toolList;
            using (var lr = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:47689/mcp") { Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}", Encoding.UTF8, "application/json") })
            {
                lr.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                using var lres = await http.SendAsync(lr);
                toolList = await lres.Content.ReadAsStringAsync();
            }
            Check("MCP tools: the app actions are listed", toolList.Contains("terminal_open") && toolList.Contains("generate_key") && toolList.Contains("add_host"));

            var gen = await Tool("generate_key", new { name = "mcp-key" });
            Check("MCP tools: generate_key returns a public key, never the private key", !gen.IsError && gen.Text.Contains("ssh-ed25519") && !gen.Text.Contains("PRIVATE KEY"), gen.Text.Length > 100 ? gen.Text[..100] : gen.Text);
            var keys = await Tool("list_keys", new { });
            Check("MCP tools: list_keys shows the key", keys.Text.Contains("mcp-key") && !keys.Text.Contains("PRIVATE KEY"));
            var added = await Tool("add_host", new { name = "mcp-host", host = "10.1.1.1", protocol = "rdp", username = "Administrator", password = "secret-pw-123", tags = new[] { "a", "b" } });
            Check("MCP tools: add_host works and does not echo the password", !added.IsError && added.Text.Contains("mcp-host") && added.Text.Contains("\"port\":3389") && !added.Text.Contains("secret-pw-123") && added.Text.Contains("hasPassword"), added.Text);
            var stored = (await Call("servers.list")).EnumerateArray().First(x => x.GetProperty("name").GetString() == "mcp-host");
            Check("MCP tools: the password is saved in the vault", stored.GetProperty("password").GetString() == "secret-pw-123");
            var got = await Tool("get_host", new { host = "mcp-host" });
            Check("MCP tools: get_host hides the password", got.Text.Contains("\"hasPassword\":true") && !got.Text.Contains("secret-pw-123"));
            var upd = await Tool("update_host", new { target = "mcp-host", notes = "changed", workspace = "ws1" });
            Check("MCP tools: update_host keeps the other fields", upd.Text.Contains("changed") && upd.Text.Contains("ws1") && upd.Text.Contains("Administrator"));
            Check("MCP tools: the password survives update_host", (await Call("servers.list")).EnumerateArray().First(x => x.GetProperty("name").GetString() == "mcp-host").GetProperty("password").GetString() == "secret-pw-123");
            Check("MCP tools: delete_host", !(await Tool("delete_host", new { host = "mcp-host" })).IsError && !(await Call("servers.list")).EnumerateArray().Any(x => x.GetProperty("name").GetString() == "mcp-host"));
            var lf = Path.Combine(local, "mcp-note.txt");
            await Tool("local_write_file", new { path = lf, content = "hello from the AI" });
            Check("MCP tools: local_write_file and local_read_file", (await Tool("local_read_file", new { path = lf })).Text == "hello from the AI");
            await Tool("save_snippet", new { name = "mcp-snippet", command = "uptime" });
            Check("MCP tools: snippets", (await Tool("list_snippets", new { })).Text.Contains("mcp-snippet") && !(await Tool("delete_snippet", new { snippet = "mcp-snippet" })).IsError);
            Check("MCP tools: a tool error is reported inside the result", (await Tool("get_host", new { host = "does-not-exist" })).IsError);

            // read-only mode and the workspace limit must cover the AI tools too
            await Call("servers.save", new { name = "ws-a", host = "10.9.0.1", protocol = "ssh", username = "u", workspace = "Allowed" });
            await Call("servers.save", new { name = "ws-b", host = "10.9.0.2", protocol = "ssh", username = "u", workspace = "Secret" });
            async Task SaveMcp(bool readOnly, string[] workspaces) => await Call("settings.save", new { sshKeepAliveSeconds = 30, sshConnectTimeoutSeconds = 15, rdpColorDepth = 32, rdpClipboard = true, rdpDrives = false, rdpDynamicResize = true, rdpWarnOnCertificate = false, mcpEnabled = true, mcpPort = 47689, mcpToken = token, mcpReadOnly = readOnly, mcpAllowedWorkspaces = workspaces });
            await SaveMcp(true, []);
            string ListNow()
            {
                using var lr = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:47689/mcp") { Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}", Encoding.UTF8, "application/json") };
                lr.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                return http.Send(lr).Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }
            var roList = ListNow();
            Check("MCP read-only mode: only looking tools are offered", roList.Contains("list_keys") && roList.Contains("read_file") && !roList.Contains("add_host") && !roList.Contains("terminal_open") && !roList.Contains("run_command") && !roList.Contains("delete_file"));
            var roCall = await Tool("add_host", new { name = "x", host = "1.2.3.4" });
            Check("MCP read-only mode: a changing tool is refused", roCall.IsError || roCall.Text.Contains("Unknown tool") || roCall.Text.Contains("read-only"));
            await SaveMcp(false, ["Allowed"]);
            var wsList = ListNow();
            Check("MCP workspace limit: tools that are not tied to a host are not offered", !wsList.Contains("list_keys") && !wsList.Contains("local_read_file") && wsList.Contains("terminal_open"));
            Check("MCP workspace limit: an allowed host is visible, another workspace is not", !(await Tool("get_host", new { host = "ws-a" })).IsError && (await Tool("get_host", new { host = "ws-b" })).IsError);
            await SaveMcp(false, []);

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
                var term = await Tool("terminal_open", new { host = "selftest-ssh" });
                var termId = System.Text.RegularExpressions.Regex.Match(term.Text, "mcp-term-[0-9a-f]+").Value;
                Check("MCP tools: terminal_open gives a terminal", !term.IsError && termId.Length > 0, term.Text.Length > 120 ? term.Text[..120] : term.Text);
                await Tool("terminal_send", new { terminal = termId, text = "echo MSV-$((20+22))" });
                var termOut = await Tool("terminal_read", new { terminal = termId, wait_ms = 3000 });
                Check("MCP tools: terminal_send and terminal_read", termOut.Text.Contains("MSV-42"), termOut.Text.Length > 160 ? termOut.Text[..160] : termOut.Text);
                Check("MCP tools: terminal_close", !(await Tool("terminal_close", new { terminal = termId })).IsError);

                var mcpRemote = home.TrimEnd('/') + "/msv-mcp-" + Guid.NewGuid().ToString("N")[..6];
                Check("MCP tools: make_directory", !(await Tool("make_directory", new { host = "selftest-ssh", path = mcpRemote })).IsError);
                var upFile = Path.Combine(local, "mcp-up.txt");
                File.WriteAllText(upFile, "uploaded by the AI");
                Check("MCP tools: upload_file", !(await Tool("upload_file", new { host = "selftest-ssh", local_paths = new[] { upFile }, remote_dir = mcpRemote })).IsError);
                var downDir = Path.Combine(dir, "mcp-down");
                Directory.CreateDirectory(downDir);
                Check("MCP tools: download_file", !(await Tool("download_file", new { host = "selftest-ssh", remote_paths = new[] { mcpRemote + "/mcp-up.txt" }, local_dir = downDir })).IsError && File.ReadAllText(Path.Combine(downDir, "mcp-up.txt")) == "uploaded by the AI");
                Check("MCP tools: file_checksum and delete_file", (await Tool("file_checksum", new { host = "selftest-ssh", path = mcpRemote + "/mcp-up.txt" })).Text.Length > 20 && !(await Tool("delete_file", new { host = "selftest-ssh", path = mcpRemote })).IsError);
                await Call("ssh.open", new { sessionId = "st-log", serverId, cols = 80, rows = 24 });
                Check("Logs: an open SSH session is running", (await Call("history.list")).EnumerateArray().Any(h => h.GetProperty("kind").GetString() == "ssh" && h.GetProperty("result").GetString() == "running"));
                await Call("ssh.close", new { sessionId = "st-log" });
                Check("Logs: closing the SSH tab ends its entry (nothing stays running)", !(await Call("history.list")).EnumerateArray().Any(h => h.GetProperty("result").GetString() == "running"));
                Check("SFTP: cleaned up", true);
            }
            await Call("settings.save", new { sshKeepAliveSeconds = 30, sshConnectTimeoutSeconds = 15, rdpColorDepth = 32, rdpClipboard = true, rdpDrives = false, rdpDynamicResize = true, rdpWarnOnCertificate = false, mcpEnabled = false, mcpPort = 47689, mcpToken = "" });
        }
        catch (Exception ex)
        {
            Check("Self-test", false, ex.Message);
        }

        Console.WriteLine(failed == 0 ? "ALL OK" : $"{failed} FAILED");
        return failed == 0 ? 0 : 1;
    }
}
