using System.Text.Json;

namespace MultiServerView.Core.Sync;

public enum SyncOutcome
{
    /// <summary>Cloud war leer, lokaler Stand wurde hochgeladen.</summary>
    Created,

    /// <summary>Lokal und Cloud waren schon identisch.</summary>
    UpToDate,

    /// <summary>Nur Änderungen aus der Cloud übernommen.</summary>
    Pulled,

    /// <summary>Nur lokale Änderungen hochgeladen.</summary>
    Pushed,

    /// <summary>Beide Seiten hatten Änderungen: zusammengeführt und hochgeladen.</summary>
    Merged
}

public sealed record SyncResult(SyncOutcome Outcome, long Version, DateTimeOffset At);

/// <summary>
/// Gleicht den lokalen Vault mit der Cloud ab: laden -> zusammenführen -> hochladen.
/// Der Server sieht nur den verschlüsselten Blob; das Zusammenführen passiert lokal.
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
            if (!cfg.IsConfigured) throw new SyncException("Die Cloud ist noch nicht eingerichtet.");

            var client = _clientFactory(cfg);
            var token = await client.SignInAsync(cfg.Email, cfg.Password, ct);

            for (var attempt = 0; attempt < 4; attempt++)
            {
                var result = await TryOnceAsync(client, token, ct);
                if (result != null)
                {
                    cfg.LastSync = result.At;
                    _settings.Save(cfg);
                    return result;
                }
                // Konflikt: jemand war schneller -> nochmal von vorne (neu laden, mergen).
            }
            throw new SyncException("Die Cloud wird gerade von einem anderen Gerät geändert. Bitte gleich nochmal versuchen.");
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<SyncResult?> TryOnceAsync(SupabaseClient client, string token, CancellationToken ct)
    {
        var remote = await client.GetVaultAsync(token, ct);
        var now = DateTimeOffset.UtcNow;

        if (remote == null)
        {
            var v = await client.InsertVaultAsync(token, _store.ExportBlob(), ct);
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
                "Der Vault in der Cloud passt nicht zu diesem Gerät (anderes Master-Passwort oder getrennt erstellt). " +
                "Auf einem neuen Gerät: «Aus Cloud laden» statt einen neuen Vault zu erstellen. " +
                "Falls du das Master-Passwort auf einem anderen Gerät geändert hast: «Neues Master-Passwort übernehmen» (Cloud-Seite).");
        }

        var local = _store.Snapshot();
        var merged = VaultMerge.Merge(local, remoteData);
        var mergedFp = VaultMerge.Fingerprint(merged);
        var localChanged = VaultMerge.Fingerprint(local) != mergedFp;   // Cloud brachte etwas Neues
        var remoteChanged = VaultMerge.Fingerprint(remoteData) != mergedFp; // Lokal hat etwas Neues

        if (localChanged) _store.Apply(merged);

        if (!remoteChanged)
            return new SyncResult(localChanged ? SyncOutcome.Pulled : SyncOutcome.UpToDate, remote.Version, now);

        var newVersion = await client.UpdateVaultAsync(token, remote.Version, _store.ExportBlob(), ct);
        if (newVersion == null) return null; // Konflikt

        return new SyncResult(localChanged ? SyncOutcome.Merged : SyncOutcome.Pushed, newVersion.Value, now);
    }

    /// <summary>Neues Gerät: Cloud-Blob als lokalen Vault installieren (danach mit dem Master-Passwort entsperren).</summary>
    public async Task DownloadInitialAsync(SyncSettings cfg, CancellationToken ct = default)
    {
        if (_store.Exists) throw new SyncException("Auf diesem Gerät gibt es schon einen Vault.");
        if (!cfg.IsConfigured) throw new SyncException("Bitte alle Cloud-Felder ausfüllen.");

        var client = _clientFactory(cfg);
        var token = await client.SignInAsync(cfg.Email, cfg.Password, ct);
        var remote = await client.GetVaultAsync(token, ct)
                     ?? throw new SyncException("In der Cloud liegt noch kein Vault für dieses Konto.");

        _store.InstallBlob(remote.Blob);
        cfg.LastSync = DateTimeOffset.UtcNow;
        _settings.Save(cfg);
    }

    /// <summary>
    /// Überschreibt den Cloud-Vault mit dem lokalen Stand (nötig nach einem Wechsel des Master-Passworts, weil der
    /// Cloud-Blob dann mit dem alten Schlüssel verschlüsselt ist). Andere Geräte müssen das neue Passwort übernehmen.
    /// </summary>
    public async Task ForceUploadAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var cfg = _settings.Load();
            if (!cfg.IsConfigured) throw new SyncException("Die Cloud ist noch nicht eingerichtet.");
            var client = _clientFactory(cfg);
            var token = await client.SignInAsync(cfg.Email, cfg.Password, ct);
            var remote = await client.GetVaultAsync(token, ct);
            if (remote == null) await client.InsertVaultAsync(token, _store.ExportBlob(), ct);
            else if (await client.UpdateVaultAsync(token, remote.Version, _store.ExportBlob(), ct) == null)
                throw new SyncException("Die Cloud wurde gerade von einem anderen Gerät geändert. Bitte nochmal versuchen.");
            cfg.LastSync = DateTimeOffset.UtcNow;
            _settings.Save(cfg);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Für Geräte, die noch das alte Master-Passwort haben: lädt den Cloud-Vault, öffnet ihn mit dem NEUEN Passwort,
    /// führt ihn mit dem lokalen Stand zusammen und übernimmt den neuen Schlüssel.
    /// </summary>
    public async Task AdoptCloudPasswordAsync(string newPassword, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var cfg = _settings.Load();
            if (!cfg.IsConfigured) throw new SyncException("Die Cloud ist noch nicht eingerichtet.");
            var client = _clientFactory(cfg);
            var token = await client.SignInAsync(cfg.Email, cfg.Password, ct);
            var remote = await client.GetVaultAsync(token, ct) ?? throw new SyncException("In der Cloud liegt noch kein Vault.");

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

    /// <summary>Prüft nur, ob die Zugangsdaten stimmen und die Tabelle existiert.</summary>
    public async Task TestConnectionAsync(SyncSettings cfg, CancellationToken ct = default)
    {
        var client = _clientFactory(cfg);
        var token = await client.SignInAsync(cfg.Email, cfg.Password, ct);
        await client.GetVaultAsync(token, ct);
    }

    public async Task<bool> SignUpAsync(SyncSettings cfg, CancellationToken ct = default)
    {
        var client = _clientFactory(cfg);
        return await client.SignUpAsync(cfg.Email, cfg.Password, ct);
    }

    public void Save(SyncSettings cfg) => _settings.Save(cfg);

    public void Disconnect() => _settings.Clear();
}
