using System.Net;
using System.Net.Sockets;
using Renci.SshNet;

namespace MultiServerView.Core.Ssh;

/// <summary>Finds the (resolved) host including its key for a server id. null = does not exist (any more).</summary>
public delegate (ServerEntry Server, KeyEntry? Key)? JumpResolver(string serverId);

/// <summary>
/// Connects through a chain of jump hosts (A -> B -> target). At the end there is a local port on 127.0.0.1
/// that leads through all hops to the target - SSH, SFTP, VNC and RDP simply connect there.
/// </summary>
public sealed class JumpTunnel : IDisposable
{
    public const int MaxHops = 5;
    private readonly List<SshClient> _clients = new();
    private readonly List<ForwardedPortLocal> _ports = new();

    public string Host => "127.0.0.1";
    public int Port { get; private set; }

    /// <summary>Opens the tunnel. Returns null if the server has no jump host at all.</summary>
    public static async Task<JumpTunnel?> OpenAsync(ServerEntry target, JumpResolver? resolve,
        Func<string, int, string, HostKeyTrust> check, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(target.JumpHostId)) return null;
        if (resolve == null) throw new SshConnectException("Jump hosts are not available here.");

        // Resolve the chain from the inside (directly before the target) to the outside.
        var hops = new List<(ServerEntry Server, KeyEntry? Key)>();
        var seen = new HashSet<string> { target.Id };
        var currentId = target.JumpHostId;
        while (!string.IsNullOrEmpty(currentId))
        {
            if (!seen.Add(currentId) || hops.Count >= MaxHops)
                throw new SshConnectException("The jump host chain is invalid (loop or more than 5 hops).");
            var hop = resolve(currentId) ?? throw new SshConnectException("The jump host of this server no longer exists.");
            hops.Add(hop);
            currentId = hop.Server.JumpHostId;
        }
        hops.Reverse(); // the outermost one first, which is directly reachable

        var tunnel = new JumpTunnel();
        try
        {
            var host = hops[0].Server.Host;
            var port = hops[0].Server.Port > 0 ? hops[0].Server.Port : 22;

            for (var i = 0; i < hops.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (server, key) = hops[i];
                var client = new SshClient(SshConnector.BuildConnectionInfo(server, key, host, port)) { KeepAliveInterval = SshConnector.KeepAlive };
                var hostKeyError = SshConnector.HookHostKey(client, server, check, null);
                try
                {
                    await Task.Run(() => client.Connect(), ct);
                }
                catch (Exception ex)
                {
                    client.Dispose();
                    throw new SshConnectException($"Jump host \"{server.Name}\": {hostKeyError() ?? SshConnector.Describe(ex)}", ex);
                }
                tunnel._clients.Add(client);

                var (nextHost, nextPort) = i + 1 < hops.Count
                    ? (hops[i + 1].Server.Host, hops[i + 1].Server.Port > 0 ? hops[i + 1].Server.Port : 22)
                    : (target.Host, target.Port > 0 ? target.Port : ServerEntry.DefaultPort(target.Protocol));

                var local = FreePort();
                var forward = new ForwardedPortLocal("127.0.0.1", (uint)local, nextHost, (uint)nextPort);
                client.AddForwardedPort(forward);
                forward.Start();
                tunnel._ports.Add(forward);

                host = "127.0.0.1";
                port = local;
            }
            tunnel.Port = port;
            return tunnel;
        }
        catch
        {
            tunnel.Dispose();
            throw;
        }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    public void Dispose()
    {
        foreach (var p in _ports) try { p.Stop(); } catch { /* does not matter during cleanup */ }
        foreach (var c in _clients.AsEnumerable().Reverse()) try { c.Dispose(); } catch { /* does not matter during cleanup */ }
        _ports.Clear();
        _clients.Clear();
    }
}
