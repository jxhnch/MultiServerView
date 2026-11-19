using System.Collections.Concurrent;
using System.Text.Json;

namespace MultiServerView.Host;

/// <summary>
/// Asks the UI to do something with an open remote screen (RDP or VNC tab): take a screenshot, move the mouse, type.
/// The pixels live in the UI (canvas), so the request goes to the window as an event and the UI answers with the
/// bridge method "mcp.screenReply".
/// </summary>
internal sealed class ScreenHub
{
    private readonly Action<object> _emitToUi;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();

    public ScreenHub(Action<object> emitToUi) => _emitToUi = emitToUi;

    public async Task<JsonElement> RequestAsync(string op, object args, TimeSpan timeout, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            _emitToUi(new { @event = "mcp.screen", requestId = id, op, args });
            return await tcs.Task.WaitAsync(timeout, ct);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("The MultiServerView window did not answer in time. Is the app open and unlocked? Screen tools need the window.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>The UI's answer: { requestId, ok, result, error }.</summary>
    public void Reply(JsonElement p)
    {
        var id = p.TryGetProperty("requestId", out var r) ? r.GetString() ?? "" : "";
        if (!_pending.TryGetValue(id, out var tcs)) return;
        if (p.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
            tcs.TrySetResult(p.TryGetProperty("result", out var res) ? res.Clone() : default);
        else
            tcs.TrySetException(new InvalidOperationException(p.TryGetProperty("error", out var err) ? err.GetString() ?? "Screen action failed." : "Screen action failed."));
    }
}
