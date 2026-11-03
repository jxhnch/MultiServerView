using MultiServerView.Core;
using MultiServerView.Core.Sync;

namespace MultiServerView.App;

/// <summary>
/// Synchronisiert automatisch: 3 Sekunden nach der letzten Änderung und einmal nach dem Entsperren.
/// Meldet den Zustand ans UI (Event "sync.status"); bei Änderungen aus der Cloud zusätzlich "vault.changed".
/// </summary>
internal sealed class AutoSync : IDisposable
{
    private readonly VaultStore _store;
    private readonly SyncService _sync;
    private readonly Action<object> _emit;
    private readonly System.Threading.Timer _timer;

    public AutoSync(VaultStore store, SyncService sync, Action<object> emit)
    {
        _store = store;
        _sync = sync;
        _emit = emit;
        _timer = new System.Threading.Timer(_ => _ = RunQuietlyAsync(), null, Timeout.Infinite, Timeout.Infinite);
        _store.Changed += Schedule;
    }

    /// <summary>Plant einen Sync in 3 Sekunden (mehrere Änderungen werden gebündelt).</summary>
    public void Schedule()
    {
        if (!_store.IsUnlocked || !_sync.Settings.IsConfigured) return;
        _timer.Change(TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
    }

    public void RunSoon()
    {
        if (!_store.IsUnlocked || !_sync.Settings.IsConfigured) return;
        _timer.Change(TimeSpan.FromMilliseconds(200), Timeout.InfiniteTimeSpan);
    }

    private async Task RunQuietlyAsync()
    {
        try { await RunAsync(); }
        catch { /* Status wurde bereits ans UI gemeldet */ }
    }

    /// <summary>Führt den Sync aus und meldet das Ergebnis. Wirft bei Fehlern (nach dem Melden).</summary>
    public async Task<SyncResult> RunAsync()
    {
        _emit(new { @event = "sync.status", state = "syncing", message = "Synchronisiere…" });
        try
        {
            var result = await _sync.SyncAsync();
            if (result.Outcome is SyncOutcome.Pulled or SyncOutcome.Merged)
                _emit(new { @event = "vault.changed" });

            _emit(new
            {
                @event = "sync.status",
                state = "ok",
                message = result.Outcome switch
                {
                    SyncOutcome.UpToDate => "Alles aktuell",
                    SyncOutcome.Pulled => "Änderungen aus der Cloud übernommen",
                    SyncOutcome.Merged => "Zusammengeführt",
                    _ => "Hochgeladen"
                },
                at = result.At
            });
            return result;
        }
        catch (Exception ex)
        {
            _emit(new { @event = "sync.status", state = "error", message = Bridge.Friendly(ex) });
            throw;
        }
    }

    public void Dispose()
    {
        _store.Changed -= Schedule;
        _timer.Dispose();
    }
}
