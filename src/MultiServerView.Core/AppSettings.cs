using System.Text.Json;

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

    public void Normalize()
    {
        SshKeepAliveSeconds = Math.Clamp(SshKeepAliveSeconds, 0, 600);
        SshConnectTimeoutSeconds = Math.Clamp(SshConnectTimeoutSeconds, 3, 120);
        if (RdpColorDepth is not (16 or 24 or 32)) RdpColorDepth = 32;
    }
}

public sealed class AppSettingsStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private AppSettings _current;

    public AppSettingsStore(string path)
    {
        _path = path;
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
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_path, JsonSerializer.Serialize(settings, Json.Options));
        }
        Apply(settings);
        return settings;
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Json.Options) ?? new AppSettings();
                s.Normalize();
                return s;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException) { /* broken file -> defaults */ }
        return new AppSettings();
    }

    /// <summary>Applies the values to the global SSH defaults.</summary>
    private static void Apply(AppSettings s)
    {
        Ssh.SshConnector.ConnectTimeout = TimeSpan.FromSeconds(s.SshConnectTimeoutSeconds);
        Ssh.SshConnector.KeepAlive = s.SshKeepAliveSeconds == 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(s.SshKeepAliveSeconds);
    }
}
