using MultiServerView.Core;
using MultiServerView.Core.Sync;

namespace MultiServerView.App;

/// <summary>
/// Keeps the vault and its source (folder, GitHub, Supabase) in step: saves changes there 2 seconds after the last change,
/// looks for changes made on other devices every 45 seconds and right after unlocking.
/// The UI learns about it through the event "source.status"; "vault.changed" means that newer data was taken over,
/// "vault.replaced" that the vault was replaced and locked (master password changed on another device).
/// </summary>
internal sealed class AutoSync : IDisposable
{
    private readonly VaultStore _store;
    private readonly SourceService _source;
    private readonly Action<object> _emit;
    private readonly Action _onReplaced;
    private readonly System.Threading.Timer _pushTimer;
    private readonly System.Threading.Timer _pollTimer;

    public AutoSync(VaultStore store, SourceService source, Action<object> emit, Action onReplaced)
    {
        _store = store;
        _source = source;
        _emit = emit;
        _onReplaced = onReplaced;
        _pushTimer = new System.Threading.Timer(_ => _ = PushQuietlyAsync(), null, Timeout.Infinite, Timeout.Infinite);
        _pollTimer = new System.Threading.Timer(_ => _ = RefreshQuietlyAsync(), null, TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(45));
        _store.Changed += OnChanged;
        _source.StatusChanged += EmitStatus;
    }

    public static object StatusDto(SourceStatus s) => new
    {
        @event = "source.status",
        kind = s.Kind,
        state = s.State.ToString().ToLowerInvariant(),
        message = s.Message,
        lastSaved = s.LastSaved
    };

    private void EmitStatus(SourceStatus s) => _emit(StatusDto(s));

    private void OnChanged()
    {
        if (!_store.IsUnlocked || !_source.Settings.IsConfigured) return;
        _source.MarkDirty(); // survives a crash: the next start saves it
        _pushTimer.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
    }

    private async Task PushQuietlyAsync()
    {
        try { Handle(await _source.PushAsync()); }
        catch (Exception ex) { _emit(new { @event = "source.status", kind = _source.Status.Kind, state = "error", message = Bridge.Friendly(ex), lastSaved = (DateTimeOffset?)null }); }
    }

    private async Task RefreshQuietlyAsync()
    {
        if (!_store.IsUnlocked || !_source.Settings.IsConfigured) return;
        try { Handle(await _source.RefreshAsync()); }
        catch (Exception) { /* the next round tries again */ }
    }

    /// <summary>Looks at the source now (after unlocking, or when the user clicks "Try again").</summary>
    public async Task<RefreshOutcome> RefreshNowAsync()
    {
        var outcome = await _source.RefreshAsync();
        Handle(outcome);
        return outcome;
    }

    private void Handle(RefreshOutcome outcome)
    {
        switch (outcome)
        {
            case RefreshOutcome.Pulled when _store.IsUnlocked:
                _emit(new { @event = "vault.changed" });
                break;
            case RefreshOutcome.KeyChanged:
                _onReplaced();
                _emit(new { @event = "vault.replaced" });
                break;
        }
        EmitStatus(_source.Status);
    }

    public void Dispose()
    {
        _store.Changed -= OnChanged;
        _source.StatusChanged -= EmitStatus;
        _pushTimer.Dispose();
        _pollTimer.Dispose();
    }
}
