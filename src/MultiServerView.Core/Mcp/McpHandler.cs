using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MultiServerView.Core.Mcp;

/// <summary>What the MCP tools do. Implemented by the app on top of the vault and the SSH/SFTP code.</summary>
public interface IMcpBackend
{
    bool IsReadOnly => false;
    Task<IReadOnlyList<McpHost>> ListHostsAsync(string query);
    Task<McpExecResult> RunCommandAsync(string host, string command, int timeoutSeconds, CancellationToken ct);
    Task<string> ReadFileAsync(string host, string path, int? startLine, int? maxLines, CancellationToken ct);
    Task WriteFileAsync(string host, string path, string content, CancellationToken ct);
    Task<IReadOnlyList<McpFileEntry>> ListDirectoryAsync(string host, string path, CancellationToken ct);
}

public sealed record McpHost(string Id, string Name, string Address, int Port, string Protocol, string Workspace, string Group, string Tags, string Os = "", string Notes = "");
public sealed record McpExecResult(string StdOut, string StdErr, int ExitCode, bool TimedOut);
public sealed record McpFileEntry(string Name, bool IsDirectory, long Size);

/// <summary>A tool that is described by data (name, description, JSON schema) instead of being built into the handler.</summary>
public sealed record McpToolDef(string Name, string Description, JsonObject Schema, JsonObject? Annotations = null);

/// <summary>An image returned to the AI (screenshot).</summary>
public sealed record McpImage(string Base64, string MimeType);

/// <summary>What a tool returns: text and optionally images.</summary>
public sealed record McpToolOutput(string Text, IReadOnlyList<McpImage>? Images = null);

/// <summary>More tools on top of the five built-in ones. Implemented by the app (the same actions the user has in the UI).</summary>
public interface IMcpExtraTools
{
    IReadOnlyList<McpToolDef> Tools { get; }
    bool Has(string name);

    /// <summary>Runs the tool and returns the result for the AI (usually JSON text). Exceptions become tool errors.</summary>
    Task<McpToolOutput> CallAsync(string name, JsonObject args, CancellationToken ct);
}

/// <summary>
/// A minimal Model Context Protocol server (JSON-RPC 2.0 over HTTP, one request = one response).
/// Tools: list_hosts, run_command, read_file, write_file, list_directory.
/// </summary>
public sealed class McpHandler(IMcpBackend backend, IMcpExtraTools? extra = null)
{
    public const string ProtocolVersion = "2025-03-26";
    private static readonly string[] SupportedVersions = ["2025-11-25", "2025-06-18", "2025-03-26"];

    private static readonly JsonSerializerOptions Out = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    /// <summary>Handles one JSON-RPC message (or batch). null = nothing to answer (notifications only).</summary>
    public async Task<string?> HandleAsync(string requestJson, CancellationToken ct = default)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(requestJson);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return Error(null, -32700, "Invalid message: " + ex.Message);
        }

        if (node is JsonArray batch)
        {
            if (batch.Count == 0) return Error(null, -32600, "Invalid request.");
            var replies = new JsonArray();
            foreach (var item in batch)
            {
                var reply = await HandleOneAsync(item, ct);
                if (reply != null) replies.Add(reply);
            }
            return replies.Count == 0 ? null : replies.ToJsonString();
        }

        if (node is JsonObject)
            return (await HandleOneAsync(node, ct))?.ToJsonString();

        return Error(null, -32600, "Invalid request.");
    }

    private async Task<JsonObject?> HandleOneAsync(JsonNode? node, CancellationToken ct)
    {
        if (node is not JsonObject request) return ErrorObj(null, -32600, "Invalid request.");

        var hasId = request.ContainsKey("id");
        var id = request["id"]?.DeepClone();
        try
        {
            if (request["method"] is not JsonValue mv || !mv.TryGetValue<string>(out var method) || method.Length == 0)
            {
                if (!hasId) return null;
                throw new McpException(-32600, "Invalid request.");
            }
            var args = request["params"] as JsonObject;

            if (!hasId) return null; // notifications (e.g. notifications/initialized)

            JsonNode result = method switch
            {
                "initialize" => InitializeResult(args),
                "ping" => new JsonObject(),
                "tools/list" => new JsonObject { ["tools"] = AllTools() },
                "tools/call" => await CallToolAsync(args, ct),
                "resources/list" => new JsonObject { ["resources"] = new JsonArray() },
                "resources/templates/list" => new JsonObject { ["resourceTemplates"] = new JsonArray() },
                "prompts/list" => new JsonObject { ["prompts"] = new JsonArray() },
                _ => throw new McpException(-32601, "Method not found: " + method)
            };
            return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
        }
        catch (McpException ex)
        {
            return ErrorObj(id, ex.Code, ex.Message);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return ErrorObj(id, -32700, "Invalid message: " + ex.Message);
        }
    }

    private static JsonObject InitializeResult(JsonObject? args)
    {
        var requested = args?["protocolVersion"] is JsonValue pv && pv.TryGetValue<string>(out var v) ? v : null;
        var version = requested != null && SupportedVersions.Contains(requested, StringComparer.Ordinal) ? requested : ProtocolVersion;
        return new JsonObject
        {
            ["protocolVersion"] = version,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["serverInfo"] = new JsonObject { ["name"] = "MultiServerView", ["version"] = typeof(McpHandler).Assembly.GetName().Version?.ToString(3) ?? "1" },
            ["instructions"] = "Gives access to the servers saved in MultiServerView. Use list_hosts first, then run_command / read_file / write_file / list_directory with a host name or id."
        };
    }

    private static JsonObject ErrorObj(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    private static string Error(JsonNode? id, int code, string message) => ErrorObj(id, code, message).ToJsonString();

    private async Task<JsonNode> CallToolAsync(JsonObject? p, CancellationToken ct)
    {
        if (p?["name"] is not JsonValue nv || !nv.TryGetValue<string>(out var name) || name.Length == 0)
            throw new McpException(-32602, "Tool name missing.");
        var a = p["arguments"] as JsonObject ?? new JsonObject();

        try
        {
            if (backend.IsReadOnly && name is "run_command" or "write_file")
                return Result("The MCP server is in read-only mode. Disable read-only mode in MultiServerView (AI tools) to run commands or write files.", true);

            string text;
            switch (name)
            {
                case "list_hosts":
                    text = JsonSerializer.Serialize(await backend.ListHostsAsync(Str(a, "query")), Out);
                    break;
                case "run_command":
                {
                    var timeout = Int(a, "timeout_seconds", 60, 1, 900);
                    var r = await backend.RunCommandAsync(Required(Str(a, "host"), "host"), Required(Str(a, "command"), "command"), timeout, ct);
                    text = JsonSerializer.Serialize(new { exitCode = r.ExitCode, timedOut = r.TimedOut, stdout = r.StdOut, stderr = r.StdErr }, Out);
                    return Result(text, isError: r.ExitCode != 0 || r.TimedOut);
                }
                case "read_file":
                {
                    int? startLine = a.TryGetPropertyValue("start_line", out var sl) && sl != null ? Int(a, "start_line", 1, 1, int.MaxValue) : null;
                    int? maxLines = a.TryGetPropertyValue("max_lines", out var ml) && ml != null ? Int(a, "max_lines", 200, 1, 10_000) : null;
                    if (startLine != null && maxLines == null) maxLines = 200;
                    else if (maxLines != null && startLine == null) startLine = 1;
                    text = await backend.ReadFileAsync(Required(Str(a, "host"), "host"), Required(Str(a, "path"), "path"), startLine, maxLines, ct);
                    break;
                }
                case "write_file":
                    if (!a.TryGetPropertyValue("content", out var contentNode) || contentNode == null)
                        throw new McpException(-32602, "content is required.");
                    await backend.WriteFileAsync(Required(Str(a, "host"), "host"), Required(Str(a, "path"), "path"), Str(a, "content"), ct);
                    text = "File written.";
                    break;
                case "list_directory":
                {
                    var path = Str(a, "path");
                    text = JsonSerializer.Serialize(await backend.ListDirectoryAsync(Required(Str(a, "host"), "host"), path.Length > 0 ? path : ".", ct), Out);
                    break;
                }
                default:
                    if (extra != null && extra.Has(name))
                    {
                        var output = await extra.CallAsync(name, a, ct);
                        return Result(output.Text, false, output.Images);
                    }
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

    private static string Str(JsonObject a, string key)
    {
        if (!a.TryGetPropertyValue(key, out var node) || node == null) return "";
        if (node is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        throw new McpException(-32602, $"{key} must be a string.");
    }

    private static int Int(JsonObject a, string key, int fallback, int min, int max)
    {
        if (!a.TryGetPropertyValue(key, out var node) || node == null) return fallback;
        if (node is JsonValue v)
        {
            if (v.TryGetValue<int>(out var i)) return Math.Clamp(i, min, max);
            if (v.TryGetValue<long>(out var l)) return (int)Math.Clamp(l, min, max);
            if (v.TryGetValue<double>(out var d) && double.IsFinite(d)) return (int)Math.Clamp(Math.Round(d), min, max);
            if (v.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed))
                return (int)Math.Clamp(Math.Round(parsed), min, max);
        }
        throw new McpException(-32602, $"{key} must be an integer.");
    }

    private static string Required(string value, string name) => value.Length > 0 ? value : throw new McpException(-32602, name + " is required.");

    private static JsonObject Result(string text, bool isError, IReadOnlyList<McpImage>? images = null)
    {
        var content = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text });
        foreach (var image in images ?? [])
            content.Add(new JsonObject { ["type"] = "image", ["data"] = image.Base64, ["mimeType"] = image.MimeType });
        return new JsonObject { ["content"] = content, ["isError"] = isError };
    }

    private JsonArray AllTools()
    {
        var all = Tools();
        if (extra == null) return all;
        foreach (var t in extra.Tools)
        {
            var tool = new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["inputSchema"] = t.Schema.DeepClone() };
            if (t.Annotations != null) tool["annotations"] = t.Annotations.DeepClone();
            all.Add(tool);
        }
        return all;
    }

    private JsonArray Tools()
    {
        static JsonObject Prop(string type, string description) => new() { ["type"] = type, ["description"] = description };
        static JsonObject Ann(bool readOnly, bool destructive, bool idempotent, bool openWorld) => new()
        {
            ["readOnlyHint"] = readOnly,
            ["destructiveHint"] = destructive,
            ["idempotentHint"] = idempotent,
            ["openWorldHint"] = openWorld
        };
        static JsonObject Tool(string name, string description, JsonObject annotations, JsonObject props, params string[] required) => new()
        {
            ["name"] = name,
            ["description"] = description,
            ["annotations"] = annotations,
            ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = props, ["required"] = new JsonArray(required.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()) }
        };
        JsonObject host() => Prop("string", "Host name or id from list_hosts."); // a JSON node can only have one parent
        var tools = new JsonArray(
            Tool("list_hosts", "List the servers saved in MultiServerView (no passwords).",
                Ann(readOnly: true, destructive: false, idempotent: true, openWorld: false),
                new JsonObject { ["query"] = Prop("string", "Optional text to filter by name, address, workspace, OS or tag.") }),
            Tool("run_command", "Run a shell command on an SSH host (or a PowerShell command on a WinRM host) and return stdout, stderr and the exit code.",
                Ann(readOnly: false, destructive: true, idempotent: false, openWorld: true),
                new JsonObject { ["host"] = host(), ["command"] = Prop("string", "The command line."), ["timeout_seconds"] = Prop("integer", "Default 60, maximum 900.") }, "host", "command"),
            Tool("read_file", "Read a UTF-8 text file (or a line range of a large file) from an SSH host over SFTP.",
                Ann(readOnly: true, destructive: false, idempotent: true, openWorld: true),
                new JsonObject
                {
                    ["host"] = host(),
                    ["path"] = Prop("string", "Absolute or home-relative path."),
                    ["start_line"] = Prop("integer", "Optional 1-based start line for reading a slice (defaults to 1 when max_lines is set)."),
                    ["max_lines"] = Prop("integer", "Optional maximum number of lines to read (1-10000, defaults to 200 when start_line is set).")
                }, "host", "path"),
            Tool("write_file", "Write (overwrite) a text file on an SSH host over SFTP, preserving existing line endings and BOM.",
                Ann(readOnly: false, destructive: true, idempotent: true, openWorld: true),
                new JsonObject { ["host"] = host(), ["path"] = Prop("string", "Absolute or home-relative path."), ["content"] = Prop("string", "The new file content.") }, "host", "path", "content"),
            Tool("list_directory", "List a directory on an SSH host over SFTP.",
                Ann(readOnly: true, destructive: false, idempotent: true, openWorld: true),
                new JsonObject { ["host"] = host(), ["path"] = Prop("string", "Directory, default is the home folder.") }, "host"));
        if (backend.IsReadOnly)
        {
            // read-only mode: the tools that change something are not even offered
            foreach (var name in new[] { "run_command", "write_file" })
                for (var i = tools.Count - 1; i >= 0; i--)
                    if (tools[i]?["name"]?.GetValue<string>() == name) tools.RemoveAt(i);
        }
        return tools;
    }
}

public sealed class McpException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}
