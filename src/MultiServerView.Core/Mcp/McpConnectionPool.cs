namespace MultiServerView.Core.Mcp;

/// <summary>
/// Thread-safe idle connection pool keyed by server ID.
/// Reuses live connections across consecutive MCP calls, invalidates stale leases on credential or vault
/// changes, and transparently retries once on a fresh connection when a pooled connection drops.
/// </summary>
public sealed class McpConnectionPool<T> : IDisposable where T : class, IDisposable
{
    private sealed record IdleEntry(T Connection, DateTimeOffset LastUsed);

    private readonly TimeSpan _idleTimeout;
    private readonly int _maxIdlePerKey;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Timer? _cleanupTimer;
    private readonly object _gate = new();
    private readonly Dictionary<string, Stack<IdleEntry>> _idle = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _keyGeneration = new(StringComparer.Ordinal);
    private long _globalGeneration;
    private bool _disposed;

    public McpConnectionPool(TimeSpan idleTimeout, int maxIdlePerKey = 2, Func<DateTimeOffset>? clock = null)
    {
        if (maxIdlePerKey < 1) throw new ArgumentOutOfRangeException(nameof(maxIdlePerKey), "Pool must allow at least 1 idle connection per key.");
        _idleTimeout = idleTimeout;
        _maxIdlePerKey = maxIdlePerKey;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        if (clock == null && idleTimeout > TimeSpan.Zero)
        {
            var period = TimeSpan.FromSeconds(Math.Clamp(idleTimeout.TotalSeconds / 3, 10, 30));
            _cleanupTimer = new Timer(_ =>
            {
                try { EvictExpired(); } catch { /* background eviction must never crash */ }
            }, null, period, period);
        }
    }

    public int IdleCount
    {
        get
        {
            lock (_gate)
            {
                var count = 0;
                foreach (var stack in _idle.Values) count += stack.Count;
                return count;
            }
        }
    }

    public async Task<TResult> ExecuteAsync<TResult>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        Func<T, bool> isAlive,
        Func<T, Task<TResult>> action,
        Func<Exception, bool> isConnectionError,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("Pool key must not be empty.", nameof(key));

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var (conn, reused, globalGen, keyGen) = AcquireIdle(key, isAlive, allowReuse: attempt == 0);
            if (conn == null)
            {
                ct.ThrowIfCancellationRequested();
                conn = await factory(ct);
                reused = false;
            }

            try
            {
                var result = await action(conn);
                Release(key, conn, globalGen, keyGen, isAlive);
                return result;
            }
            catch (OperationCanceledException)
            {
                SafeDispose(conn);
                throw;
            }
            catch (Exception ex) when (isConnectionError(ex))
            {
                SafeDispose(conn);
                if (reused && attempt == 0) continue;
                throw;
            }
            catch
            {
                // Application-level errors (e.g., missing remote file) leave a healthy transport intact.
                Release(key, conn, globalGen, keyGen, isAlive);
                throw;
            }
        }

        throw new InvalidOperationException("Unreachable.");
    }

    public void Invalidate(string key)
    {
        List<T>? toDispose = null;
        lock (_gate)
        {
            _keyGeneration[key] = _keyGeneration.GetValueOrDefault(key) + 1;
            if (_idle.Remove(key, out var stack) && stack.Count > 0)
            {
                toDispose = new List<T>(stack.Count);
                while (stack.Count > 0) toDispose.Add(stack.Pop().Connection);
            }
        }
        DisposeAll(toDispose);
    }

    public void Clear()
    {
        List<T>? toDispose = null;
        lock (_gate)
        {
            _globalGeneration++;
            foreach (var stack in _idle.Values)
            {
                while (stack.Count > 0) (toDispose ??= new List<T>()).Add(stack.Pop().Connection);
            }
            _idle.Clear();
        }
        DisposeAll(toDispose);
    }

    public void EvictExpired()
    {
        if (_idleTimeout <= TimeSpan.Zero) return;

        List<T>? toDispose = null;
        lock (_gate)
        {
            if (_disposed) return;
            var now = _clock();
            List<string>? emptyKeys = null;

            foreach (var (key, stack) in _idle)
            {
                var items = stack.ToArray(); // LIFO: newest first, oldest last
                stack.Clear();
                for (var i = items.Length - 1; i >= 0; i--)
                {
                    if (now - items[i].LastUsed >= _idleTimeout)
                        (toDispose ??= new List<T>()).Add(items[i].Connection);
                    else
                        stack.Push(items[i]);
                }
                if (stack.Count == 0) (emptyKeys ??= new List<string>()).Add(key);
            }

            if (emptyKeys != null)
                foreach (var k in emptyKeys) _idle.Remove(k);
        }
        DisposeAll(toDispose);
    }

    public void Dispose()
    {
        List<T>? toDispose = null;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _globalGeneration++;
            foreach (var stack in _idle.Values)
            {
                while (stack.Count > 0) (toDispose ??= new List<T>()).Add(stack.Pop().Connection);
            }
            _idle.Clear();
        }
        try { _cleanupTimer?.Dispose(); } catch { /* does not matter during cleanup */ }
        DisposeAll(toDispose);
    }

    private (T? Connection, bool Reused, long GlobalGen, long KeyGen) AcquireIdle(string key, Func<T, bool> isAlive, bool allowReuse)
    {
        List<T>? toDispose = null;
        T? chosen = null;
        long globalGen;
        long keyGen;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            globalGen = _globalGeneration;
            keyGen = _keyGeneration.GetValueOrDefault(key);

            if (allowReuse && _idleTimeout > TimeSpan.Zero && _idle.TryGetValue(key, out var stack))
            {
                var now = _clock();
                while (stack.Count > 0)
                {
                    var entry = stack.Pop();
                    var expired = now - entry.LastUsed >= _idleTimeout;
                    var alive = false;
                    if (!expired)
                    {
                        try { alive = isAlive(entry.Connection); } catch { alive = false; }
                    }
                    if (!expired && alive)
                    {
                        chosen = entry.Connection;
                        break;
                    }
                    (toDispose ??= new List<T>()).Add(entry.Connection);
                }
                if (stack.Count == 0) _idle.Remove(key);
            }
        }

        DisposeAll(toDispose);
        return (chosen, chosen != null, globalGen, keyGen);
    }

    private void Release(string key, T connection, long globalGen, long keyGen, Func<T, bool> isAlive)
    {
        var dispose = false;
        lock (_gate)
        {
            var alive = false;
            if (!_disposed && _idleTimeout > TimeSpan.Zero && _globalGeneration == globalGen && _keyGeneration.GetValueOrDefault(key) == keyGen)
            {
                try { alive = isAlive(connection); } catch { alive = false; }
            }

            if (alive)
            {
                if (!_idle.TryGetValue(key, out var stack))
                {
                    stack = new Stack<IdleEntry>();
                    _idle[key] = stack;
                }
                if (stack.Count < _maxIdlePerKey)
                    stack.Push(new IdleEntry(connection, _clock()));
                else
                    dispose = true;
            }
            else
            {
                dispose = true;
            }
        }

        if (dispose) SafeDispose(connection);
    }

    private static void DisposeAll(List<T>? list)
    {
        if (list == null) return;
        foreach (var item in list) SafeDispose(item);
    }

    private static void SafeDispose(T item)
    {
        try { item.Dispose(); } catch { /* does not matter during cleanup */ }
    }
}
