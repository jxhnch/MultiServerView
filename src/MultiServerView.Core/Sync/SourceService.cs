using System.Security.Cryptography;

namespace MultiServerView.Core.Sync;

public enum SourceState
{
    /// <summary>The vault only lives on this PC.</summary>
    Local,

    /// <summary>Everything is saved at the source.</summary>
    Ready,

    /// <summary>Changes are waiting to be saved at the source (no connection, or just a moment ago).</summary>
    Pending,

    /// <summary>The source was changed somewhere else and this device has changes of its own: the user has to choose.</summary>
    Conflict,

    /// <summary>Something is wrong (e.g. the vault at the source is gone).</summary>
    Error
}

public sealed record SourceStatus(string Kind, SourceState State, string Message, DateTimeOffset? LastSaved);

public enum ProbeKind
{
    /// <summary>Nothing stored there yet.</summary>
    Empty,

    /// <summary>A MultiServerView vault is there.</summary>
    Vault,

    /// <summary>Something else is there (nothing is touched).</summary>
    NotAVault
}

/// <param name="SameVault">The vault there is the one of this device (same origin), e.g. after a sync with the old version.</param>
public sealed record ProbeResult(ProbeKind Kind, bool SameVault);

public enum RefreshOutcome
{
    NoChange,

    /// <summary>The source had a newer state and it was taken over.</summary>
    Pulled,

    /// <summary>The source uses a different master password now (changed on another device): the local vault was replaced and locked.</summary>
    KeyChanged,

    Conflict,
    Offline,
    Empty
}

/// <summary>
/// The vault lives at ONE place (this PC, a folder, a GitHub gist or Supabase). There is no merging: this device either
/// has the same state as the source or it is about to save its changes there. A save only goes through if the source
/// has not been changed by someone else since it was last read - otherwise the user is asked what to keep.
/// </summary>
public sealed class SourceService
{
    private readonly VaultStore _store;
    private readonly SyncSettingsStore _settings;
    private readonly Func<SyncSettings, SupabaseClient> _clientFactory;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private SourceStatus _status;

    private static readonly HttpClient GistHttp = new() { Timeout = TimeSpan.FromSeconds(30) };

    public SourceService(VaultStore store, SyncSettingsStore settings, Func<SyncSettings, SupabaseClient> clientFactory)
    {
        _store = store;
        _settings = settings;
        _clientFactory = clientFactory;
        _status = Describe(SourceState.Local, "");
    }

    public event Action<SourceStatus>? StatusChanged;

    public SyncSettings Settings => _settings.Load();

    public SourceStatus Status => _status;

    public static string Label(string mode) => mode switch
    {
        SyncSettings.ModeFolder => "your folder",
        SyncSettings.ModeGist => "GitHub",
        SyncSettings.ModeSupabase => "Supabase",
        _ => "this PC"
    };

    private SourceStatus Describe(SourceState state, string message)
    {
        var cfg = _settings.Load();
        var kind = cfg.IsConfigured ? cfg.Mode : "local";
        if (message.Length == 0)
            message = state switch
            {
                SourceState.Local => "Saved on this PC",
                SourceState.Ready => "Saved to " + Label(kind),
                SourceState.Pending => "Waiting to save to " + Label(kind),
                _ => ""
            };
        return new SourceStatus(kind, state, message, cfg.LastSync);
    }

    private SourceStatus SetStatus(SourceState state, string message = "")
    {
        _status = Describe(state, message);
        StatusChanged?.Invoke(_status);
        return _status;
    }

    private ISyncRemote CreateRemote(SyncSettings cfg) =>
        cfg.IsFolder ? new FolderRemote(cfg.Folder)
        : cfg.IsGist ? new GistRemote(GistHttp, cfg.Password)
        : new SupabaseRemote(_clientFactory(cfg), cfg.Email, cfg.Password);

    // ------------------------------------------------------------------------------------------------ Choosing a source

    /// <summary>Looks at the target without changing anything.</summary>
    public async Task<ProbeResult> ProbeAsync(SyncSettings cfg, CancellationToken ct = default)
    {
        if (!cfg.IsConfigured) throw new SyncException(Missing(cfg));
        if (cfg.IsFolder) new FolderRemote(cfg.Folder).CheckWritable();

        var remote = await CreateRemote(cfg).GetAsync(ct);
        if (remote == null) return new ProbeResult(ProbeKind.Empty, false);
        if (!VaultCrypto.LooksLikeVault(remote.Blob)) return new ProbeResult(ProbeKind.NotAVault, false);
        return new ProbeResult(ProbeKind.Vault, _store.SameVaultAs(remote.Blob));
    }

    /// <summary>
    /// "Move my vault here": saves the vault of this device (must be unlocked) at the new source. If a vault is already
    /// there it is only replaced with <paramref name="overwrite"/>.
    /// </summary>
    public async Task MoveHereAsync(SyncSettings cfg, bool overwrite, CancellationToken ct = default)
    {
        if (!cfg.IsConfigured) throw new SyncException(Missing(cfg));
        if (!_store.IsUnlocked) throw new SyncException("The vault is locked.");

        await _lock.WaitAsync(ct);
        try
        {
            var remote = CreateRemote(cfg);
            var current = await remote.GetAsync(ct);
            var blob = _store.ExportBlob();
            long version;
            if (current == null) version = await remote.InsertAsync(blob, ct);
            else
            {
                if (!overwrite) throw new SyncException("There is already a vault at this source. Choose what to do with it first.");
                version = await remote.UpdateAsync(current.Version, blob, ct) ?? throw new SyncException("The source was just changed by someone else. Please try again.");
            }
            Remember(cfg, version, dirty: false);
            SetStatus(SourceState.Ready);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// "Use the vault that is already there": the vault at the source becomes the vault of this device. An existing local
    /// vault is kept as a backup file. Afterwards the vault is locked (unlock with the master password of that vault).
    /// Returns the backup path ("" if there was no local vault).
    /// </summary>
    public async Task<string> UseExistingAsync(SyncSettings cfg, CancellationToken ct = default)
    {
        if (!cfg.IsConfigured) throw new SyncException(Missing(cfg));

        await _lock.WaitAsync(ct);
        try
        {
            var remote = await CreateRemote(cfg).GetAsync(ct) ?? throw new SyncException("There is no vault at this source yet.");
            if (!VaultCrypto.LooksLikeVault(remote.Blob))
                throw new SyncException("What is stored there is not a MultiServerView vault. Nothing was changed on this device.");

            var backup = _store.ReplaceWithBlob(remote.Blob);
            Remember(cfg, remote.Version, dirty: false);
            SetStatus(SourceState.Ready);
            return backup;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Back to "only on this PC". The local copy stays as it is, the data at the old source is not touched.</summary>
    public void Disconnect()
    {
        _settings.Clear();
        SetStatus(SourceState.Local);
    }

    private void Remember(SyncSettings cfg, long version, bool dirty)
    {
        cfg.LastVersion = version;
        cfg.Dirty = dirty;
        cfg.LastSync = DateTimeOffset.UtcNow;
        _settings.Save(cfg);
    }

    private static string Missing(SyncSettings cfg) =>
        cfg.IsFolder ? "Please choose a folder." : cfg.IsGist ? "Please enter your GitHub token." : "Please fill in all Supabase fields.";

    // ------------------------------------------------------------------------------------------------ While the app runs

    /// <summary>Remembers that this device has changes that are not saved at the source yet (survives a crash).</summary>
    public void MarkDirty()
    {
        var cfg = _settings.Load();
        if (!cfg.IsConfigured || cfg.Dirty) return;
        cfg.Dirty = true;
        _settings.Save(cfg);
    }

    /// <summary>
    /// Compares with the source: takes over a newer state (if this device has no changes of its own), saves waiting changes,
    /// or reports a conflict. Works with a locked vault too (then the local copy is just replaced).
    /// </summary>
    public async Task<RefreshOutcome> RefreshAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var cfg = _settings.Load();
            if (!cfg.IsConfigured)
            {
                SetStatus(SourceState.Local);
                return RefreshOutcome.NoChange;
            }

            RemoteVault? remote;
            try
            {
                remote = await CreateRemote(cfg).GetAsync(ct);
            }
            catch (SyncException ex)
            {
                SetStatus(cfg.Dirty ? SourceState.Pending : SourceState.Ready, "Not reachable: " + ex.Message + (cfg.Dirty ? " Your changes are saved on this PC and will be saved there when it works again." : ""));
                return RefreshOutcome.Offline;
            }

            if (remote == null)
            {
                SetStatus(SourceState.Error, "The vault is no longer stored at " + Label(cfg.Mode) + ". Your copy on this PC is untouched. Open Source and choose \"Move my vault here\".");
                return RefreshOutcome.Empty;
            }

            if (cfg.LastVersion == 0)
            {
                // settings of the old sync: we do not know which side is right
                SetStatus(SourceState.Conflict, "Your vault is stored at " + Label(cfg.Mode) + " and on this PC. Choose which one to keep.");
                return RefreshOutcome.Conflict;
            }

            if (remote.Version == cfg.LastVersion)
            {
                if (cfg.Dirty && _store.IsUnlocked) return await PushCoreAsync(cfg, remote, ct);
                SetStatus(cfg.Dirty ? SourceState.Pending : SourceState.Ready);
                return RefreshOutcome.NoChange;
            }

            // the source changed since we last looked
            if (cfg.Dirty)
            {
                SetStatus(SourceState.Conflict, "The vault at " + Label(cfg.Mode) + " was changed on another device, and this device has changes that are not saved yet. Choose which version to keep.");
                return RefreshOutcome.Conflict;
            }

            if (_store.IsUnlocked)
            {
                try
                {
                    var data = _store.OpenRemote(remote.Blob);
                    _store.BackupLocal();
                    _store.Apply(data);
                    Remember(cfg, remote.Version, dirty: false);
                    SetStatus(SourceState.Ready);
                    return RefreshOutcome.Pulled;
                }
                catch (CryptographicException)
                {
                    // the master password was changed on another device: take over the new vault and ask for the new password
                    _store.ReplaceWithBlob(remote.Blob);
                    Remember(cfg, remote.Version, dirty: false);
                    SetStatus(SourceState.Ready);
                    return RefreshOutcome.KeyChanged;
                }
            }

            _store.ReplaceWithBlob(remote.Blob);
            Remember(cfg, remote.Version, dirty: false);
            SetStatus(SourceState.Ready);
            return RefreshOutcome.Pulled;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Saves the changes of this device at the source (only if nobody else changed it meanwhile).</summary>
    public async Task<RefreshOutcome> PushAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var cfg = _settings.Load();
            if (!cfg.IsConfigured || !_store.IsUnlocked) return RefreshOutcome.NoChange;
            if (cfg.LastVersion == 0)
            {
                SetStatus(SourceState.Conflict, "Your vault is stored at " + Label(cfg.Mode) + " and on this PC. Choose which one to keep.");
                return RefreshOutcome.Conflict;
            }

            RemoteVault? remote;
            try
            {
                remote = await CreateRemote(cfg).GetAsync(ct);
            }
            catch (SyncException ex)
            {
                cfg.Dirty = true;
                _settings.Save(cfg);
                SetStatus(SourceState.Pending, "Not reachable: " + ex.Message + " Your changes are saved on this PC and will be saved there when it works again.");
                return RefreshOutcome.Offline;
            }
            return await PushCoreAsync(cfg, remote, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<RefreshOutcome> PushCoreAsync(SyncSettings cfg, RemoteVault? remote, CancellationToken ct)
    {
        var target = CreateRemote(cfg);
        try
        {
            if (remote != null && remote.Version != cfg.LastVersion)
            {
                cfg.Dirty = true;
                _settings.Save(cfg);
                SetStatus(SourceState.Conflict, "The vault at " + Label(cfg.Mode) + " was changed on another device. Choose which version to keep.");
                return RefreshOutcome.Conflict;
            }

            var blob = _store.ExportBlob();
            long? version = remote == null ? await target.InsertAsync(blob, ct) : await target.UpdateAsync(remote.Version, blob, ct);
            if (version == null)
            {
                cfg.Dirty = true;
                _settings.Save(cfg);
                SetStatus(SourceState.Conflict, "The vault at " + Label(cfg.Mode) + " was changed on another device. Choose which version to keep.");
                return RefreshOutcome.Conflict;
            }

            Remember(cfg, version.Value, dirty: false);
            SetStatus(SourceState.Ready);
            return RefreshOutcome.NoChange;
        }
        catch (SyncException ex)
        {
            cfg.Dirty = true;
            _settings.Save(cfg);
            SetStatus(SourceState.Pending, "Could not save: " + ex.Message + " Your changes are kept on this PC and saved later.");
            return RefreshOutcome.Offline;
        }
    }

    /// <summary>
    /// The user decided a conflict. "source" = take the version from the source (the local one is kept as a backup file,
    /// the vault is locked afterwards). "mine" = this device's version overwrites the source.
    /// </summary>
    public async Task<string> ResolveAsync(string choice, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var cfg = _settings.Load();
            if (!cfg.IsConfigured) throw new SyncException("No source is set up.");
            var target = CreateRemote(cfg);
            var remote = await target.GetAsync(ct);

            if (choice == "source")
            {
                if (remote == null) throw new SyncException("There is nothing at the source to take over.");
                var backup = _store.ReplaceWithBlob(remote.Blob);
                Remember(cfg, remote.Version, dirty: false);
                SetStatus(SourceState.Ready);
                return backup;
            }

            if (choice != "mine") throw new ArgumentException("Unknown choice.");
            if (!_store.IsUnlocked) throw new SyncException("Unlock the vault first.");
            var blob = _store.ExportBlob();
            long version = remote == null
                ? await target.InsertAsync(blob, ct)
                : await target.UpdateAsync(remote.Version, blob, ct) ?? throw new SyncException("The source was changed again just now. Please try again.");
            Remember(cfg, version, dirty: false);
            SetStatus(SourceState.Ready);
            return "";
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> SignUpAsync(SyncSettings cfg, CancellationToken ct = default)
    {
        if (!cfg.IsSupabase) throw new SyncException("Only Supabase needs an account.");
        return await _clientFactory(cfg).SignUpAsync(cfg.Email, cfg.Password, ct);
    }

    public void Save(SyncSettings cfg) => _settings.Save(cfg);
}
