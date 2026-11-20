using System.Text.Json.Nodes;
using MultiServerView.Core.Mcp;
using Xunit;

namespace MultiServerView.Core.Tests;

public sealed class McpClientConfigTests : IDisposable
{
    private readonly string _root;
    private readonly string _home;
    private readonly string _appData;

    public McpClientConfigTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "msv-mcp-cfg-" + Guid.NewGuid().ToString("N"));
        _home = Path.Combine(_root, "home");
        _appData = Path.Combine(_root, "appdata");
        Directory.CreateDirectory(_home);
        Directory.CreateDirectory(_appData);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore cleanup failure */ }
    }

    [Fact]
    public void ResolvePath_maps_supported_clients_and_rejects_unsupported_ones()
    {
        var ws = Path.Combine(_root, "project");
        Assert.Equal(Path.Combine(_home, ".cursor", "mcp.json"), McpClientConfig.ResolvePath("cursor", _home, _appData));
        Assert.Equal(Path.Combine(_home, ".gemini", "config", "mcp_config.json"), McpClientConfig.ResolvePath("antigravity", _home, _appData));
        Assert.Equal(Path.Combine(_home, ".codex", "config.toml"), McpClientConfig.ResolvePath("codex", _home, _appData));
        Assert.Equal(Path.Combine(ws, ".vscode", "mcp.json"), McpClientConfig.ResolvePath("vscode", _home, _appData, ws));
        Assert.Null(McpClientConfig.ResolvePath("vscode", _home, _appData));
        Assert.Equal(Path.Combine(_appData, "Claude", "claude_desktop_config.json"), McpClientConfig.ResolvePath("desktop", _home, _appData));
        Assert.Null(McpClientConfig.ResolvePath("desktop", _home, ""));

        foreach (var unsupported in new[] { "claude-code", "other" })
        {
            Assert.Null(McpClientConfig.ResolvePath(unsupported, _home, _appData, ws));
            Assert.False(McpClientConfig.Status(unsupported, "http://127.0.0.1:7676/mcp", "tok", _home, _appData, ws).Supported);
        }
    }

    [Fact]
    public void Add_creates_missing_folder_and_file_without_backup()
    {
        var url = "http://127.0.0.1:7676/mcp";
        var token = "secret-token-123";

        var before = McpClientConfig.Status("antigravity", url, token, _home, _appData);
        Assert.True(before.Supported);
        Assert.False(before.Exists);
        Assert.False(before.Configured);
        Assert.False(before.UpToDate);

        var added = McpClientConfig.Add("antigravity", url, token, _home, _appData);
        Assert.True(added.Exists);
        Assert.True(added.Configured);
        Assert.True(added.UpToDate);
        Assert.Null(added.BackupPath);
        Assert.True(File.Exists(added.Path));
        Assert.False(File.Exists(added.Path + ".bak"));

        var doc = JsonNode.Parse(File.ReadAllText(added.Path))!.AsObject();
        var entry = doc["mcpServers"]!["multiserverview"]!.AsObject();
        Assert.Equal(url, entry["url"]!.GetValue<string>());
        Assert.Equal("Bearer " + token, entry["headers"]!["Authorization"]!.GetValue<string>());

        var after = McpClientConfig.Status("antigravity", url, token, _home, _appData);
        Assert.True(after.Exists);
        Assert.True(after.Configured);
        Assert.True(after.UpToDate);
    }

    [Fact]
    public void Add_treats_empty_file_as_empty_object_and_creates_backup()
    {
        var path = McpClientConfig.ResolvePath("antigravity", _home, _appData)!;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");

        var added = McpClientConfig.Add("antigravity", "http://127.0.0.1:7676/mcp", "tok1", _home, _appData);
        Assert.Equal(path + ".bak", added.BackupPath);
        Assert.True(File.Exists(path + ".bak"));
        Assert.Equal("", File.ReadAllText(path + ".bak"));
        Assert.True(McpClientConfig.Status("antigravity", "http://127.0.0.1:7676/mcp", "tok1", _home, _appData).UpToDate);
    }

    [Fact]
    public void Add_preserves_existing_servers_and_updates_when_token_or_port_changes()
    {
        var path = McpClientConfig.ResolvePath("cursor", _home, _appData)!;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const string original = """
            {
              "customSetting": true,
              "mcpServers": {
                "sqlite": {
                  "command": "sqlite-mcp-server",
                  "args": ["test.db"]
                }
              }
            }
            """;
        File.WriteAllText(path, original);

        var added = McpClientConfig.Add("cursor", "http://127.0.0.1:7676/mcp", "tok-a", _home, _appData);
        Assert.Equal(path + ".bak", added.BackupPath);
        Assert.Equal(original, File.ReadAllText(path + ".bak"));

        var outdated = McpClientConfig.Status("cursor", "http://127.0.0.1:7777/mcp", "tok-b", _home, _appData);
        Assert.True(outdated.Configured);
        Assert.False(outdated.UpToDate);

        McpClientConfig.Add("cursor", "http://127.0.0.1:7777/mcp", "tok-b", _home, _appData);
        var updated = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.True(updated["customSetting"]!.GetValue<bool>());
        Assert.Equal("sqlite-mcp-server", updated["mcpServers"]!["sqlite"]!["command"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:7777/mcp", updated["mcpServers"]!["multiserverview"]!["url"]!.GetValue<string>());
        Assert.Equal("Bearer tok-b", updated["mcpServers"]!["multiserverview"]!["headers"]!["Authorization"]!.GetValue<string>());
        Assert.True(McpClientConfig.Status("cursor", "http://127.0.0.1:7777/mcp", "tok-b", _home, _appData).UpToDate);
    }

    [Fact]
    public void Add_writes_mcp_remote_entry_for_claude_desktop()
    {
        var added = McpClientConfig.Add("desktop", "http://127.0.0.1:7676/mcp", "tok-desk", _home, _appData);
        var doc = JsonNode.Parse(File.ReadAllText(added.Path))!.AsObject();
        var entry = doc["mcpServers"]!["multiserverview"]!.AsObject();
        Assert.Equal("npx", entry["command"]!.GetValue<string>());
        var args = entry["args"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "-y", "mcp-remote", "http://127.0.0.1:7676/mcp", "--allow-http", "--header", "Authorization:Bearer tok-desk" }, args);
    }

    [Theory]
    [InlineData("{ broken")]
    [InlineData("{\n  // comment\n  \"mcpServers\": {}\n}")]
    [InlineData("{\n  \"mcpServers\": {},\n}")]
    [InlineData("[]")]
    [InlineData("{\"mcpServers\": []}")]
    public void Broken_or_non_strict_json_aborts_without_modifying_file_or_creating_backup(string content)
    {
        var path = McpClientConfig.ResolvePath("cursor", _home, _appData)!;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);

        var status = McpClientConfig.Status("cursor", "http://127.0.0.1:7676/mcp", "tok", _home, _appData);
        Assert.True(status.Exists);
        Assert.False(status.Configured);
        Assert.False(string.IsNullOrEmpty(status.Error));

        Assert.Throws<InvalidOperationException>(() => McpClientConfig.Add("cursor", "http://127.0.0.1:7676/mcp", "tok", _home, _appData));
        Assert.Throws<InvalidOperationException>(() => McpClientConfig.Remove("cursor", _home, _appData));
        Assert.Equal(content, File.ReadAllText(path));
        Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public void Remove_deletes_only_multiserverview_creates_backup_and_ignores_missing_entries()
    {
        var missing = McpClientConfig.Remove("antigravity", _home, _appData);
        Assert.False(missing.Exists);
        Assert.False(missing.Configured);
        Assert.Null(missing.BackupPath);

        var path = McpClientConfig.ResolvePath("antigravity", _home, _appData)!;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const string withoutMsv = """{"other":1,"mcpServers":{"keep":{"url":"https://example.com/mcp"}}}""";
        File.WriteAllText(path, withoutMsv);
        var noop = McpClientConfig.Remove("antigravity", _home, _appData);
        Assert.True(noop.Exists);
        Assert.False(noop.Configured);
        Assert.Null(noop.BackupPath);
        Assert.Equal(withoutMsv, File.ReadAllText(path));
        Assert.False(File.Exists(path + ".bak"));

        McpClientConfig.Add("antigravity", "http://127.0.0.1:7676/mcp", "tok", _home, _appData);
        var removed = McpClientConfig.Remove("antigravity", _home, _appData);
        Assert.False(removed.Configured);
        Assert.Equal(path + ".bak", removed.BackupPath);
        Assert.True(File.Exists(path + ".bak"));

        var doc = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(1, doc["other"]!.GetValue<int>());
        Assert.NotNull(doc["mcpServers"]!["keep"]);
        Assert.False(doc["mcpServers"]!.AsObject().ContainsKey("multiserverview"));
    }

    [Fact]
    public void Config_file_with_the_token_is_only_readable_by_its_owner_on_unix()
    {
        if (OperatingSystem.IsWindows()) return;
        var result = McpClientConfig.Add("cursor", "http://127.0.0.1:7676/mcp", "token-token-token-1234", _home, _appData);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(result.Path));
    }

    [Fact]
    public void Vscode_writes_under_servers_preserves_existing_settings_updates_and_removes()
    {
        var ws = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(ws);

        var added = McpClientConfig.Add("vscode", "http://127.0.0.1:7676/mcp", "tok-vsc-1", _home, _appData, ws);
        Assert.True(added.UpToDate);
        var path = Path.Combine(ws, ".vscode", "mcp.json");
        Assert.Equal(path, added.Path);

        var doc = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var entry = doc["servers"]!["multiserverview"]!.AsObject();
        Assert.Equal("http", entry["type"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:7676/mcp", entry["url"]!.GetValue<string>());
        Assert.Equal("Bearer tok-vsc-1", entry["headers"]!["Authorization"]!.GetValue<string>());

        // Preserve unrelated inputs and servers across updates.
        File.WriteAllText(path, """{"inputs":[{"id":"k"}],"servers":{"other":{"command":"echo"},"multiserverview":{"type":"http","url":"http://127.0.0.1:7676/mcp","headers":{"Authorization":"Bearer old"}}}}""");
        var outdated = McpClientConfig.Status("vscode", "http://127.0.0.1:7676/mcp", "tok-vsc-2", _home, _appData, ws);
        Assert.True(outdated.Configured);
        Assert.False(outdated.UpToDate);

        McpClientConfig.Add("vscode", "http://127.0.0.1:7676/mcp", "tok-vsc-2", _home, _appData, ws);
        var updated = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.NotNull(updated["inputs"]);
        Assert.NotNull(updated["servers"]!["other"]);
        Assert.Equal("Bearer tok-vsc-2", updated["servers"]!["multiserverview"]!["headers"]!["Authorization"]!.GetValue<string>());

        var removed = McpClientConfig.Remove("vscode", _home, _appData, ws);
        Assert.False(removed.Configured);
        var afterRemove = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.NotNull(afterRemove["servers"]!["other"]);
        Assert.False(afterRemove["servers"]!.AsObject().ContainsKey("multiserverview"));
    }

    [Fact]
    public void Codex_toml_creates_merges_updates_and_removes_preserving_comments_and_sections()
    {
        var path = McpClientConfig.ResolvePath("codex", _home, _appData)!;

        // Missing file -> creates .codex/config.toml without backup
        var added = McpClientConfig.Add("codex", "http://127.0.0.1:7676/mcp", "tok-c1", _home, _appData);
        Assert.Null(added.BackupPath);
        Assert.True(McpClientConfig.Status("codex", "http://127.0.0.1:7676/mcp", "tok-c1", _home, _appData).UpToDate);

        // Existing TOML with comments, multiline arrays, other servers and subtable
        const string existing = """
            # User Codex settings
            model = "gpt-5"

            [mcp_servers.other]
            command = "npx"
            args = [
              "-y",
              "some-server"
            ]

            [mcp_servers.multiserverview]
            url = "http://127.0.0.1:7676/mcp"

            [mcp_servers.multiserverview.http_headers]
            Authorization = "Bearer old"

            [projects."/tmp/demo"]
            trust_level = "trusted"
            """;
        File.WriteAllText(path, existing + "\n");

        var outdated = McpClientConfig.Status("codex", "http://127.0.0.1:8888/mcp", "tok-c2", _home, _appData);
        Assert.True(outdated.Configured);
        Assert.False(outdated.UpToDate);

        var updated = McpClientConfig.Add("codex", "http://127.0.0.1:8888/mcp", "tok-c2", _home, _appData);
        Assert.Equal(path + ".bak", updated.BackupPath);
        Assert.True(McpClientConfig.Status("codex", "http://127.0.0.1:8888/mcp", "tok-c2", _home, _appData).UpToDate);

        var text = File.ReadAllText(path);
        Assert.Contains("# User Codex settings", text);
        Assert.Contains("[mcp_servers.other]", text);
        Assert.Contains("[projects.\"/tmp/demo\"]", text);
        Assert.DoesNotContain("[mcp_servers.multiserverview.http_headers]", text);
        Assert.Contains("url = \"http://127.0.0.1:8888/mcp\"", text);
        Assert.Contains("Authorization = \"Bearer tok-c2\"", text);

        var removed = McpClientConfig.Remove("codex", _home, _appData);
        Assert.False(removed.Configured);
        var afterRemove = File.ReadAllText(path);
        Assert.DoesNotContain("multiserverview", afterRemove);
        Assert.Contains("[mcp_servers.other]", afterRemove);
        Assert.Contains("[projects.\"/tmp/demo\"]", afterRemove);

        // No-op when absent
        File.Delete(path + ".bak");
        var noop = McpClientConfig.Remove("codex", _home, _appData);
        Assert.Null(noop.BackupPath);
        Assert.False(File.Exists(path + ".bak"));
    }

    [Theory]
    [InlineData("[unclosed\n")]
    [InlineData("model = \"unclosed\n")]
    [InlineData("not a key value line\n")]
    [InlineData("text = \"\"\"\nunclosed multiline\n")]
    [InlineData("[mcp_servers.multiserverview]\nurl = \"a\"\n[mcp_servers.multiserverview]\nurl = \"b\"\n")]
    public void Broken_toml_aborts_without_modifying_file_or_creating_backup(string content)
    {
        var path = McpClientConfig.ResolvePath("codex", _home, _appData)!;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);

        var status = McpClientConfig.Status("codex", "http://127.0.0.1:7676/mcp", "tok", _home, _appData);
        Assert.True(status.Exists);
        Assert.False(status.Configured);
        Assert.False(string.IsNullOrEmpty(status.Error));

        Assert.Throws<InvalidOperationException>(() => McpClientConfig.Add("codex", "http://127.0.0.1:7676/mcp", "tok", _home, _appData));
        Assert.Throws<InvalidOperationException>(() => McpClientConfig.Remove("codex", _home, _appData));
        Assert.Equal(content, File.ReadAllText(path));
        Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public void StatusAll_reports_status_for_global_clients()
    {
        McpClientConfig.Add("antigravity", "http://127.0.0.1:7676/mcp", "tok-1", _home, _appData);
        McpClientConfig.Add("codex", "http://127.0.0.1:7676/mcp", "tok-old", _home, _appData);

        var all = McpClientConfig.StatusAll("http://127.0.0.1:7676/mcp", "tok-1", _home, _appData);
        Assert.True(all["antigravity"].UpToDate);
        Assert.True(all["codex"].Configured);
        Assert.False(all["codex"].UpToDate);
        Assert.False(all["cursor"].Configured);
        Assert.Contains("desktop", all.Keys);
    }
}
