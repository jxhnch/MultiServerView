using System.Text.Json.Nodes;
using MultiServerView.Core;
using MultiServerView.Core.Mcp;
using MultiServerView.Core.Sftp;
using Xunit;

namespace MultiServerView.Core.Tests;

public class McpTests
{
    private sealed class FakeBackend : IMcpBackend
    {
        public string? LastHost, LastCommand;
        public int LastTimeout;

        public Task<IReadOnlyList<McpHost>> ListHostsAsync(string query) =>
            Task.FromResult<IReadOnlyList<McpHost>>(new[] { new McpHost("1", "web-1", "10.0.0.5", 22, "ssh", "Prod", "", "") }.Where(h => query.Length == 0 || h.Name.Contains(query)).ToList());

        public Task<McpExecResult> RunCommandAsync(string host, string command, int timeoutSeconds, CancellationToken ct)
        {
            LastHost = host; LastCommand = command; LastTimeout = timeoutSeconds;
            if (host == "bad") throw new InvalidOperationException("Host not found.");
            return Task.FromResult(new McpExecResult("hello\n", "", 0, false));
        }

        public Task<string> ReadFileAsync(string host, string path, CancellationToken ct) => Task.FromResult("content of " + path);
        public Task WriteFileAsync(string host, string path, string content, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<McpFileEntry>> ListDirectoryAsync(string host, string path, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<McpFileEntry>>(new[] { new McpFileEntry("a.txt", false, 3) });
    }

    private static async Task<JsonObject> Call(McpHandler h, string json) => (JsonObject)JsonNode.Parse((await h.HandleAsync(json))!)!;

    [Fact]
    public async Task Initialize_and_tools_list_describe_the_server()
    {
        var h = new McpHandler(new FakeBackend());
        var init = await Call(h, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""");
        Assert.Equal("MultiServerView", init["result"]!["serverInfo"]!["name"]!.GetValue<string>());

        var list = await Call(h, """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        var names = list["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "list_hosts", "run_command", "read_file", "write_file", "list_directory" }, names);
    }

    [Fact]
    public async Task Notifications_get_no_answer() =>
        Assert.Null(await new McpHandler(new FakeBackend()).HandleAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""));

    [Fact]
    public async Task Run_command_passes_arguments_and_returns_output()
    {
        var backend = new FakeBackend();
        var r = await Call(new McpHandler(backend), """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"run_command","arguments":{"host":"web-1","command":"uptime","timeout_seconds":5}}}""");
        Assert.Equal(("web-1", "uptime", 5), (backend.LastHost, backend.LastCommand, backend.LastTimeout));
        Assert.False(r["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("hello", r["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Backend_errors_become_tool_errors_not_protocol_errors()
    {
        var r = await Call(new McpHandler(new FakeBackend()), """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"run_command","arguments":{"host":"bad","command":"x"}}}""");
        Assert.Null(r["error"]);
        Assert.True(r["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal("Host not found.", r["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Unknown_methods_and_missing_arguments_are_protocol_errors()
    {
        var h = new McpHandler(new FakeBackend());
        Assert.Equal(-32601, (await Call(h, """{"jsonrpc":"2.0","id":5,"method":"nope"}"""))["error"]!["code"]!.GetValue<int>());
        Assert.Equal(-32602, (await Call(h, """{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"read_file","arguments":{"host":"a"}}}"""))["error"]!["code"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("2025-06-18", "2025-06-18")]
    [InlineData("2025-11-25", "2025-11-25")]
    [InlineData("2024-11-05", McpHandler.ProtocolVersion)]
    public async Task Initialize_negotiates_supported_protocol_versions(string requested, string expected)
    {
        var h = new McpHandler(new FakeBackend());
        var init = await Call(h, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"__VER__","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""".Replace("__VER__", requested));
        Assert.Equal(expected, init["result"]!["protocolVersion"]!.GetValue<string>());
    }

    [Fact]
    public async Task Batch_requests_handle_mixed_notifications_and_empty_batches()
    {
        var h = new McpHandler(new FakeBackend());
        var mixedRaw = await h.HandleAsync("""[{"jsonrpc":"2.0","method":"notifications/initialized"},{"jsonrpc":"2.0","id":1,"method":"ping"}]""");
        var mixed = JsonNode.Parse(mixedRaw!)!.AsArray();
        Assert.Single(mixed);
        Assert.Equal(1, mixed[0]!["id"]!.GetValue<int>());

        Assert.Null(await h.HandleAsync("""[{"jsonrpc":"2.0","method":"notifications/initialized"}]"""));

        var empty = await Call(h, "[]");
        Assert.Equal(-32600, empty["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task Discovery_fallbacks_return_empty_arrays()
    {
        var h = new McpHandler(new FakeBackend());
        Assert.Empty((await Call(h, """{"jsonrpc":"2.0","id":1,"method":"resources/list"}"""))["result"]!["resources"]!.AsArray());
        Assert.Empty((await Call(h, """{"jsonrpc":"2.0","id":2,"method":"resources/templates/list"}"""))["result"]!["resourceTemplates"]!.AsArray());
        Assert.Empty((await Call(h, """{"jsonrpc":"2.0","id":3,"method":"prompts/list"}"""))["result"]!["prompts"]!.AsArray());
        Assert.Equal(-32601, (await Call(h, """{"jsonrpc":"2.0","id":4,"method":"unknown/method"}"""))["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task Argument_types_are_coerced_or_validated_strictly()
    {
        var backend = new FakeBackend();
        var h = new McpHandler(backend);

        await Call(h, """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"run_command","arguments":{"host":"web-1","command":"id","timeout_seconds":5.0}}}""");
        Assert.Equal(5, backend.LastTimeout);

        await Call(h, """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"run_command","arguments":{"host":"web-1","command":"id","timeout_seconds":"12"}}}""");
        Assert.Equal(12, backend.LastTimeout);

        await Call(h, """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"run_command","arguments":{"host":"web-1","command":"id","timeout_seconds":1000000000000}}}""");
        Assert.Equal(900, backend.LastTimeout);

        var badTimeout = await Call(h, """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"run_command","arguments":{"host":"web-1","command":"id","timeout_seconds":"abc"}}}""");
        Assert.Equal(-32602, badTimeout["error"]!["code"]!.GetValue<int>());

        var badHost = await Call(h, """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"run_command","arguments":{"host":123,"command":"id"}}}""");
        Assert.Equal(-32602, badHost["error"]!["code"]!.GetValue<int>());

        var badMethod = await Call(h, """{"jsonrpc":"2.0","id":6,"method":"123"}""");
        Assert.Equal(-32601, badMethod["error"]!["code"]!.GetValue<int>());
        var nonStringMethod = await Call(h, """{"jsonrpc":"2.0","id":7,"method":123}""");
        Assert.Equal(-32600, nonStringMethod["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public void Settings_normalize_rotates_token_across_disable_and_enable()
    {
        var s = new AppSettings { McpEnabled = true };
        s.Normalize();
        var first = s.McpToken;
        Assert.True(first.Length >= 32);

        s.McpEnabled = false;
        s.Normalize();
        Assert.Equal("", s.McpToken);

        s.McpEnabled = true;
        s.Normalize();
        Assert.True(s.McpToken.Length >= 32);
        Assert.NotEqual(first, s.McpToken);
    }

    [Fact]
    public void Sftp_normalize_expands_tilde_to_home()
    {
        Assert.Equal("/home/alice", SftpSession.Normalize("/home/alice", "~"));
        Assert.Equal("/home/alice/.ssh/config", SftpSession.Normalize("/home/alice", "~/.ssh/config"));
    }
}
