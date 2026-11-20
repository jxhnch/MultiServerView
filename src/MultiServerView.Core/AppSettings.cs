using System.Text.Json;
using MultiServerView.Core.Sync;

namespace MultiServerView.Core;

/// <summary>
/// Settings the host process (C#) needs: connections and RDP.
/// Purely cosmetic settings (theme, terminal appearance, auto-lock) live in the UI.
/// Stay local on this PC (not in the vault, not synced).
/// </summary>
public sealed class AppSettings
{
    /// <summary>Seconds between keep-alive packets (0 = off). Keeps SSH sessions open behind firewalls.</summary>
    public int SshKeepAliveSeconds { get; set; } = 30;

    /// <summary>How long to wait for the connection to be established.</summary>
    public int SshConnectTimeoutSeconds { get; set; } = 15;

    /// <summary>16, 24 or 32 bit.</summary>
    public int RdpColorDepth { get; set; } = 32;

    public bool RdpClipboard { get; set; } = true;

    /// <summary>Redirect local drives into the remote PC (copy files with Explorer).</summary>
    public bool RdpDrives { get; set; }

    /// <summary>Adjust the resolution when the window size changes (instead of just scaling).</summary>
    public bool RdpDynamicResize { get; set; } = true;

    /// <summary>Warn on certificate problems of the server (safer, but annoying with self-signed certificates).</summary>
    public bool RdpWarnOnCertificate { get; set; }

    /// <summary>Local MCP server for AI tools (off by default). Listens on 127.0.0.1 only and needs <see cref="McpToken"/>.</summary>
    public bool McpEnabled { get; set; }

    public int McpPort { get; set; } = 7676;

    public string McpToken { get; set; } = "";

    public const string McpNoWorkspace = "(none)";

    /// <summary>When true, MCP only allows read-only tools (list_hosts, read_file, list_directory).</summary>
    public bool McpReadOnly { get; set; }

    /// <summary>Optional workspace allow-list for MCP (empty = all workspaces allowed, "(none)" = hosts without a workspace).</summary>
    public List<string> McpAllowedWorkspaces { get; set; } = new();

    public static bool IsWorkspaceAllowed(string workspace, IReadOnlySet<string>? allowed) =>
        allowed == null || allowed.Count == 0 || allowed.Contains(string.IsNullOrEmpty(workspace) ? McpNoWorkspace : workspace);

    public void Normalize()
    {
        McpPort = Math.Clamp(McpPort, 1024, 65535);
        if (!McpEnabled) McpToken = "";
        else if (McpToken.Length < 16) McpToken = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        McpAllowedWorkspaces = (McpAllowedWorkspaces ?? new List<string>())
            .Select(w => w.Trim())
            .Where(w => w.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        SshKeepAliveSeconds = Math.Clamp(SshKeepAliveSeconds, 0, 600);
        SshConnectTimeoutSeconds = Math.Clamp(SshConnectTimeoutSeconds, 3, 120);
        if (RdpColorDepth is not (16 or 24 or 32)) RdpColorDepth = 32;
    }
}

public sealed class AppSettingsStore
{
    private readonly string _path;
    private readonly ISecretProtector? _protector;
    private readonly object _gate = new();
    private AppSettings _current;

    public AppSettingsStore(string path, ISecretProtector? protector = null)
    {
        _path = path;
        _protector = protector;
        _current = Load();
        Apply(_current);
    }

    public AppSettings Current
    {
        get { lock (_gate) return _current; }
    }

    public AppSettings Save(AppSettings settings)
    {
        settings.Normalize();
        lock (_gate)
        {
            _current = settings;
            WriteFile(settings);
        }
        Apply(settings);
        return settings;
    }

    private sealed class Disk
    {
        public int SshKeepAliveSeconds { get; set; } = 30;
        public int SshConnectTimeoutSeconds { get; set; } = 15;
        public int RdpColorDepth { get; set; } = 32;
        public bool RdpClipboard { get; set; } = true;
        public bool RdpDrives { get; set; }
        public bool RdpDynamicResize { get; set; } = true;
        public bool RdpWarnOnCertificate { get; set; }
        public bool McpEnabled { get; set; }
        public int McpPort { get; set; } = 7676;
        public string McpToken { get; set; } = "";
        public string McpTokenProtected { get; set; } = "";
        public bool McpReadOnly { get; set; }
        public List<string> McpAllowedWorkspaces { get; set; } = new();
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var d = JsonSerializer.Deserialize<Disk>(File.ReadAllText(_path), Json.Options) ?? new Disk();
                var token = d.McpToken;
                var migrated = false;
                if (_protector != null)
                {
                    if (d.McpTokenProtected.Length > 0)
                    {
                        try { token = _protector.Unprotect(d.McpTokenProtected); }
                        catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException) { token = ""; }
                    }
                    else if (d.McpToken.Length > 0)
                    {
                        migrated = true;
                    }
                }
                var s = new AppSettings
                {
                    SshKeepAliveSeconds = d.SshKeepAliveSeconds,
                    SshConnectTimeoutSeconds = d.SshConnectTimeoutSeconds,
                    RdpColorDepth = d.RdpColorDepth,
                    RdpClipboard = d.RdpClipboard,
                    RdpDrives = d.RdpDrives,
                    RdpDynamicResize = d.RdpDynamicResize,
                    RdpWarnOnCertificate = d.RdpWarnOnCertificate,
                    McpEnabled = d.McpEnabled,
                    McpPort = d.McpPort,
                    McpToken = token,
                    McpReadOnly = d.McpReadOnly,
                    McpAllowedWorkspaces = d.McpAllowedWorkspaces ?? new List<string>()
                };
                s.Normalize();
                if (migrated) WriteFile(s);
                return s;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException) { /* broken file -> defaults */ }
        return new AppSettings();
    }

    private void WriteFile(AppSettings settings)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var disk = new Disk
        {
            SshKeepAliveSeconds = settings.SshKeepAliveSeconds,
            SshConnectTimeoutSeconds = settings.SshConnectTimeoutSeconds,
            RdpColorDepth = settings.RdpColorDepth,
            RdpClipboard = settings.RdpClipboard,
            RdpDrives = settings.RdpDrives,
            RdpDynamicResize = settings.RdpDynamicResize,
            RdpWarnOnCertificate = settings.RdpWarnOnCertificate,
            McpEnabled = settings.McpEnabled,
            McpPort = settings.McpPort,
            McpToken = _protector == null ? settings.McpToken : "",
            McpTokenProtected = _protector != null && settings.McpToken.Length > 0 ? _protector.Protect(settings.McpToken) : "",
            McpReadOnly = settings.McpReadOnly,
            McpAllowedWorkspaces = settings.McpAllowedWorkspaces
        };

        var tmp = _path + ".tmp";
        if (!OperatingSystem.IsWindows())
        {
            File.WriteAllText(tmp, "");
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        File.WriteAllText(tmp, JsonSerializer.Serialize(disk, Json.Options));
        File.Move(tmp, _path, overwrite: true);
    }

    /// <summary>Applies the values to the global SSH defaults.</summary>
    private static void Apply(AppSettings s)
    {
        Ssh.SshConnector.ConnectTimeout = TimeSpan.FromSeconds(s.SshConnectTimeoutSeconds);
        Ssh.SshConnector.KeepAlive = s.SshKeepAliveSeconds == 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(s.SshKeepAliveSeconds);
    }
}
