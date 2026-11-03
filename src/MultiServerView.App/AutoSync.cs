using MultiServerView.Core;
using MultiServerView.Core.Sync;

namespace MultiServerView.App;

/// <summary>
/// Synchronizes automatically: 3 seconds after the last change and once after unlocking.
/// Reports the state to the UI (event "sync.status"); for changes from the remote additionally "vault.changed".
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

    /// <summary>Schedules a sync in 3 seconds (several changes are bundled).</summary>
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
        catch { /* the status was already reported to the UI */ }
    }

    /// <summary>Runs the sync and reports the result. Throws on errors (after reporting).</summary>
    public async Task<SyncResult> RunAsync()
    {
        _emit(new { @event = "sync.status", state = "syncing", message = "Syncing…" });
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
                    SyncOutcome.UpToDate => "Up to date",
                    SyncOutcome.Pulled => "Changes taken over from the remote",
                    SyncOutcome.Merged => "Merged",
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
