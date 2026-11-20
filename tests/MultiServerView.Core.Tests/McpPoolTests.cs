using System.Net.Sockets;
using MultiServerView.Core.Mcp;
using MultiServerView.Core.Ssh;
using Renci.SshNet.Common;
using Xunit;

namespace MultiServerView.Core.Tests;

public class McpPoolTests
{
    private sealed class FakeConn(int id) : IDisposable
    {
        public int Id { get; } = id;
        public bool Alive { get; set; } = true;
        public bool Disposed { get; private set; }
        public void Dispose()
        {
            Disposed = true;
            Alive = false;
        }
    }

    private sealed class SimulatedConnectionException(string msg) : Exception(msg);
    private sealed class SimulatedAppException(string msg) : Exception(msg);

    private static bool IsConnErr(Exception ex) => ex is SimulatedConnectionException;

    [Fact]
    public async Task ReusesIdleConnectionForSameKey()
    {
        var now = DateTimeOffset.UtcNow;
        using var pool = new McpConnectionPool<FakeConn>(TimeSpan.FromSeconds(90), clock: () => now);
        var created = 0;

        var id1 = await pool.ExecuteAsync("h1", _ => Task.FromResult(new FakeConn(++created)), c => c.Alive, c => Task.FromResult(c.Id), IsConnErr);
        Assert.Equal(1, pool.IdleCount);

        now += TimeSpan.FromSeconds(30);
        var id2 = await pool.ExecuteAsync("h1", _ => Task.FromResult(new FakeConn(++created)), c => c.Alive, c => Task.FromResult(c.Id), IsConnErr);

        Assert.Equal(1, created);
        Assert.Equal(id1, id2);
        Assert.Equal(1, pool.IdleCount);
    }

    [Fact]
    public async Task SeparatesConnectionsByKey()
    {
        var now = DateTimeOffset.UtcNow;
        using var pool = new McpConnectionPool<FakeConn>(TimeSpan.FromSeconds(90), clock: () => now);
        var created = 0;

        var id1 = await pool.ExecuteAsync("h1", _ => Task.FromResult(new FakeConn(++created)), c => c.Alive, c => Task.FromResult(c.Id), IsConnErr);
        var id2 = await pool.ExecuteAsync("h2", _ => Task.FromResult(new FakeConn(++created)), c => c.Alive, c => Task.FromResult(c.Id), IsConnErr);

        Assert.Equal(2, created);
        Assert.NotEqual(id1, id2);
        Assert.Equal(2, pool.IdleCount);
    }

    [Fact]
    public async Task EvictsIdleConnectionsPastTimeout()
    {
        var now = DateTimeOffset.UtcNow;
        using var pool = new McpConnectionPool<FakeConn>(TimeSpan.FromSeconds(90), clock: () => now);
        FakeConn? first = null;
        var created = 0;

        await pool.ExecuteAsync("h1", _ => Task.FromResult(first = new FakeConn(++created)), c => c.Alive, c => Task.FromResult(c.Id), IsConnErr);
        Assert.Equal(1, pool.IdleCount);

        now += TimeSpan.FromSeconds(95);
        pool.EvictExpired();
        Assert.Equal(0, pool.IdleCount);
        Assert.True(first!.Disposed);

        var id2 = await pool.ExecuteAsync("h1", _ => Task.FromResult(new FakeConn(++created)), c => c.Alive, c => Task.FromResult(c.Id), IsConnErr);
        Assert.Equal(2, created);
        Assert.Equal(2, id2);
    }

    [Fact]
    public async Task InvalidateDisposesIdleAndPreventsInFlightReturn()
    {
        var now = DateTimeOffset.UtcNow;
        using var pool = new McpConnectionPool<FakeConn>(TimeSpan.FromSeconds(90), clock: () => now);
        var created = 0;
        FakeConn? other = null;
        await pool.ExecuteAsync("h2", _ => Task.FromResult(other = new FakeConn(++created)), c => c.Alive, c => Task.FromResult(c.Id), IsConnErr);

        FakeConn? inflight = null;
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var task = pool.ExecuteAsync("h1", _ => Task.FromResult(inflight = new FakeConn(++created)), c => c.Alive, async c =>
        {
            entered.SetResult();
            await release.Task;
            return c.Id;
        }, IsConnErr);

        await entered.Task;
        pool.Invalidate("h1");
        Assert.False(other!.Disposed);
        Assert.Equal(1, pool.IdleCount);

        release.SetResult();
        await task;

        Assert.True(inflight!.Disposed);
        Assert.Equal(1, pool.IdleCount); // only h2 remains pooled
    }

    [Fact]
    public async Task ClearDisposesAllIdleAndPreventsInFlightReturn()
    {
        var now = DateTimeOffset.UtcNow;
        using var pool = new McpConnectionPool<FakeConn>(TimeSpan.FromSeconds(90), clock: () => now);
        var created = 0;
        FakeConn? idleConn = null;
        await pool.ExecuteAsync("h2", _ => Task.FromResult(idleConn = new FakeConn(++created)), c => c.Alive, c => Task.FromResult(c.Id), IsConnErr);

        FakeConn? inflight = null;
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var task = pool.ExecuteAsync("h1", _ => Task.FromResult(inflight = new FakeConn(++created)), c => c.Alive, async c =>
        {
            entered.SetResult();
            await release.Task;
            return c.Id;
        }, IsConnErr);

        await entered.Task;
        pool.Clear();
        Assert.True(idleConn!.Disposed);
        Assert.Equal(0, pool.IdleCount);

        release.SetResult();
        await task;

        Assert.True(inflight!.Disposed);
        Assert.Equal(0, pool.IdleCount);
    }

    [Fact]
    public async Task RetriesOnceOnConnectionErrorForReusedConnection()
    {
        var now = DateTimeOffset.UtcNow;
        using var pool = new McpConnectionPool<FakeConn>(TimeSpan.FromSeconds(90), clock: () => now);
        var created = 0;
        var list = new List<FakeConn>();
        await pool.ExecuteAsync("h1", _ =>
        {
            var c = new FakeConn(++created);
            list.Add(c);
            return Task.FromResult(c);
        }, c => c.Alive, c => Task.FromResult(c.Id), IsConnErr);

        var attempts = 0;
        var id = await pool.ExecuteAsync("h1", _ =>
        {
            var c = new FakeConn(++created);
            list.Add(c);
            return Task.FromResult(c);
        }, c => c.Alive, c =>
        {
            attempts++;
            if (attempts == 1) throw new SimulatedConnectionException("broken pipe");
            return Task.FromResult(c.Id);
        }, IsConnErr);

        Assert.Equal(2, attempts);
        Assert.Equal(2, created);
        Assert.Equal(2, id);
        Assert.True(list[0].Disposed);
        Assert.False(list[1].Disposed);
        Assert.Equal(1, pool.IdleCount);
    }

    [Fact]
    public async Task DoesNotRetryFreshConnectionError()
    {
        var now = DateTimeOffset.UtcNow;
        using var pool = new McpConnectionPool<FakeConn>(TimeSpan.FromSeconds(90), clock: () => now);
        var created = 0;
        FakeConn? fresh = null;

        await Assert.ThrowsAsync<SimulatedConnectionException>(() => pool.ExecuteAsync(
            "h1",
            _ => Task.FromResult(fresh = new FakeConn(++created)),
            c => c.Alive,
            _ => Task.FromException<int>(new SimulatedConnectionException("reset")),
            IsConnErr));

        Assert.Equal(1, created);
        Assert.True(fresh!.Disposed);
        Assert.Equal(0, pool.IdleCount);
    }

    [Fact]
    public async Task DoesNotRetryNonConnectionErrorAndKeepsHealthyConnection()
    {
        var now = DateTimeOffset.UtcNow;
        using var pool = new McpConnectionPool<FakeConn>(TimeSpan.FromSeconds(90), clock: () => now);
        var created = 0;
        FakeConn? conn = null;
        await pool.ExecuteAsync("h1", _ => Task.FromResult(conn = new FakeConn(++created)), c => c.Alive, c => Task.FromResult(c.Id), IsConnErr);

        await Assert.ThrowsAsync<SimulatedAppException>(() => pool.ExecuteAsync(
            "h1",
            _ => Task.FromResult(new FakeConn(++created)),
            c => c.Alive,
            _ => Task.FromException<int>(new SimulatedAppException("no such file")),
            IsConnErr));

        Assert.Equal(1, created);
        Assert.False(conn!.Disposed);
        Assert.Equal(1, pool.IdleCount);
    }

    [Fact]
    public async Task DisposesConnectionOnCancellation()
    {
        var now = DateTimeOffset.UtcNow;
        using var pool = new McpConnectionPool<FakeConn>(TimeSpan.FromSeconds(90), clock: () => now);
        var created = 0;
        FakeConn? conn = null;
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAsync<OperationCanceledException>(() => pool.ExecuteAsync(
            "h1",
            _ => Task.FromResult(conn = new FakeConn(++created)),
            c => c.Alive,
            _ =>
            {
                cts.Cancel();
                return Task.FromException<int>(new OperationCanceledException(cts.Token));
            },
            IsConnErr,
            cts.Token));

        Assert.True(conn!.Disposed);
        Assert.Equal(0, pool.IdleCount);
    }

    [Fact]
    public async Task RespectsMaxIdlePerKeyCap()
    {
        var now = DateTimeOffset.UtcNow;
        using var pool = new McpConnectionPool<FakeConn>(TimeSpan.FromSeconds(90), maxIdlePerKey: 2, clock: () => now);
        var created = 0;
        var conns = new List<FakeConn>();
        var gate = new TaskCompletionSource();
        var readyCount = 0;
        var allReady = new TaskCompletionSource();

        var tasks = Enumerable.Range(0, 3).Select(_ => pool.ExecuteAsync("h1", _ =>
        {
            var c = new FakeConn(Interlocked.Increment(ref created));
            lock (conns) conns.Add(c);
            return Task.FromResult(c);
        }, c => c.Alive, async c =>
        {
            if (Interlocked.Increment(ref readyCount) == 3) allReady.SetResult();
            await gate.Task;
            return c.Id;
        }, IsConnErr)).ToArray();

        await allReady.Task;
        gate.SetResult();
        await Task.WhenAll(tasks);

        Assert.Equal(3, created);
        Assert.Equal(2, pool.IdleCount);
        Assert.Equal(1, conns.Count(c => c.Disposed));
    }

    [Fact]
    public async Task DisposesAllAndRejectsNewCallsAfterDispose()
    {
        var now = DateTimeOffset.UtcNow;
        var pool = new McpConnectionPool<FakeConn>(TimeSpan.FromSeconds(90), clock: () => now);
        FakeConn? conn = null;
        await pool.ExecuteAsync("h1", _ => Task.FromResult(conn = new FakeConn(1)), c => c.Alive, c => Task.FromResult(c.Id), IsConnErr);

        pool.Dispose();
        Assert.True(conn!.Disposed);
        Assert.Equal(0, pool.IdleCount);

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            pool.ExecuteAsync("h1", _ => Task.FromResult(new FakeConn(2)), c => c.Alive, c => Task.FromResult(c.Id), IsConnErr));
    }

    [Fact]
    public void SshExecIsConnectionErrorClassification()
    {
        Assert.True(SshExec.IsConnectionError(new SshConnectionException("dropped")));
        Assert.True(SshExec.IsConnectionError(new SocketException((int)SocketError.ConnectionReset)));
        Assert.True(SshExec.IsConnectionError(new IOException("eof")));
        Assert.True(SshExec.IsConnectionError(new SshException("channel closed")));
        Assert.True(SshExec.IsConnectionError(new ObjectDisposedException("client")));
        Assert.True(SshExec.IsConnectionError(new SshConnectException("wrap", new SocketException((int)SocketError.ConnectionRefused))));

        Assert.False(SshExec.IsConnectionError(new SshAuthenticationException("bad auth")));
        Assert.False(SshExec.IsConnectionError(new SshOperationTimeoutException("timed out")));
        Assert.False(SshExec.IsConnectionError(new SshPassPhraseNullOrEmptyException("passphrase")));
        Assert.False(SshExec.IsConnectionError(new SftpPathNotFoundException("missing")));
        Assert.False(SshExec.IsConnectionError(new SftpPermissionDeniedException("denied")));
        Assert.False(SshExec.IsConnectionError(new InvalidOperationException("vault locked")));
    }
}
