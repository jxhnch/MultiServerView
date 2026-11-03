using System.Security.Cryptography;
using System.Text.Json;

namespace MultiServerView.Core;

public sealed class VaultData
{
    public int Version { get; set; } = 1;
    public List<ServerEntry> Servers { get; set; } = new();
}

public sealed class WrongPasswordException : Exception
{
    public WrongPasswordException() : base("Falsches Passwort.") { }
}

/// <summary>
/// Lokaler, verschlüsselter Vault (eine Datei). Alles ausser der Datei selbst bleibt nur im Speicher.
/// Diese Datei ist später auch der Blob, der in die Cloud synchronisiert wird.
/// </summary>
public sealed class VaultStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private VaultKey? _key;
    private VaultData? _data;

    public VaultStore(string path) => _path = path;

    public string FilePath => _path;
    public bool Exists => File.Exists(_path);
    public bool IsUnlocked { get { lock (_gate) return _data != null; } }

    public void Create(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 8)
            throw new ArgumentException("Das Master-Passwort muss mindestens 8 Zeichen haben.");

        lock (_gate)
        {
            if (Exists) throw new InvalidOperationException("Es existiert bereits ein Vault.");
            _key = VaultCrypto.Derive(password);
            _data = new VaultData();
            Save();
        }
    }

    public void Unlock(string password)
    {
        lock (_gate)
        {
            if (!Exists) throw new InvalidOperationException("Es existiert noch kein Vault.");
            try
            {
                var blob = File.ReadAllBytes(_path);
                var plain = VaultCrypto.Open(blob, password, out var key);
                _data = JsonSerializer.Deserialize<VaultData>(plain, Json.Options) ?? new VaultData();
                _key = key;
            }
            catch (AuthenticationTagMismatchException)
            {
                throw new WrongPasswordException();
            }
        }
    }

    public void Lock()
    {
        lock (_gate)
        {
            _data = null;
            _key = null;
        }
    }

    public IReadOnlyList<ServerEntry> List()
    {
        lock (_gate) return Data().Servers.ToList();
    }

    public ServerEntry Upsert(ServerEntry entry)
    {
        lock (_gate)
        {
            var data = Data();
            if (string.IsNullOrWhiteSpace(entry.Id)) entry.Id = Guid.NewGuid().ToString("N");
            if (entry.Port <= 0) entry.Port = ServerEntry.DefaultPort(entry.Protocol);

            var index = data.Servers.FindIndex(s => s.Id == entry.Id);
            if (index >= 0) data.Servers[index] = entry;
            else data.Servers.Add(entry);

            Save();
            return entry;
        }
    }

    public void Delete(string id)
    {
        lock (_gate)
        {
            Data().Servers.RemoveAll(s => s.Id == id);
            Save();
        }
    }

    /// <summary>Setzt "zuletzt benutzt" auf jetzt.</summary>
    public ServerEntry Touch(string id)
    {
        lock (_gate)
        {
            var entry = Data().Servers.FirstOrDefault(s => s.Id == id)
                        ?? throw new KeyNotFoundException("Server nicht gefunden.");
            entry.LastUsed = DateTimeOffset.UtcNow;
            Save();
            return entry;
        }
    }

    private VaultData Data() => _data ?? throw new InvalidOperationException("Der Vault ist gesperrt.");

    private void Save()
    {
        if (_data == null || _key == null) throw new InvalidOperationException("Der Vault ist gesperrt.");

        var plain = JsonSerializer.SerializeToUtf8Bytes(_data, Json.Options);
        var blob = VaultCrypto.Seal(plain, _key);

        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Atomar schreiben: erst temp-Datei, dann ersetzen - so geht bei einem Absturz nichts verloren.
        var tmp = _path + ".tmp";
        File.WriteAllBytes(tmp, blob);
        File.Move(tmp, _path, overwrite: true);
    }
}
