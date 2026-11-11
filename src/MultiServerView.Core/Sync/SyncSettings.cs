using System.Text.Json;

namespace MultiServerView.Core.Sync;

/// <summary>Protects secrets on disk (in the app: Windows DPAPI, bound to the Windows user).</summary>
public interface ISecretProtector
{
    string Protect(string plain);
    string Unprotect(string protectedText);
}

/// <summary>For tests only: pretends to protect.</summary>
public sealed class PlainProtector : ISecretProtector
{
    public string Protect(string plain) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plain));
    public string Unprotect(string protectedText) => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(protectedText));
}

/// <summary>Where to sync to: a Supabase account or a folder. This is NOT the master password of the vault.</summary>
public sealed class SyncSettings
{
    public const string ModeSupabase = "supabase";
    public const string ModeFolder = "folder";
    public const string ModeGist = "gist";

    /// <summary>"supabase" (default), "folder" or "gist" (Password = GitHub token).</summary>
    public string Mode { get; set; } = ModeSupabase;

    /// <summary>Sync folder (only with Mode = folder), e.g. a folder in OneDrive.</summary>
    public string Folder { get; set; } = "";

    public string Url { get; set; } = "";
    public string AnonKey { get; set; } = "";
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
    public DateTimeOffset? LastSync { get; set; }

    /// <summary>Version of the vault at the source when this device last read or saved it (0 = unknown, e.g. settings of the old sync).</summary>
    public long LastVersion { get; set; }

    /// <summary>This device has changes that are not saved at the source yet.</summary>
    public bool Dirty { get; set; }

    public bool IsSupabase => !IsFolder && !IsGist;
    public bool IsFolder => Mode == ModeFolder;
    public bool IsGist => Mode == ModeGist;

    public bool IsConfigured =>
        IsFolder ? Folder.Length > 0
        : IsGist ? Password.Length > 0
        : Url.Length > 0 && AnonKey.Length > 0 && Email.Length > 0 && Password.Length > 0;
}

/// <summary>Stores <see cref="SyncSettings"/> locally; the cloud password is stored protected.</summary>
public sealed class SyncSettingsStore
{
    private readonly string _path;
    private readonly ISecretProtector _protector;

    public SyncSettingsStore(string path, ISecretProtector protector)
    {
        _path = path;
        _protector = protector;
    }

    private sealed class Disk
    {
        public string Mode { get; set; } = "";
        public string Folder { get; set; } = "";
        public string Url { get; set; } = "";
        public string AnonKey { get; set; } = "";
        public string Email { get; set; } = "";
        public string PasswordProtected { get; set; } = "";
        public DateTimeOffset? LastSync { get; set; }
        public long LastVersion { get; set; }
        public bool Dirty { get; set; }
    }

    public SyncSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return new SyncSettings();
            var d = JsonSerializer.Deserialize<Disk>(File.ReadAllText(_path), Json.Options) ?? new Disk();
            return new SyncSettings
            {
                Mode = d.Mode is SyncSettings.ModeFolder or SyncSettings.ModeGist ? d.Mode : SyncSettings.ModeSupabase,
                Folder = d.Folder,
                Url = d.Url,
                AnonKey = d.AnonKey,
                Email = d.Email,
                Password = d.PasswordProtected.Length > 0 ? _protector.Unprotect(d.PasswordProtected) : "",
                LastSync = d.LastSync,
                LastVersion = d.LastVersion,
                Dirty = d.Dirty
            };
        }
        catch (Exception ex) when (ex is IOException or JsonException or FormatException or System.Security.Cryptography.CryptographicException)
        {
            return new SyncSettings();
        }
    }

    public void Save(SyncSettings s)
    {
        var d = new Disk
        {
            Mode = s.Mode,
            Folder = s.Folder.Trim(),
            Url = s.Url.Trim(),
            AnonKey = s.AnonKey.Trim(),
            Email = s.Email.Trim(),
            PasswordProtected = s.Password.Length > 0 ? _protector.Protect(s.Password) : "",
            LastSync = s.LastSync,
            LastVersion = s.LastVersion,
            Dirty = s.Dirty
        };
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_path, JsonSerializer.Serialize(d, Json.Options));
    }

    public void Clear()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}
