using System.Net;
using System.Net.Sockets;
using Renci.SshNet;

namespace MultiServerView.Core.Ssh;

/// <summary>Findet zu einer Server-Id den (aufgelösten) Host samt Key. null = gibt es nicht (mehr).</summary>
public delegate (ServerEntry Server, KeyEntry? Key)? JumpResolver(string serverId);

/// <summary>
/// Verbindet über eine Kette von Jump Hosts (A -> B -> Ziel). Am Ende gibt es einen lokalen Port auf 127.0.0.1,
/// der durch alle Hops bis zum Ziel führt - SSH, SFTP, VNC und RDP verbinden sich einfach dorthin.
/// </summary>
public sealed class JumpTunnel : IDisposable
{
    public const int MaxHops = 5;
    private readonly List<SshClient> _clients = new();
    private readonly List<ForwardedPortLocal> _ports = new();

    public string Host => "127.0.0.1";
    public int Port { get; private set; }

    /// <summary>Öffnet den Tunnel. Gibt null zurück, wenn der Server gar keinen Jump Host hat.</summary>
    public static async Task<JumpTunnel?> OpenAsync(ServerEntry target, JumpResolver? resolve,
        Func<string, int, string, HostKeyTrust> check, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(target.JumpHostId)) return null;
        if (resolve == null) throw new SshConnectException("Jump Hosts sind hier nicht verfügbar.");

        // Kette von innen (direkt vor dem Ziel) nach aussen auflösen.
        var hops = new List<(ServerEntry Server, KeyEntry? Key)>();
        var seen = new HashSet<string> { target.Id };
        var currentId = target.JumpHostId;
        while (!string.IsNullOrEmpty(currentId))
        {
            if (!seen.Add(currentId) || hops.Count >= MaxHops)
                throw new SshConnectException("Die Jump-Host-Kette ist ungültig (Schleife oder mehr als 5 Stationen).");
            var hop = resolve(currentId) ?? throw new SshConnectException("Der Jump Host dieses Servers existiert nicht mehr.");
            hops.Add(hop);
            currentId = hop.Server.JumpHostId;
        }
        hops.Reverse(); // zuerst der äusserste, der direkt erreichbar ist

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
                    throw new SshConnectException($"Jump Host «{server.Name}»: {hostKeyError() ?? SshConnector.Describe(ex)}", ex);
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
        foreach (var p in _ports) try { p.Stop(); } catch { /* egal beim Aufräumen */ }
        foreach (var c in _clients.AsEnumerable().Reverse()) try { c.Dispose(); } catch { /* egal beim Aufräumen */ }
        _ports.Clear();
        _clients.Clear();
    }
}
