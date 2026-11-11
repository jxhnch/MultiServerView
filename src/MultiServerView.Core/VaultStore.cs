using System.Security.Cryptography;
using System.Text.Json;

namespace MultiServerView.Core;

public sealed class WrongPasswordException : Exception
{
    public WrongPasswordException() : base("Wrong password.") { }
}

public enum HostKeyTrust
{
    /// <summary>Known and identical.</summary>
    Trusted,

    /// <summary>First contact: the fingerprint was stored (trust on first use).</summary>
    NewlyTrusted,

    /// <summary>Known host but a different fingerprint - possible attack or the server was set up again.</summary>
    Mismatch
}

/// <summary>
/// Local, encrypted vault (one file). Everything except the file itself stays in memory only.
/// This file is also the blob that is synchronized.
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

    /// <summary>Raised after every change by the user (not on touch and not on sync apply).</summary>
    public event Action? Changed;

    // ------------------------------------------------------------------ Lifecycle

    public void Create(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 8)
            throw new ArgumentException("The master password must be at least 8 characters.");

        lock (_gate)
        {
            if (Exists) throw new InvalidOperationException("A vault already exists.");
            _key = VaultCrypto.Derive(password);
            _data = new VaultData();
            Save();
        }
    }

    public void Unlock(string password)
    {
        lock (_gate)
        {
            if (!Exists) throw new InvalidOperationException("No vault exists yet.");
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

    /// <summary>
    /// Changes the master password (new salt, new key). The vault must be unlocked.
    /// Throws <see cref="WrongPasswordException"/> if the old password is wrong.
    /// </summary>
    public void ChangePassword(string oldPassword, string newPassword)
    {
        if (string.IsNullOrEmpty(newPassword) || newPassword.Length < 8)
            throw new ArgumentException("The new master password must be at least 8 characters.");

        lock (_gate)
        {
            Data();
            try
            {
                VaultCrypto.Open(File.ReadAllBytes(_path), oldPassword, out _);
            }
            catch (AuthenticationTagMismatchException)
            {
                throw new WrongPasswordException();
            }
            _key = VaultCrypto.Derive(newPassword);
            Save();
        }
    }

    /// <summary>Checks the master password against the vault file. Throws <see cref="WrongPasswordException"/> if it is wrong.</summary>
    public void VerifyPassword(string password)
    {
        lock (_gate)
        {
            Data();
            try
            {
                VaultCrypto.Open(File.ReadAllBytes(_path), password, out _);
            }
            catch (AuthenticationTagMismatchException)
            {
                throw new WrongPasswordException();
            }
        }
    }

    /// <summary>How much is in the vault (for the warning before clearing).</summary>
    public VaultSummary Summary()
    {
        lock (_gate)
        {
            var d = Data();
            return new VaultSummary(d.Servers.Count, d.Keys.Count, d.Identities.Count, d.Snippets.Count, d.Forwards.Count, d.Proxmox.Count, d.KnownHosts.Count);
        }
    }

    /// <summary>
    /// Clears the vault: all hosts, keys, identities, snippets, port forwards, Proxmox connections and known host keys.
    /// The master password stays. Every removed entry gets a tombstone so that it cannot "come back to life" from another
    /// device during a sync (and is deleted there as well). Requires the master password as an additional safeguard.
    /// </summary>
    public VaultSummary Clear(string password)
    {
        VerifyPassword(password);
        VaultSummary before;
        lock (_gate)
        {
            var data = Data();
            before = Summary();
            var now = DateTimeOffset.UtcNow;
            foreach (var id in data.Servers.Select(x => x.Id)
                         .Concat(data.Keys.Select(x => x.Id)).Concat(data.Identities.Select(x => x.Id))
                         .Concat(data.Snippets.Select(x => x.Id)).Concat(data.Forwards.Select(x => x.Id))
                         .Concat(data.Proxmox.Select(x => x.Id)))
                data.Tombstones[id] = now;
            data.Servers.Clear();
            data.Keys.Clear();
            data.Identities.Clear();
            data.Snippets.Clear();
            data.Forwards.Clear();
            data.Proxmox.Clear();
            data.KnownHosts.Clear();
            Save();
        }
        Changed?.Invoke();
        return before;
    }

    /// <summary>Replaces key and content (e.g. when another device changed the master password).</summary>
    public void Rekey(VaultKey key, VaultData data)
    {
        lock (_gate)
        {
            Data();
            _key = key;
            _data = data;
            Save();
        }
    }

    /// <summary>Copies the encrypted vault file as a backup. Contains encrypted data only.</summary>
    public void ExportTo(string path)
    {
        lock (_gate)
        {
            Data();
            File.Copy(_path, path, overwrite: true);
        }
    }

    /// <summary>Writes a downloaded blob as the local vault (only if none exists yet).</summary>
    public void InstallBlob(byte[] blob)
    {
        lock (_gate)
        {
            if (Exists) throw new InvalidOperationException("A local vault already exists.");
            WriteAtomic(blob);
        }
    }

    /// <summary>
    /// Replaces the local vault with another one (a synced vault from the main device). The old file is kept as
    /// "vault.msv.bak-DATE" next to it. Afterwards the vault is locked: unlock with the master password of the new vault.
    /// Returns the path of the backup ("" if there was no vault before).
    /// </summary>
    public string ReplaceWithBlob(byte[] blob)
    {
        lock (_gate)
        {
            var backup = BackupLocal();
            _data = null;
            _key = null;
            WriteAtomic(blob);
            return backup;
        }
    }

    /// <summary>
    /// Copies the vault file to "vault.msv.bak-DATE" (the newest 10 backups are kept). Returns the path, or "" if there is no file.
    /// </summary>
    public string BackupLocal()
    {
        lock (_gate)
        {
            if (!Exists) return "";
            var backup = _path + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            for (var n = 1; File.Exists(backup); n++) backup = _path + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + n; // never overwrite an older backup
            File.Copy(_path, backup, overwrite: false);
            var dir = Path.GetDirectoryName(_path)!;
            var old = Directory.GetFiles(dir, Path.GetFileName(_path) + ".bak-*").OrderByDescending(f => f, StringComparer.Ordinal).Skip(10);
            foreach (var f in old)
            {
                try { File.Delete(f); } catch (IOException) { /* not important */ }
            }
            return backup;
        }
    }

    /// <summary>true if the blob is a vault of the same origin as the local one (same salt), i.e. it was created from it.</summary>
    public bool SameVaultAs(byte[] blob)
    {
        lock (_gate)
        {
            if (!Exists || !VaultCrypto.LooksLikeVault(blob)) return false;
            var mine = File.ReadAllBytes(_path);
            return VaultCrypto.LooksLikeVault(mine) && mine.AsSpan(8, 16).SequenceEqual(blob.AsSpan(8, 16));
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

    /// <summary>Several servers at once (import) - one save, one Changed.</summary>
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

    /// <summary>Deletes several hosts at once (one save, one sync trigger). Returns the number.</summary>
    public int DeleteMany(IEnumerable<string> ids)
    {
        var set = ids.ToHashSet();
        int removed;
        lock (_gate)
        {
            var data = Data();
            var now = DateTimeOffset.UtcNow;
            var gone = data.Servers.Where(s => set.Contains(s.Id)).Select(s => s.Id).ToList();
            removed = data.Servers.RemoveAll(s => set.Contains(s.Id));
            foreach (var id in gone) data.Tombstones[id] = now;
            foreach (var f in data.Forwards.Where(f => set.Contains(f.ServerId)).ToList())
            {
                data.Forwards.Remove(f);
                data.Tombstones[f.Id] = now;
            }
            if (removed > 0) Save();
        }
        if (removed > 0) Changed?.Invoke();
        return removed;
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

    /// <summary>Sets "last used" to now. Deliberately does not trigger a sync.</summary>
    public ServerEntry Touch(string id)
    {
        lock (_gate)
        {
            var entry = Data().Servers.FirstOrDefault(s => s.Id == id)
                        ?? throw new KeyNotFoundException("Server not found.");
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

            // Servers that used this key fall back to a password.
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

    // ------------------------------------------------------------------ Identities, snippets, port forwards

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

    // ------------------------------------------------------------------ Proxmox connections

    public IReadOnlyList<ProxmoxEntry> ListProxmox()
    {
        lock (_gate) return Data().Proxmox.ToList();
    }

    public ProxmoxEntry? FindProxmox(string id)
    {
        lock (_gate) return Data().Proxmox.FirstOrDefault(p => p.Id == id);
    }

    public ProxmoxEntry UpsertProxmox(ProxmoxEntry entry) => UpsertItem(d => d.Proxmox, entry);

    public void DeleteProxmox(string id) => DeleteItem(d => d.Proxmox, id);

    /// <summary>Applies the credentials of an identity to a copy of the server (username, password, auth, key).</summary>
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

    /// <summary>Remembers the detected operating system of a host (for the icon).</summary>
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

    /// <summary>All known host keys ("host:port" -> fingerprint), alphabetical.</summary>
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

    /// <summary>Deep copy of the current content.</summary>
    public VaultData Snapshot()
    {
        lock (_gate) return Data().Clone();
    }

    /// <summary>Current state as an encrypted blob (what goes into sync).</summary>
    public byte[] ExportBlob()
    {
        lock (_gate)
        {
            var data = Data();
            return VaultCrypto.Seal(JsonSerializer.SerializeToUtf8Bytes(data, Json.Options), _key!);
        }
    }

    /// <summary>Decrypts a synced blob with the key of this vault.</summary>
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

    /// <summary>Applies a merged state (sync). Does not raise Changed.</summary>
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

    private VaultData Data() => _data ?? throw new InvalidOperationException("The vault is locked.");

    private void Save()
    {
        if (_data == null || _key == null) throw new InvalidOperationException("The vault is locked.");
        var plain = JsonSerializer.SerializeToUtf8Bytes(_data, Json.Options);
        WriteAtomic(VaultCrypto.Seal(plain, _key));
    }

    private void WriteAtomic(byte[] blob)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Write atomically: temp file first, then replace - so nothing is lost on a crash.
        var tmp = _path + ".tmp";
        File.WriteAllBytes(tmp, blob);
        File.Move(tmp, _path, overwrite: true);
    }
}

/// <summary>Number of entries in the vault, by type.</summary>
public sealed record VaultSummary(int Servers, int Keys, int Identities, int Snippets, int Forwards, int Proxmox, int KnownHosts);
