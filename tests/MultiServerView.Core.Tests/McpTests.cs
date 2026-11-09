using System.Text.Json.Nodes;
using MultiServerView.Core.Mcp;
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
}
