using System.Text.Json.Nodes;
using MultiServerView.Core;
using MultiServerView.Core.Mcp;
using MultiServerView.Core.Sync;
using Xunit;

namespace MultiServerView.Core.Tests;

// These tests change the global SSH defaults (static): they must not run at the same time.
[Collection("GlobalSshDefaults")]
public sealed class McpSecurityTests : IDisposable
{
    private readonly string _dir;

    public McpSecurityTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "msv-mcp-sec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* ignore cleanup failure */ }
    }

    private sealed class ReadOnlyBackend(bool readOnly) : IMcpBackend
    {
        public bool IsReadOnly => readOnly;
        public bool ExecutedWriteOrCommand { get; private set; }

        public Task<IReadOnlyList<McpHost>> ListHostsAsync(string query) =>
            Task.FromResult<IReadOnlyList<McpHost>>(new[] { new McpHost("1", "web-1", "10.0.0.5", 22, "ssh", "Prod", "", "") });

        public Task<McpExecResult> RunCommandAsync(string host, string command, int timeoutSeconds, CancellationToken ct)
        {
            ExecutedWriteOrCommand = true;
            return Task.FromResult(new McpExecResult("ok", "", 0, false));
        }

        public Task<string> ReadFileAsync(string host, string path, int? startLine, int? maxLines, CancellationToken ct) => Task.FromResult("file-content");

        public Task WriteFileAsync(string host, string path, string content, CancellationToken ct)
        {
            ExecutedWriteOrCommand = true;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<McpFileEntry>> ListDirectoryAsync(string host, string path, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<McpFileEntry>>(new[] { new McpFileEntry("a.txt", false, 1) });
    }

    private static async Task<JsonObject> Call(McpHandler h, string json) =>
        (JsonObject)JsonNode.Parse((await h.HandleAsync(json))!)!;

    [Fact]
    public async Task Read_only_mode_hides_and_blocks_mutating_tools()
    {
        var backend = new ReadOnlyBackend(readOnly: true);
        var h = new McpHandler(backend);

        var list = await Call(h, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        var names = list["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "list_hosts", "read_file", "list_directory" }, names);

        var cmd = await Call(h, """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"run_command","arguments":{"host":"web-1","command":"rm -rf /"}}}""");
        Assert.True(cmd["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("read-only", cmd["result"]!["content"]![0]!["text"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);

        var write = await Call(h, """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"write_file","arguments":{"host":"web-1","path":"/tmp/x","content":"y"}}}""");
        Assert.True(write["result"]!["isError"]!.GetValue<bool>());
        Assert.False(backend.ExecutedWriteOrCommand);

        var read = await Call(h, """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"read_file","arguments":{"host":"web-1","path":"/etc/hosts"}}}""");
        Assert.False(read["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal("file-content", read["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void Settings_store_protects_mcp_token_on_disk_and_migrates_legacy_plaintext()
    {
        var path = Path.Combine(_dir, "settings.json");
        var store = new AppSettingsStore(path, new PlainProtector());
        var saved = store.Save(new AppSettings { McpEnabled = true, McpReadOnly = true, McpAllowedWorkspaces = new List<string> { " Prod ", "prod", "", "Dev" } });
        Assert.True(saved.McpToken.Length >= 32);
        Assert.Equal(new[] { "Prod", "Dev" }, saved.McpAllowedWorkspaces);
        Assert.True(saved.McpReadOnly);

        var diskJson = File.ReadAllText(path);
        Assert.DoesNotContain(saved.McpToken, diskJson);
        Assert.Contains("mcpTokenProtected", diskJson);

        var reloaded = new AppSettingsStore(path, new PlainProtector()).Current;
        Assert.Equal(saved.McpToken, reloaded.McpToken);
        Assert.True(reloaded.McpReadOnly);
        Assert.Equal(new[] { "Prod", "Dev" }, reloaded.McpAllowedWorkspaces);

        // Legacy plaintext settings.json migrates automatically without rotating the existing token.
        var legacyPath = Path.Combine(_dir, "legacy-settings.json");
        const string legacyToken = "0123456789abcdef0123456789abcdef";
        File.WriteAllText(legacyPath, $$"""{"mcpEnabled":true,"mcpPort":7676,"mcpToken":"{{legacyToken}}"}""");

        var migrated = new AppSettingsStore(legacyPath, new PlainProtector()).Current;
        Assert.Equal(legacyToken, migrated.McpToken);

        var upgradedJson = File.ReadAllText(legacyPath);
        Assert.DoesNotContain(legacyToken, upgradedJson);
        Assert.Contains("mcpTokenProtected", upgradedJson);
    }

    [Fact]
    public void Workspace_allowlist_filters_named_and_unassigned_hosts()
    {
        Assert.True(AppSettings.IsWorkspaceAllowed("Prod", null));
        Assert.True(AppSettings.IsWorkspaceAllowed("", new HashSet<string>()));

        var prodOnly = new HashSet<string>(new[] { "Prod" }, StringComparer.OrdinalIgnoreCase);
        Assert.True(AppSettings.IsWorkspaceAllowed("prod", prodOnly));
        Assert.False(AppSettings.IsWorkspaceAllowed("Staging", prodOnly));
        Assert.False(AppSettings.IsWorkspaceAllowed("", prodOnly));

        var unassignedOnly = new HashSet<string>(new[] { AppSettings.McpNoWorkspace }, StringComparer.OrdinalIgnoreCase);
        Assert.True(AppSettings.IsWorkspaceAllowed("", unassignedOnly));
        Assert.False(AppSettings.IsWorkspaceAllowed("Prod", unassignedOnly));
    }

    [Fact]
    public void History_store_records_mcp_audit_entries()
    {
        var path = Path.Combine(_dir, "history.json");
        var history = new HistoryStore(path);
        var server = new ServerEntry { Id = "h1", Name = "Raspy", Host = "192.168.1.38", Port = 22 };

        var id = history.Begin(server, "mcp");
        history.End(id, true, "read_file: /etc/os-release");

        var idFail = history.Begin(server, "mcp");
        history.End(idFail, false, "run_command: systemctl status — Connection refused");

        var entries = new HistoryStore(path).List();
        Assert.Equal(2, entries.Count);
        Assert.Equal("mcp", entries[0].Kind);
        Assert.Equal("error", entries[0].Result);
        Assert.Contains("Connection refused", entries[0].Message);
        Assert.Equal("mcp", entries[1].Kind);
        Assert.Equal("ok", entries[1].Result);
        Assert.Equal("read_file: /etc/os-release", entries[1].Message);
    }
}
