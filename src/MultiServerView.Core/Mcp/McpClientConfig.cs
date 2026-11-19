using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MultiServerView.Core.Mcp;

public sealed record McpConfigResult(
    bool Supported,
    string Path,
    bool Exists,
    bool Configured,
    bool UpToDate,
    string? BackupPath = null,
    string? Error = null);

/// <summary>
/// Safely merges or removes the "multiserverview" entry in JSON config files of supported AI tools
/// (Cursor, Antigravity, Claude Desktop). Always creates a .bak copy before modifying an existing file.
/// </summary>
public static class McpClientConfig
{
    public const string ServerKey = "multiserverview";

    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly JsonSerializerOptions JsonOut = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string? ResolvePath(string client, string? userProfile = null, string? appData = null)
    {
        if (client is "cursor" or "antigravity")
        {
            var home = string.IsNullOrWhiteSpace(userProfile)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : userProfile;
            if (string.IsNullOrWhiteSpace(home)) return null;
            return client == "cursor"
                ? Path.Combine(home, ".cursor", "mcp.json")
                : Path.Combine(home, ".gemini", "config", "mcp_config.json");
        }

        if (client == "desktop")
        {
            if (appData == null)
            {
                if (!OperatingSystem.IsWindows()) return null;
                appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            }
            return string.IsNullOrWhiteSpace(appData)
                ? null
                : Path.Combine(appData, "Claude", "claude_desktop_config.json");
        }

        return null;
    }

    public static McpConfigResult Status(string client, string url, string token, string? userProfile = null, string? appData = null)
    {
        var path = ResolvePath(client, userProfile, appData);
        if (path == null) return new McpConfigResult(false, "", false, false, false);
        if (!File.Exists(path)) return new McpConfigResult(true, path, false, false, false);

        try
        {
            var root = ReadRoot(path);
            var existing = (root["mcpServers"] as JsonObject)?[ServerKey];
            if (existing == null) return new McpConfigResult(true, path, true, false, false);

            var upToDate = !string.IsNullOrWhiteSpace(url)
                           && !string.IsNullOrWhiteSpace(token)
                           && JsonNode.DeepEquals(existing, BuildEntry(client, url, token));
            return new McpConfigResult(true, path, true, true, upToDate);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return new McpConfigResult(true, path, true, false, false, Error: ex.Message);
        }
    }

    public static McpConfigResult Add(string client, string url, string token, string? userProfile = null, string? appData = null)
    {
        var path = ResolvePath(client, userProfile, appData)
                   ?? throw new InvalidOperationException($"Automatic configuration is not supported for \"{client}\" on this platform.");
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("MCP server URL and token are required.");

        var exists = File.Exists(path);
        var root = exists ? ReadRoot(path) : new JsonObject();

        if (root["mcpServers"] is not JsonObject servers)
        {
            servers = new JsonObject();
            root["mcpServers"] = servers;
        }
        servers[ServerKey] = BuildEntry(client, url, token);

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        string? backup = null;
        if (exists)
        {
            backup = path + ".bak";
            File.Copy(path, backup, overwrite: true);
        }

        WriteAtomic(path, root);
        return new McpConfigResult(true, path, true, true, true, BackupPath: backup);
    }

    public static McpConfigResult Remove(string client, string? userProfile = null, string? appData = null)
    {
        var path = ResolvePath(client, userProfile, appData)
                   ?? throw new InvalidOperationException($"Automatic configuration is not supported for \"{client}\" on this platform.");
        if (!File.Exists(path)) return new McpConfigResult(true, path, false, false, false);

        var root = ReadRoot(path);
        if (root["mcpServers"] is not JsonObject servers || !servers.ContainsKey(ServerKey))
            return new McpConfigResult(true, path, true, false, false);

        var backup = path + ".bak";
        File.Copy(path, backup, overwrite: true);
        servers.Remove(ServerKey);
        WriteAtomic(path, root);
        return new McpConfigResult(true, path, true, false, false, BackupPath: backup);
    }

    private static JsonObject BuildEntry(string client, string url, string token) =>
        client == "desktop"
            ? new JsonObject
            {
                ["command"] = "npx",
                ["args"] = new JsonArray("-y", "mcp-remote", url, "--allow-http", "--header", $"Authorization:Bearer {token}")
            }
            : new JsonObject
            {
                ["url"] = url,
                ["headers"] = new JsonObject { ["Authorization"] = $"Bearer {token}" }
            };

    private static JsonObject ReadRoot(string path)
    {
        var raw = File.ReadAllText(path, Encoding.UTF8);
        if (string.IsNullOrWhiteSpace(raw)) return new JsonObject();

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException(
                $"Could not parse \"{path}\" as JSON (invalid syntax, comments or trailing commas are not supported). Please edit the file manually.");
        }

        if (node is not JsonObject root)
            throw new InvalidOperationException($"Could not update \"{path}\": top-level JSON value is not an object.");

        if (root.TryGetPropertyValue("mcpServers", out var servers) && servers != null && servers is not JsonObject)
            throw new InvalidOperationException($"Could not update \"{path}\": \"mcpServers\" is not a JSON object.");

        return root;
    }

    private static void WriteAtomic(string path, JsonObject root)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, root.ToJsonString(JsonOut) + "\n", Utf8NoBom);
        File.Move(tmp, path, overwrite: true);
    }
}
