using System.Text.Json;
using System.Text.Json.Nodes;

namespace MultiServerView.Core.Mcp;

/// <summary>What the MCP tools do. Implemented by the app on top of the vault and the SSH/SFTP code.</summary>
public interface IMcpBackend
{
    Task<IReadOnlyList<McpHost>> ListHostsAsync(string query);
    Task<McpExecResult> RunCommandAsync(string host, string command, int timeoutSeconds, CancellationToken ct);
    Task<string> ReadFileAsync(string host, string path, CancellationToken ct);
    Task WriteFileAsync(string host, string path, string content, CancellationToken ct);
    Task<IReadOnlyList<McpFileEntry>> ListDirectoryAsync(string host, string path, CancellationToken ct);
}

public sealed record McpHost(string Id, string Name, string Address, int Port, string Protocol, string Workspace, string Group, string Tags);
public sealed record McpExecResult(string StdOut, string StdErr, int ExitCode, bool TimedOut);
public sealed record McpFileEntry(string Name, bool IsDirectory, long Size);

/// <summary>
/// A minimal Model Context Protocol server (JSON-RPC 2.0 over HTTP, one request = one response).
/// Tools: list_hosts, run_command, read_file, write_file, list_directory.
/// </summary>
public sealed class McpHandler(IMcpBackend backend)
{
    public const string ProtocolVersion = "2025-03-26";

    private static readonly JsonSerializerOptions Out = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    /// <summary>Handles one JSON-RPC message. null = nothing to answer (a notification).</summary>
    public async Task<string?> HandleAsync(string requestJson, CancellationToken ct = default)
    {
        JsonNode? id = null;
        try
        {
            var request = JsonNode.Parse(requestJson) as JsonObject ?? throw new McpException(-32600, "Invalid request.");
            id = request["id"]?.DeepClone();
            var method = request["method"]?.GetValue<string>() ?? "";
            var args = request["params"] as JsonObject;

            if (id == null) return null; // notifications (e.g. notifications/initialized)

            JsonNode result = method switch
            {
                "initialize" => new JsonObject
                {
                    ["protocolVersion"] = ProtocolVersion,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "MultiServerView", ["version"] = typeof(McpHandler).Assembly.GetName().Version?.ToString(3) ?? "1" },
                    ["instructions"] = "Gives access to the servers saved in MultiServerView. Use list_hosts first, then run_command / read_file / write_file / list_directory with a host name or id."
                },
                "ping" => new JsonObject(),
                "tools/list" => new JsonObject { ["tools"] = Tools() },
                "tools/call" => await CallToolAsync(args, ct),
                _ => throw new McpException(-32601, "Method not found: " + method)
            };
            return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }.ToJsonString();
        }
        catch (McpException ex)
        {
            return Error(id, ex.Code, ex.Message);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return Error(id, -32700, "Invalid message: " + ex.Message);
        }
    }

    private static string Error(JsonNode? id, int code, string message) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }.ToJsonString();

    private async Task<JsonNode> CallToolAsync(JsonObject? p, CancellationToken ct)
    {
        var name = p?["name"]?.GetValue<string>() ?? throw new McpException(-32602, "Tool name missing.");
        var a = p["arguments"] as JsonObject ?? new JsonObject();
        string S(string key) => a[key]?.GetValue<string>() ?? "";

        try
        {
            string text;
            switch (name)
            {
                case "list_hosts":
                    text = JsonSerializer.Serialize(await backend.ListHostsAsync(S("query")), Out);
                    break;
                case "run_command":
                {
                    var timeout = a["timeout_seconds"] is JsonNode t ? Math.Clamp(t.GetValue<int>(), 1, 900) : 60;
                    var r = await backend.RunCommandAsync(Required(S("host"), "host"), Required(S("command"), "command"), timeout, ct);
                    text = JsonSerializer.Serialize(new { exitCode = r.ExitCode, timedOut = r.TimedOut, stdout = r.StdOut, stderr = r.StdErr }, Out);
                    return Result(text, isError: r.ExitCode != 0 || r.TimedOut);
                }
                case "read_file":
                    text = await backend.ReadFileAsync(Required(S("host"), "host"), Required(S("path"), "path"), ct);
                    break;
                case "write_file":
                    await backend.WriteFileAsync(Required(S("host"), "host"), Required(S("path"), "path"), a["content"]?.GetValue<string>() ?? throw new McpException(-32602, "content is required."), ct);
                    text = "File written.";
                    break;
                case "list_directory":
                    text = JsonSerializer.Serialize(await backend.ListDirectoryAsync(Required(S("host"), "host"), S("path").Length > 0 ? S("path") : ".", ct), Out);
                    break;
                default:
                    throw new McpException(-32602, "Unknown tool: " + name);
            }
            return Result(text, false);
        }
        catch (McpException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Tool errors are reported inside the result so the model can read and react to them.
            return Result(ex.Message, true);
        }
    }

    private static string Required(string value, string name) => value.Length > 0 ? value : throw new McpException(-32602, name + " is required.");

    private static JsonObject Result(string text, bool isError) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = isError
    };

    private static JsonArray Tools()
    {
        static JsonObject Prop(string type, string description) => new() { ["type"] = type, ["description"] = description };
        static JsonObject Tool(string name, string description, JsonObject props, params string[] required) => new()
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = props, ["required"] = new JsonArray(required.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()) }
        };
        JsonObject host() => Prop("string", "Host name or id from list_hosts."); // a JSON node can only have one parent
        return new JsonArray(
            Tool("list_hosts", "List the servers saved in MultiServerView (no passwords).", new JsonObject { ["query"] = Prop("string", "Optional text to filter by name, address, workspace or tag.") }),
            Tool("run_command", "Run a shell command on an SSH host and return stdout, stderr and the exit code.",
                new JsonObject { ["host"] = host(), ["command"] = Prop("string", "The command line."), ["timeout_seconds"] = Prop("integer", "Default 60, maximum 900.") }, "host", "command"),
            Tool("read_file", "Read a text file from an SSH host over SFTP (up to 2 MB).", new JsonObject { ["host"] = host(), ["path"] = Prop("string", "Absolute or home-relative path.") }, "host", "path"),
            Tool("write_file", "Write (overwrite) a text file on an SSH host over SFTP.", new JsonObject { ["host"] = host(), ["path"] = Prop("string", "Absolute or home-relative path."), ["content"] = Prop("string", "The new file content.") }, "host", "path", "content"),
            Tool("list_directory", "List a directory on an SSH host over SFTP.", new JsonObject { ["host"] = host(), ["path"] = Prop("string", "Directory, default is the home folder.") }, "host"));
    }
}

public sealed class McpException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}
