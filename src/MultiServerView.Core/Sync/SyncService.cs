using System.Text.Json;

namespace MultiServerView.Core.Sync;

public enum SyncOutcome
{
    /// <summary>The remote was empty, the local state was uploaded.</summary>
    Created,

    /// <summary>Local and remote were already identical.</summary>
    UpToDate,

    /// <summary>Only changes from the remote were taken over.</summary>
    Pulled,

    /// <summary>Only local changes were uploaded.</summary>
    Pushed,

    /// <summary>Both sides had changes: merged and uploaded.</summary>
    Merged
}

public sealed record SyncResult(SyncOutcome Outcome, long Version, DateTimeOffset At);

/// <summary>
/// Reconciles the local vault with the remote: load -> merge -> upload.
/// The server only sees the encrypted blob; merging happens locally.
/// </summary>
public sealed class SyncService
{
    private readonly VaultStore _store;
    private readonly SyncSettingsStore _settings;
    private readonly Func<SyncSettings, SupabaseClient> _clientFactory;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public SyncService(VaultStore store, SyncSettingsStore settings, Func<SyncSettings, SupabaseClient> clientFactory)
    {
        _store = store;
        _settings = settings;
        _clientFactory = clientFactory;
    }

    public SyncSettings Settings => _settings.Load();

    public async Task<SyncResult> SyncAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var cfg = _settings.Load();
            if (!cfg.IsConfigured) throw new SyncException("Sync is not set up yet.");

            var remote = CreateRemote(cfg);

            for (var attempt = 0; attempt < 4; attempt++)
            {
                var result = await TryOnceAsync(remote, ct);
                if (result != null)
                {
                    cfg.LastSync = result.At;
                    _settings.Save(cfg);
                    return result;
                }
                // Conflict: someone was faster -> start over (reload, merge).
            }
            throw new SyncException("The vault is being changed by another device right now. Please try again in a moment.");
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Where the vault lives: in the sync folder or at Supabase.</summary>
    private ISyncRemote CreateRemote(SyncSettings cfg) =>
        cfg.IsFolder ? new FolderRemote(cfg.Folder) : new SupabaseRemote(_clientFactory(cfg), cfg.Email, cfg.Password);

    private async Task<SyncResult?> TryOnceAsync(ISyncRemote store, CancellationToken ct)
    {
        var remote = await store.GetAsync(ct);
        var now = DateTimeOffset.UtcNow;

        if (remote == null)
        {
            var v = await store.InsertAsync(_store.ExportBlob(), ct);
            return new SyncResult(SyncOutcome.Created, v, now);
        }

        VaultData remoteData;
        try
        {
            remoteData = _store.OpenRemote(remote.Blob);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            throw new SyncException(
                "The synced vault does not match this device (different master password or created separately). " +
                "On a new device: use \"Load vault\" instead of creating a new vault. " +
                "If you changed the master password on another device: use \"Adopt new master password\" (Sync page).");
        }

        var local = _store.Snapshot();
        var merged = VaultMerge.Merge(local, remoteData);

        // Also read conflict copies of other devices (folder sync only) so their changes are not lost.
        var usedCopies = new List<ConflictCopy>();
        if (store is IConflictAware conflicts)
        {
            foreach (var copy in await conflicts.GetConflictCopiesAsync(ct))
            {
                try
                {
                    merged = VaultMerge.Merge(merged, _store.OpenRemote(copy.Blob));
                    usedCopies.Add(copy);
                }
                catch (Exception)
                {
                    // Does not belong to this vault (different master password) or is unreadable: leave it untouched.
                }
            }
        }
        void ArchiveCopies()
        {
            if (store is IConflictAware aware) foreach (var copy in usedCopies) aware.Archive(copy);
        }
        var mergedFp = VaultMerge.Fingerprint(merged);
        var localChanged = VaultMerge.Fingerprint(local) != mergedFp;   // the remote brought something new
        var remoteChanged = VaultMerge.Fingerprint(remoteData) != mergedFp; // local has something new

        if (localChanged) _store.Apply(merged);

        if (!remoteChanged)
        {
            ArchiveCopies();
            return new SyncResult(localChanged ? SyncOutcome.Pulled : SyncOutcome.UpToDate, remote.Version, now);
        }

        var newVersion = await store.UpdateAsync(remote.Version, _store.ExportBlob(), ct);
        if (newVersion == null) return null; // conflict

        ArchiveCopies();
        return new SyncResult(localChanged ? SyncOutcome.Merged : SyncOutcome.Pushed, newVersion.Value, now);
    }

    /// <summary>New device: install the synced blob as the local vault (then unlock with the master password).</summary>
    public async Task DownloadInitialAsync(SyncSettings cfg, CancellationToken ct = default)
    {
        if (_store.Exists) throw new SyncException("There is already a vault on this device.");
        if (!cfg.IsConfigured) throw new SyncException(cfg.IsFolder ? "Please choose a sync folder." : "Please fill in all cloud fields.");

        var remote = await CreateRemote(cfg).GetAsync(ct)
                     ?? throw new SyncException(cfg.IsFolder
                         ? $"There is no vault in this folder yet ({FolderRemote.FileName}). Wait until the folder service has downloaded the file from the other device."
                         : "There is no vault in the cloud for this account yet.");

        _store.InstallBlob(remote.Blob);
        cfg.LastSync = DateTimeOffset.UtcNow;
        _settings.Save(cfg);
    }

    /// <summary>
    /// Overwrites the synced vault with the local state (needed after a change of the master password because the
    /// synced blob is then encrypted with the old key). Other devices have to adopt the new password.
    /// </summary>
    public async Task ForceUploadAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var cfg = _settings.Load();
            if (!cfg.IsConfigured) throw new SyncException("Sync is not set up yet.");
            var store = CreateRemote(cfg);
            var remote = await store.GetAsync(ct);
            if (remote == null) await store.InsertAsync(_store.ExportBlob(), ct);
            else if (await store.UpdateAsync(remote.Version, _store.ExportBlob(), ct) == null)
                throw new SyncException("The vault was just changed by another device. Please try again.");
            cfg.LastSync = DateTimeOffset.UtcNow;
            _settings.Save(cfg);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// For devices that still have the old master password: loads the synced vault, opens it with the NEW password,
    /// merges it with the local state and takes over the new key.
    /// </summary>
    public async Task AdoptCloudPasswordAsync(string newPassword, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var cfg = _settings.Load();
            if (!cfg.IsConfigured) throw new SyncException("Sync is not set up yet.");
            var remote = await CreateRemote(cfg).GetAsync(ct) ?? throw new SyncException("There is no vault there yet.");

            VaultData remoteData;
            VaultKey key;
            try
            {
                remoteData = JsonSerializer.Deserialize<VaultData>(VaultCrypto.Open(remote.Blob, newPassword, out key), Json.Options) ?? new VaultData();
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                throw new WrongPasswordException();
            }
            _store.Rekey(key, VaultMerge.Merge(_store.Snapshot(), remoteData));
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Only checks whether the credentials are correct and the table exists (folder: whether it is writable).</summary>
    public async Task TestConnectionAsync(SyncSettings cfg, CancellationToken ct = default)
    {
        if (cfg.IsFolder) new FolderRemote(cfg.Folder).CheckWritable();
        else await CreateRemote(cfg).GetAsync(ct);
    }

    public async Task<bool> SignUpAsync(SyncSettings cfg, CancellationToken ct = default)
    {
        if (cfg.IsFolder) throw new SyncException("A sync folder needs no account.");
        return await _clientFactory(cfg).SignUpAsync(cfg.Email, cfg.Password, ct);
    }

    public void Save(SyncSettings cfg) => _settings.Save(cfg);

    public void Disconnect() => _settings.Clear();
}
