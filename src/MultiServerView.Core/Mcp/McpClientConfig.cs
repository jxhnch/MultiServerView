using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

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
/// Safely merges or removes the "multiserverview" entry in JSON/TOML config files of supported AI tools
/// (Cursor, Antigravity, Claude Desktop, VS Code, Codex). Always creates a .bak copy before modifying an existing file.
/// </summary>
public static class McpClientConfig
{
    public const string ServerKey = "multiserverview";

    private static readonly string[] GlobalClients = ["codex", "cursor", "antigravity", "desktop"];
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly JsonSerializerOptions JsonOut = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string? ResolvePath(string client, string? userProfile = null, string? appData = null, string? workspaceDir = null)
    {
        if (client is "cursor" or "antigravity" or "codex")
        {
            var home = string.IsNullOrWhiteSpace(userProfile)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : userProfile;
            if (string.IsNullOrWhiteSpace(home)) return null;
            return client switch
            {
                "cursor" => Path.Combine(home, ".cursor", "mcp.json"),
                "antigravity" => Path.Combine(home, ".gemini", "config", "mcp_config.json"),
                _ => Path.Combine(home, ".codex", "config.toml")
            };
        }

        if (client == "vscode")
            return string.IsNullOrWhiteSpace(workspaceDir) ? null : Path.Combine(workspaceDir, ".vscode", "mcp.json");

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

    public static Dictionary<string, McpConfigResult> StatusAll(string url, string token, string? userProfile = null, string? appData = null)
    {
        var results = new Dictionary<string, McpConfigResult>(StringComparer.Ordinal);
        foreach (var c in GlobalClients)
            results[c] = Status(c, url, token, userProfile, appData);
        return results;
    }

    public static McpConfigResult Status(string client, string url, string token, string? userProfile = null, string? appData = null, string? workspaceDir = null)
    {
        var path = ResolvePath(client, userProfile, appData, workspaceDir);
        if (path == null) return new McpConfigResult(false, "", false, false, false);
        if (!File.Exists(path)) return new McpConfigResult(true, path, false, false, false);

        try
        {
            if (client == "codex") return StatusToml(path, url, token);

            var serversKey = ServersKey(client);
            var root = ReadRoot(path, serversKey);
            var existing = (root[serversKey] as JsonObject)?[ServerKey];
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

    public static McpConfigResult Add(string client, string url, string token, string? userProfile = null, string? appData = null, string? workspaceDir = null)
    {
        var path = ResolvePath(client, userProfile, appData, workspaceDir)
                   ?? throw new InvalidOperationException($"Automatic configuration is not supported for \"{client}\" on this platform.");
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("MCP server URL and token are required.");

        if (client == "codex") return AddToml(path, url, token);

        var serversKey = ServersKey(client);
        var exists = File.Exists(path);
        var root = exists ? ReadRoot(path, serversKey) : new JsonObject();

        if (root[serversKey] is not JsonObject servers)
        {
            servers = new JsonObject();
            root[serversKey] = servers;
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

        WriteAtomicText(path, root.ToJsonString(JsonOut) + "\n");
        return new McpConfigResult(true, path, true, true, true, BackupPath: backup);
    }

    public static McpConfigResult Remove(string client, string? userProfile = null, string? appData = null, string? workspaceDir = null)
    {
        var path = ResolvePath(client, userProfile, appData, workspaceDir)
                   ?? throw new InvalidOperationException($"Automatic configuration is not supported for \"{client}\" on this platform.");
        if (!File.Exists(path)) return new McpConfigResult(true, path, false, false, false);

        if (client == "codex") return RemoveToml(path);

        var serversKey = ServersKey(client);
        var root = ReadRoot(path, serversKey);
        if (root[serversKey] is not JsonObject servers || !servers.ContainsKey(ServerKey))
            return new McpConfigResult(true, path, true, false, false);

        var backup = path + ".bak";
        File.Copy(path, backup, overwrite: true);
        servers.Remove(ServerKey);
        WriteAtomicText(path, root.ToJsonString(JsonOut) + "\n");
        return new McpConfigResult(true, path, true, false, false, BackupPath: backup);
    }

    private static string ServersKey(string client) => client == "vscode" ? "servers" : "mcpServers";

    private static JsonObject BuildEntry(string client, string url, string token) => client switch
    {
        "desktop" => new JsonObject
        {
            ["command"] = "npx",
            ["args"] = new JsonArray("-y", "mcp-remote", url, "--allow-http", "--header", $"Authorization:Bearer {token}")
        },
        "vscode" => new JsonObject
        {
            ["type"] = "http",
            ["url"] = url,
            ["headers"] = new JsonObject { ["Authorization"] = $"Bearer {token}" }
        },
        _ => new JsonObject
        {
            ["url"] = url,
            ["headers"] = new JsonObject { ["Authorization"] = $"Bearer {token}" }
        }
    };

    private static JsonObject ReadRoot(string path, string serversKey)
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

        if (root.TryGetPropertyValue(serversKey, out var servers) && servers != null && servers is not JsonObject)
            throw new InvalidOperationException($"Could not update \"{path}\": \"{serversKey}\" is not a JSON object.");

        return root;
    }

    // ------------------------------------------------------------------------------------------------ Codex TOML

    private sealed record TomlSection(int StartLine, int EndLine, bool IsMainTarget, bool IsSubTarget);

    private sealed record TomlDoc(List<string> Lines, string Newline, List<TomlSection> TargetSections);

    private static McpConfigResult StatusToml(string path, string url, string token)
    {
        var doc = ReadToml(path);
        var main = doc.TargetSections.FirstOrDefault(s => s.IsMainTarget);
        if (main == null) return new McpConfigResult(true, path, true, false, false);

        var body = string.Join("\n", doc.Lines.Skip(main.StartLine + 1).Take(main.EndLine - main.StartLine - 1));
        var subBodies = doc.TargetSections.Where(s => s.IsSubTarget)
            .Select(s => string.Join("\n", doc.Lines.Skip(s.StartLine + 1).Take(s.EndLine - s.StartLine - 1)))
            .ToList();

        var expectedUrl = !string.IsNullOrWhiteSpace(url) && TryExtractTomlString(body, "url", out var gotUrl) && gotUrl == url;
        var hasEnvToken = Regex.IsMatch(body, @"^\s*bearer_token_env_var\s*=", RegexOptions.Multiline);
        var authOk = false;
        if (!string.IsNullOrWhiteSpace(token))
        {
            if (TryExtractInlineHeaderAuth(body, out var inlineAuth)) authOk = inlineAuth == "Bearer " + token;
            else if (subBodies.Count == 1 && TryExtractTomlString(subBodies[0], "Authorization", out var subAuth)) authOk = subAuth == "Bearer " + token;
        }

        return new McpConfigResult(true, path, true, true, expectedUrl && !hasEnvToken && authOk);
    }

    private static McpConfigResult AddToml(string path, string url, string token)
    {
        var exists = File.Exists(path);
        var doc = exists ? ReadToml(path) : new TomlDoc(new List<string>(), Environment.NewLine, new List<TomlSection>());
        var block = BuildCodexBlock(url, token);
        var outLines = new List<string>(doc.Lines);

        if (doc.TargetSections.Count > 0)
        {
            // Remove subtables (or extra ranges) back-to-front, replacing the main section in place.
            foreach (var sec in doc.TargetSections.OrderByDescending(s => s.StartLine))
            {
                var count = sec.EndLine - sec.StartLine;
                if (sec.IsMainTarget)
                {
                    // Trim trailing blank lines inside the old section only when appending at EOF, keeping spacing before next section.
                    var hadTrailingBlank = sec.EndLine < outLines.Count && count > 1 && string.IsNullOrWhiteSpace(outLines[sec.EndLine - 1]);
                    outLines.RemoveRange(sec.StartLine, count);
                    var insert = hadTrailingBlank ? block.Concat(new[] { "" }).ToArray() : block;
                    outLines.InsertRange(sec.StartLine, insert);
                }
                else
                {
                    outLines.RemoveRange(sec.StartLine, count);
                }
            }
        }
        else
        {
            while (outLines.Count > 0 && string.IsNullOrWhiteSpace(outLines[^1])) outLines.RemoveAt(outLines.Count - 1);
            if (outLines.Count > 0) outLines.Add("");
            outLines.AddRange(block);
        }

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        string? backup = null;
        if (exists)
        {
            backup = path + ".bak";
            File.Copy(path, backup, overwrite: true);
        }

        WriteAtomicText(path, string.Join(doc.Newline, outLines) + doc.Newline);
        return new McpConfigResult(true, path, true, true, true, BackupPath: backup);
    }

    private static McpConfigResult RemoveToml(string path)
    {
        var doc = ReadToml(path);
        if (doc.TargetSections.Count == 0) return new McpConfigResult(true, path, true, false, false);

        var outLines = new List<string>(doc.Lines);
        foreach (var sec in doc.TargetSections.OrderByDescending(s => s.StartLine))
            outLines.RemoveRange(sec.StartLine, sec.EndLine - sec.StartLine);

        while (outLines.Count > 0 && string.IsNullOrWhiteSpace(outLines[^1])) outLines.RemoveAt(outLines.Count - 1);
        var backup = path + ".bak";
        File.Copy(path, backup, overwrite: true);
        var text = outLines.Count == 0 ? "" : string.Join(doc.Newline, outLines) + doc.Newline;
        WriteAtomicText(path, text);
        return new McpConfigResult(true, path, true, false, false, BackupPath: backup);
    }

    private static string[] BuildCodexBlock(string url, string token) =>
    [
        $"[mcp_servers.{ServerKey}]",
        $"url = \"{EscapeToml(url)}\"",
        $"http_headers = {{ Authorization = \"Bearer {EscapeToml(token)}\" }}"
    ];

    private static string EscapeToml(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static TomlDoc ReadToml(string path)
    {
        var raw = File.ReadAllText(path, Encoding.UTF8);
        var newline = raw.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        if (string.IsNullOrWhiteSpace(raw)) return new TomlDoc(new List<string>(), newline, new List<TomlSection>());

        var normalized = raw.Replace("\r\n", "\n");
        if (normalized.EndsWith('\n')) normalized = normalized[..^1];
        var lines = normalized.Split('\n').ToList();

        string? multiline = null;
        var depth = 0;
        var sections = new List<(int Start, bool IsMain, bool IsSub, bool IsTarget)>();
        var mainCount = 0;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (multiline == null && depth == 0)
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith('['))
                {
                    if (!TryParseTableHeader(trimmed, out var parts, out var isArray))
                        throw new InvalidOperationException($"Could not parse \"{path}\" as TOML (invalid table header on line {i + 1}). Please edit the file manually.");
                    var isMain = !isArray && parts.Count == 2 && parts[0] == "mcp_servers" && parts[1] == ServerKey;
                    var isSub = parts.Count > 2 && parts[0] == "mcp_servers" && parts[1] == ServerKey;
                    if (isArray && parts.Count == 2 && parts[0] == "mcp_servers" && parts[1] == ServerKey)
                        throw new InvalidOperationException($"Could not update \"{path}\": \"mcp_servers.{ServerKey}\" is an array table.");
                    if (isMain) mainCount++;
                    if (mainCount > 1)
                        throw new InvalidOperationException($"Could not update \"{path}\": duplicate [mcp_servers.{ServerKey}] sections found.");
                    sections.Add((i, isMain, isSub, isMain || isSub));
                    continue;
                }
                if (!HasCodeOrBlankOrComment(trimmed))
                    throw new InvalidOperationException($"Could not parse \"{path}\" as TOML (invalid line {i + 1}). Please edit the file manually.");
            }

            if (!ScanTomlLine(line, ref multiline, ref depth))
                throw new InvalidOperationException($"Could not parse \"{path}\" as TOML (unbalanced quotes or brackets near line {i + 1}). Please edit the file manually.");
        }

        if (multiline != null || depth != 0)
            throw new InvalidOperationException($"Could not parse \"{path}\" as TOML (unclosed string or bracket). Please edit the file manually.");

        var targets = new List<TomlSection>();
        for (var s = 0; s < sections.Count; s++)
        {
            if (!sections[s].IsTarget) continue;
            var end = s + 1 < sections.Count ? sections[s + 1].Start : lines.Count;
            targets.Add(new TomlSection(sections[s].Start, end, sections[s].IsMain, sections[s].IsSub));
        }
        return new TomlDoc(lines, newline, targets);
    }

    private static bool TryParseTableHeader(string trimmed, out List<string> parts, out bool isArray)
    {
        parts = new List<string>();
        isArray = trimmed.StartsWith("[[", StringComparison.Ordinal);
        var start = isArray ? 2 : 1;
        var closeNeeded = isArray ? 2 : 1;
        char? quote = null;
        var esc = false;
        var endIdx = -1;

        for (var i = start; i < trimmed.Length; i++)
        {
            var c = trimmed[i];
            if (quote != null)
            {
                if (esc) { esc = false; continue; }
                if (quote == '"' && c == '\\') { esc = true; continue; }
                if (c == quote) quote = null;
                continue;
            }
            if (c is '"' or '\'') { quote = c; continue; }
            if (c == ']')
            {
                if (closeNeeded == 2)
                {
                    if (i + 1 >= trimmed.Length || trimmed[i + 1] != ']') return false;
                    endIdx = i;
                    break;
                }
                endIdx = i;
                break;
            }
        }
        if (endIdx < 0 || quote != null) return false;
        var after = trimmed[(endIdx + closeNeeded)..].Trim();
        if (after.Length > 0 && !after.StartsWith('#')) return false;

        var inner = trimmed[start..endIdx].Trim();
        if (inner.Length == 0) return false;

        var seg = new StringBuilder();
        for (var i = 0; i < inner.Length; i++)
        {
            var c = inner[i];
            if (quote != null)
            {
                if (esc) { seg.Append(c); esc = false; continue; }
                if (quote == '"' && c == '\\') { esc = true; continue; }
                if (c == quote) { quote = null; continue; }
                seg.Append(c);
                continue;
            }
            if (c is '"' or '\'') { quote = c; continue; }
            if (c == '.')
            {
                var p = seg.ToString().Trim();
                if (p.Length == 0) return false;
                parts.Add(p);
                seg.Clear();
                continue;
            }
            seg.Append(c);
        }
        var last = seg.ToString().Trim();
        if (last.Length == 0) return false;
        parts.Add(last);
        return true;
    }

    private static bool HasCodeOrBlankOrComment(string trimmed)
    {
        if (trimmed.Length == 0 || trimmed.StartsWith('#')) return true;
        char? quote = null;
        var esc = false;
        for (var i = 0; i < trimmed.Length; i++)
        {
            var c = trimmed[i];
            if (quote != null)
            {
                if (esc) { esc = false; continue; }
                if (quote == '"' && c == '\\') { esc = true; continue; }
                if (c == quote) quote = null;
                continue;
            }
            if (c is '"' or '\'') { quote = c; continue; }
            if (c == '#') return false;
            if (c == '=') return i > 0;
        }
        return false;
    }

    private static bool ScanTomlLine(string line, ref string? multiline, ref int depth)
    {
        for (var i = 0; i < line.Length; i++)
        {
            if (multiline != null)
            {
                if (multiline == "\"\"\"" && line[i] == '\\') { i++; continue; }
                if (i + 2 < line.Length && line.AsSpan(i, 3).SequenceEqual(multiline))
                {
                    multiline = null;
                    i += 2;
                }
                continue;
            }

            var c = line[i];
            if (c == '#') break;
            if (c is '"' or '\'')
            {
                if (i + 2 < line.Length && line[i + 1] == c && line[i + 2] == c)
                {
                    multiline = new string(c, 3);
                    i += 2;
                    continue;
                }
                var q = c;
                var esc = false;
                var closed = false;
                for (i++; i < line.Length; i++)
                {
                    var ch = line[i];
                    if (esc) { esc = false; continue; }
                    if (q == '"' && ch == '\\') { esc = true; continue; }
                    if (ch == q) { closed = true; break; }
                }
                if (!closed) return false;
                continue;
            }
            if (c is '[' or '{') depth++;
            else if (c is ']' or '}')
            {
                depth--;
                if (depth < 0) return false;
            }
        }
        return true;
    }

    private static bool TryExtractTomlString(string text, string key, out string value)
    {
        value = "";
        var m = Regex.Match(text, $@"(?m)^\s*{Regex.Escape(key)}\s*=\s*""((?:[^""\\]|\\.)*)""\s*(?:#.*)?$");
        if (m.Success)
        {
            value = UnescapeToml(m.Groups[1].Value);
            return true;
        }
        m = Regex.Match(text, $@"(?m)^\s*{Regex.Escape(key)}\s*=\s*'([^']*)'\s*(?:#.*)?$");
        if (!m.Success) return false;
        value = m.Groups[1].Value;
        return true;
    }

    private static bool TryExtractInlineHeaderAuth(string text, out string value)
    {
        value = "";
        var m = Regex.Match(text, @"(?m)^\s*http_headers\s*=\s*\{(?<body>[^}\r\n]*)\}\s*(?:#.*)?$");
        if (!m.Success) return false;
        var a = Regex.Match(m.Groups["body"].Value, @"(?:^|,)\s*(?:Authorization|""Authorization"")\s*=\s*""((?:[^""\\]|\\.)*)""");
        if (!a.Success) return false;
        value = UnescapeToml(a.Groups[1].Value);
        return true;
    }

    private static string UnescapeToml(string s) => s.Replace("\\\"", "\"").Replace("\\\\", "\\");

    private static void WriteAtomicText(string path, string content)
    {
        var tmp = path + ".tmp";
        // The file contains the MCP token: on Linux/macOS only the owner may read it (set before the token is written).
        if (!OperatingSystem.IsWindows())
        {
            File.WriteAllText(tmp, "");
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        File.WriteAllText(tmp, content, Utf8NoBom);
        File.Move(tmp, path, overwrite: true);
    }
}
