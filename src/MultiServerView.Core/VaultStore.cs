using System.Security.Cryptography;
using System.Text.Json;

namespace MultiServerView.Core;

public sealed class WrongPasswordException : Exception
{
    public WrongPasswordException() : base("Falsches Passwort.") { }
}

public enum HostKeyTrust
{
    /// <summary>Bekannt und identisch.</summary>
    Trusted,

    /// <summary>Erster Kontakt: Fingerprint wurde gespeichert (Trust on first use).</summary>
    NewlyTrusted,

    /// <summary>Bekannter Host, aber anderer Fingerprint - möglicher Angriff oder Server neu aufgesetzt.</summary>
    Mismatch
}

/// <summary>
/// Lokaler, verschlüsselter Vault (eine Datei). Alles ausser der Datei selbst bleibt nur im Speicher.
/// Diese Datei ist auch der Blob, der in die Cloud synchronisiert wird.
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

    /// <summary>Wird nach jeder Änderung durch den Benutzer ausgelöst (nicht bei Touch und nicht beim Sync-Apply).</summary>
    public event Action? Changed;

    // ------------------------------------------------------------------ Lebenszyklus

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

    /// <summary>Schreibt einen aus der Cloud geladenen Blob als lokalen Vault (nur wenn noch keiner existiert).</summary>
    public void InstallBlob(byte[] blob)
    {
        lock (_gate)
        {
            if (Exists) throw new InvalidOperationException("Es existiert bereits ein lokaler Vault.");
            WriteAtomic(blob);
        }
    }

    // ------------------------------------------------------------------ Server

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
            entry.UpdatedAt = DateTimeOffset.UtcNow;
            data.Tombstones.Remove(entry.Id);

            var index = data.Servers.FindIndex(s => s.Id == entry.Id);
            if (index >= 0) data.Servers[index] = entry;
            else data.Servers.Add(entry);

            Save();
        }
        Changed?.Invoke();
        return entry;
    }

    /// <summary>Mehrere Server auf einmal (Import) - ein Speichern, ein Changed.</summary>
    public int UpsertMany(IEnumerable<ServerEntry> entries)
    {
        var count = 0;
        lock (_gate)
        {
            var data = Data();
            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Id)) entry.Id = Guid.NewGuid().ToString("N");
                if (entry.Port <= 0) entry.Port = ServerEntry.DefaultPort(entry.Protocol);
                entry.UpdatedAt = DateTimeOffset.UtcNow;
                data.Servers.Add(entry);
                count++;
            }
            Save();
        }
        if (count > 0) Changed?.Invoke();
        return count;
    }

    public void Delete(string id)
    {
        lock (_gate)
        {
            var data = Data();
            if (data.Servers.RemoveAll(s => s.Id == id) > 0) data.Tombstones[id] = DateTimeOffset.UtcNow;
            foreach (var f in data.Forwards.Where(f => f.ServerId == id).ToList())
            {
                data.Forwards.Remove(f);
                data.Tombstones[f.Id] = DateTimeOffset.UtcNow;
            }
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>Setzt "zuletzt benutzt" auf jetzt. Löst bewusst keinen Sync aus.</summary>
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

    public ServerEntry? Find(string id)
    {
        lock (_gate) return Data().Servers.FirstOrDefault(s => s.Id == id);
    }

    // ------------------------------------------------------------------ Keys

    public IReadOnlyList<KeyEntry> ListKeys()
    {
        lock (_gate) return Data().Keys.ToList();
    }

    public KeyEntry? FindKey(string id)
    {
        lock (_gate) return Data().Keys.FirstOrDefault(k => k.Id == id);
    }

    public KeyEntry UpsertKey(KeyEntry key)
    {
        lock (_gate)
        {
            var data = Data();
            if (string.IsNullOrWhiteSpace(key.Id)) key.Id = Guid.NewGuid().ToString("N");
            key.UpdatedAt = DateTimeOffset.UtcNow;
            data.Tombstones.Remove(key.Id);

            var index = data.Keys.FindIndex(k => k.Id == key.Id);
            if (index >= 0) data.Keys[index] = key;
            else data.Keys.Add(key);

            Save();
        }
        Changed?.Invoke();
        return key;
    }

    public void DeleteKey(string id)
    {
        lock (_gate)
        {
            var data = Data();
            if (data.Keys.RemoveAll(k => k.Id == id) > 0) data.Tombstones[id] = DateTimeOffset.UtcNow;

            // Server, die diesen Key benutzt haben, fallen auf Passwort zurück.
            foreach (var s in data.Servers.Where(s => s.KeyId == id))
            {
                s.KeyId = "";
                s.Auth = AuthMethod.Password;
                s.UpdatedAt = DateTimeOffset.UtcNow;
            }
            Save();
        }
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ Identities, Snippets, Port-Weiterleitungen

    public IReadOnlyList<IdentityEntry> ListIdentities()
    {
        lock (_gate) return Data().Identities.ToList();
    }

    public IdentityEntry? FindIdentity(string id)
    {
        lock (_gate) return Data().Identities.FirstOrDefault(i => i.Id == id);
    }

    public IdentityEntry UpsertIdentity(IdentityEntry identity) => UpsertItem(d => d.Identities, identity);

    public void DeleteIdentity(string id)
    {
        lock (_gate)
        {
            var data = Data();
            if (data.Identities.RemoveAll(i => i.Id == id) > 0) data.Tombstones[id] = DateTimeOffset.UtcNow;
            foreach (var s in data.Servers.Where(s => s.IdentityId == id))
            {
                s.IdentityId = "";
                s.UpdatedAt = DateTimeOffset.UtcNow;
            }
            Save();
        }
        Changed?.Invoke();
    }

    public IReadOnlyList<SnippetEntry> ListSnippets()
    {
        lock (_gate) return Data().Snippets.ToList();
    }

    public SnippetEntry UpsertSnippet(SnippetEntry snippet) => UpsertItem(d => d.Snippets, snippet);

    public void DeleteSnippet(string id) => DeleteItem(d => d.Snippets, id);

    public IReadOnlyList<PortForwardEntry> ListForwards()
    {
        lock (_gate) return Data().Forwards.ToList();
    }

    public PortForwardEntry? FindForward(string id)
    {
        lock (_gate) return Data().Forwards.FirstOrDefault(f => f.Id == id);
    }

    public PortForwardEntry UpsertForward(PortForwardEntry forward) => UpsertItem(d => d.Forwards, forward);

    public void DeleteForward(string id) => DeleteItem(d => d.Forwards, id);

    /// <summary>Wendet die Zugangsdaten einer Identity auf eine Kopie des Servers an (Benutzer, Passwort, Auth, Key).</summary>
    public ServerEntry Resolve(ServerEntry server)
    {
        lock (_gate)
        {
            if (string.IsNullOrEmpty(server.IdentityId)) return server;
            var identity = Data().Identities.FirstOrDefault(i => i.Id == server.IdentityId);
            if (identity == null) return server;

            var copy = server.Clone();
            copy.Username = identity.Username;
            copy.Password = identity.Password;
            copy.Auth = identity.Auth;
            copy.KeyId = identity.KeyId;
            return copy;
        }
    }

    public ServerEntry? FindResolved(string id)
    {
        lock (_gate) return Find(id) is { } s ? Resolve(s) : null;
    }

    /// <summary>Merkt sich das erkannte Betriebssystem eines Hosts (für das Icon).</summary>
    public void SetOs(string serverId, string os)
    {
        lock (_gate)
        {
            var s = Data().Servers.FirstOrDefault(x => x.Id == serverId);
            if (s == null || s.Os == os) return;
            s.Os = os;
            s.UpdatedAt = DateTimeOffset.UtcNow;
            Save();
        }
        Changed?.Invoke();
    }

    private T UpsertItem<T>(Func<VaultData, List<T>> select, T item) where T : IVaultItem
    {
        lock (_gate)
        {
            var data = Data();
            if (string.IsNullOrWhiteSpace(item.Id)) item.Id = Guid.NewGuid().ToString("N");
            item.UpdatedAt = DateTimeOffset.UtcNow;
            data.Tombstones.Remove(item.Id);

            var list = select(data);
            var index = list.FindIndex(x => x.Id == item.Id);
            if (index >= 0) list[index] = item;
            else list.Add(item);
            Save();
        }
        Changed?.Invoke();
        return item;
    }

    private void DeleteItem<T>(Func<VaultData, List<T>> select, string id) where T : IVaultItem
    {
        lock (_gate)
        {
            var data = Data();
            if (select(data).RemoveAll(x => x.Id == id) > 0) data.Tombstones[id] = DateTimeOffset.UtcNow;
            Save();
        }
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ Known Hosts (Trust on first use)

    public static string HostKeyId(string host, int port) => $"{host.Trim().ToLowerInvariant()}:{port}";

    public HostKeyTrust CheckHostKey(string host, int port, string fingerprint)
    {
        var changed = false;
        HostKeyTrust result;
        lock (_gate)
        {
            var data = Data();
            var id = HostKeyId(host, port);
            if (!data.KnownHosts.TryGetValue(id, out var known))
            {
                data.KnownHosts[id] = fingerprint;
                Save();
                changed = true;
                result = HostKeyTrust.NewlyTrusted;
            }
            else result = known == fingerprint ? HostKeyTrust.Trusted : HostKeyTrust.Mismatch;
        }
        if (changed) Changed?.Invoke();
        return result;
    }

    /// <summary>Alle bekannten Host-Keys ("host:port" -> Fingerprint), alphabetisch.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> ListKnownHosts()
    {
        lock (_gate) return Data().KnownHosts.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
    }

    public void RemoveKnownHost(string id)
    {
        lock (_gate)
        {
            if (!Data().KnownHosts.Remove(id)) return;
            Save();
        }
        Changed?.Invoke();
    }

    public void ForgetHostKey(string host, int port)
    {
        lock (_gate)
        {
            Data().KnownHosts.Remove(HostKeyId(host, port));
            Save();
        }
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ Sync-Hooks

    /// <summary>Tiefe Kopie des aktuellen Inhalts.</summary>
    public VaultData Snapshot()
    {
        lock (_gate) return Data().Clone();
    }

    /// <summary>Aktueller Stand als verschlüsselter Blob (das, was in die Cloud geht).</summary>
    public byte[] ExportBlob()
    {
        lock (_gate)
        {
            var data = Data();
            return VaultCrypto.Seal(JsonSerializer.SerializeToUtf8Bytes(data, Json.Options), _key!);
        }
    }

    /// <summary>Entschlüsselt einen Cloud-Blob mit dem Schlüssel dieses Vaults.</summary>
    public VaultData OpenRemote(byte[] blob)
    {
        VaultKey key;
        lock (_gate)
        {
            Data();
            key = _key!;
        }
        try
        {
            var plain = VaultCrypto.OpenWithKey(blob, key);
            return JsonSerializer.Deserialize<VaultData>(plain, Json.Options) ?? new VaultData();
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new VaultMismatchException();
        }
    }

    /// <summary>Übernimmt einen zusammengeführten Stand (Sync). Löst kein Changed aus.</summary>
    public void Apply(VaultData merged)
    {
        lock (_gate)
        {
            Data();
            _data = merged;
            Save();
        }
    }

    // ------------------------------------------------------------------ intern

    private VaultData Data() => _data ?? throw new InvalidOperationException("Der Vault ist gesperrt.");

    private void Save()
    {
        if (_data == null || _key == null) throw new InvalidOperationException("Der Vault ist gesperrt.");
        var plain = JsonSerializer.SerializeToUtf8Bytes(_data, Json.Options);
        WriteAtomic(VaultCrypto.Seal(plain, _key));
    }

    private void WriteAtomic(byte[] blob)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Atomar schreiben: erst temp-Datei, dann ersetzen - so geht bei einem Absturz nichts verloren.
        var tmp = _path + ".tmp";
        File.WriteAllBytes(tmp, blob);
        File.Move(tmp, _path, overwrite: true);
    }
}
