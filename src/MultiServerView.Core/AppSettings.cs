using System.Text.Json;

namespace MultiServerView.Core;

/// <summary>
/// Einstellungen, die der Host-Prozess (C#) braucht: Verbindungen und RDP.
/// Rein oberflächliche Einstellungen (Theme, Terminal-Aussehen, Auto-Sperre) liegen im UI.
/// Bleiben lokal auf diesem PC (nicht im Vault, nicht in der Cloud).
/// </summary>
public sealed class AppSettings
{
    /// <summary>Sekunden zwischen Keep-Alive-Paketen (0 = aus). Hält SSH-Sitzungen hinter Firewalls offen.</summary>
    public int SshKeepAliveSeconds { get; set; } = 30;

    /// <summary>Wie lange auf den Verbindungsaufbau gewartet wird.</summary>
    public int SshConnectTimeoutSeconds { get; set; } = 15;

    /// <summary>16, 24 oder 32 Bit.</summary>
    public int RdpColorDepth { get; set; } = 32;

    public bool RdpClipboard { get; set; } = true;

    /// <summary>Lokale Laufwerke im Remote-PC einbinden (Dateien per Explorer kopieren).</summary>
    public bool RdpDrives { get; set; }

    /// <summary>Auflösung beim Ändern der Fenstergrösse anpassen (statt nur zu skalieren).</summary>
    public bool RdpDynamicResize { get; set; } = true;

    /// <summary>Bei Zertifikatsproblemen des Servers warnen (sicherer, aber bei selbstsignierten Zertifikaten lästig).</summary>
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
        catch (Exception ex) when (ex is IOException or JsonException) { /* kaputte Datei -> Standardwerte */ }
        return new AppSettings();
    }

    /// <summary>Überträgt die Werte auf die globalen SSH-Voreinstellungen.</summary>
    private static void Apply(AppSettings s)
    {
        Ssh.SshConnector.ConnectTimeout = TimeSpan.FromSeconds(s.SshConnectTimeoutSeconds);
        Ssh.SshConnector.KeepAlive = s.SshKeepAliveSeconds == 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(s.SshKeepAliveSeconds);
    }
}
