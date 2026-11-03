using System.Text.Json;

namespace MultiServerView.Core.Sync;

/// <summary>Schützt Geheimnisse auf der Platte (in der App: Windows DPAPI, gebunden an den Windows-Benutzer).</summary>
public interface ISecretProtector
{
    string Protect(string plain);
    string Unprotect(string protectedText);
}

/// <summary>Nur für Tests: tut so, als würde es schützen.</summary>
public sealed class PlainProtector : ISecretProtector
{
    public string Protect(string plain) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plain));
    public string Unprotect(string protectedText) => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(protectedText));
}

/// <summary>Zugangsdaten zur Cloud (Supabase). Das ist NICHT das Master-Passwort des Vaults.</summary>
public sealed class SyncSettings
{
    public string Url { get; set; } = "";
    public string AnonKey { get; set; } = "";
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
    public DateTimeOffset? LastSync { get; set; }

    public bool IsConfigured =>
        Url.Length > 0 && AnonKey.Length > 0 && Email.Length > 0 && Password.Length > 0;
}

/// <summary>Speichert <see cref="SyncSettings"/> lokal; das Cloud-Passwort wird geschützt abgelegt.</summary>
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
        public string Url { get; set; } = "";
        public string AnonKey { get; set; } = "";
        public string Email { get; set; } = "";
        public string PasswordProtected { get; set; } = "";
        public DateTimeOffset? LastSync { get; set; }
    }

    public SyncSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return new SyncSettings();
            var d = JsonSerializer.Deserialize<Disk>(File.ReadAllText(_path), Json.Options) ?? new Disk();
            return new SyncSettings
            {
                Url = d.Url,
                AnonKey = d.AnonKey,
                Email = d.Email,
                Password = d.PasswordProtected.Length > 0 ? _protector.Unprotect(d.PasswordProtected) : "",
                LastSync = d.LastSync
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
            Url = s.Url.Trim(),
            AnonKey = s.AnonKey.Trim(),
            Email = s.Email.Trim(),
            PasswordProtected = s.Password.Length > 0 ? _protector.Protect(s.Password) : "",
            LastSync = s.LastSync
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
