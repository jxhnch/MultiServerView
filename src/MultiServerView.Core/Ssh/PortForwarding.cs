using System.Collections.Concurrent;
using Renci.SshNet;

namespace MultiServerView.Core.Ssh;

/// <summary>A running port forward. Has its own SSH connection so it runs independently of terminal tabs.</summary>
public sealed class ForwardRunner : IDisposable
{
    private readonly PortForwardEntry _rule;
    private readonly ServerEntry _server;
    private readonly KeyEntry? _key;
    private readonly Func<string, int, string, HostKeyTrust> _check;
    private readonly JumpResolver? _jumps;
    private JumpTunnel? _tunnel;
    private SshClient? _client;
    private int _stopped;

    /// <summary>The forward ended (reason, or null on a normal stop).</summary>
    public event Action<string?>? Stopped;

    public ForwardRunner(PortForwardEntry rule, ServerEntry server, KeyEntry? key, Func<string, int, string, HostKeyTrust> check, JumpResolver? jumps = null)
    {
        _jumps = jumps;
        _rule = rule;
        _server = server;
        _key = key;
        _check = check;
    }

    public async Task StartAsync()
    {
        if (_rule.BindPort is < 1 or > 65535) throw new ArgumentException("The local port must be between 1 and 65535.");
        if (_rule.Type != ForwardType.Dynamic && (string.IsNullOrWhiteSpace(_rule.DestHost) || _rule.DestPort is < 1 or > 65535))
            throw new ArgumentException("Destination address and port are missing.");

        var tunnel = await JumpTunnel.OpenAsync(_server, _jumps, _check);
        var client = new SshClient(SshConnector.BuildConnectionInfo(_server, _key, tunnel?.Host, tunnel?.Port)) { KeepAliveInterval = SshConnector.KeepAlive };
        var hostKeyError = SshConnector.HookHostKey(client, _server, _check, null);
        try
        {
            await Task.Run(() => client.Connect());
        }
        catch (Exception ex)
        {
            client.Dispose();
            tunnel?.Dispose();
            throw new SshConnectException(hostKeyError() ?? SshConnector.Describe(ex), ex);
        }
        _tunnel = tunnel;

        var bind = string.IsNullOrWhiteSpace(_rule.BindHost) ? "127.0.0.1" : _rule.BindHost.Trim();
        ForwardedPort port = _rule.Type switch
        {
            ForwardType.Local => new ForwardedPortLocal(bind, (uint)_rule.BindPort, _rule.DestHost.Trim(), (uint)_rule.DestPort),
            ForwardType.Remote => new ForwardedPortRemote(bind, (uint)_rule.BindPort, _rule.DestHost.Trim(), (uint)_rule.DestPort),
            _ => new ForwardedPortDynamic(bind, (uint)_rule.BindPort)
        };
        port.Exception += (_, e) => Stop(e.Exception.Message);
        client.ErrorOccurred += (_, e) => Stop(e.Exception.Message);
        _client = client;

        try
        {
            client.AddForwardedPort(port);
            port.Start();
        }
        catch (Exception ex)
        {
            Dispose();
            throw new SshConnectException($"Port {_rule.BindPort} could not be opened: {ex.Message}", ex);
        }
    }

    private void Stop(string? reason)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        try { _client?.Dispose(); } catch { /* does not matter during cleanup */ }
        try { _tunnel?.Dispose(); } catch { /* does not matter during cleanup */ }
        Stopped?.Invoke(reason);
    }

    public void Dispose() => Stop(null);
}

/// <summary>Manages all running port forwards (by rule id).</summary>
public sealed class PortForwardManager : IDisposable
{
    private readonly ConcurrentDictionary<string, ForwardRunner> _running = new();

    /// <summary>(rule id, running, error/reason)</summary>
    public event Action<string, bool, string?>? StatusChanged;

    public bool IsRunning(string ruleId) => _running.ContainsKey(ruleId);

    public IReadOnlyCollection<string> RunningIds => _running.Keys.ToList();

    public async Task StartAsync(PortForwardEntry rule, ServerEntry server, KeyEntry? key, Func<string, int, string, HostKeyTrust> check, JumpResolver? jumps = null)
    {
        if (_running.ContainsKey(rule.Id)) return;

        var runner = new ForwardRunner(rule, server, key, check, jumps);
        runner.Stopped += reason =>
        {
            if (_running.TryGetValue(rule.Id, out var current) && ReferenceEquals(current, runner)) _running.TryRemove(rule.Id, out _);
            StatusChanged?.Invoke(rule.Id, false, reason);
        };
        await runner.StartAsync();
        _running[rule.Id] = runner;
        StatusChanged?.Invoke(rule.Id, true, null);
    }

    public void Stop(string ruleId)
    {
        if (_running.TryRemove(ruleId, out var runner)) runner.Dispose();
    }

    public void StopAll()
    {
        foreach (var id in _running.Keys.ToList()) Stop(id);
    }

    public void Dispose() => StopAll();
}
