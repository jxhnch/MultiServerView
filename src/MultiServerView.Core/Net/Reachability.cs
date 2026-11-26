using System.Net.Sockets;
using MultiServerView.Core.Web;

namespace MultiServerView.Core.Net;

/// <summary>
/// "Is the host there?" A plain TCP connection to the port the host is used on, closed again right away: no login, nothing is sent.
/// "up" = the port answers, "closed" = the machine answers but refuses the port, "down" = no answer.
/// </summary>
public static class Reachability
{
    public const string Up = "up", Closed = "closed", Down = "down";

    /// <summary>null = cannot be checked from here (serial port, TeamViewer, behind a jump host, unusable address).</summary>
    public static (string Host, int Port)? TargetOf(ServerEntry server)
    {
        if (server.Protocol is Protocol.Serial or Protocol.Teamviewer || server.JumpHostId.Length > 0) return null;
        if (server.Protocol == Protocol.Web)
        {
            try
            {
                var web = WebTarget.Parse(server);
                return (web.Host, web.Port);
            }
            catch (ArgumentException) { return null; }
        }
        var host = server.Host.Trim();
        var port = server.Port > 0 ? server.Port : ServerEntry.DefaultPort(server.Protocol);
        return host.Length == 0 || port is <= 0 or > 65535 ? null : (host, port);
    }

    public static async Task<string> ProbeAsync(string host, int port, TimeSpan timeout, CancellationToken ct = default)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, limit.Token);
            return Up;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused) { return Closed; }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException) { return Down; }
    }

    /// <summary>Checks several hosts, a few at a time. Hosts that cannot be checked are left out of the result.</summary>
    public static async Task<Dictionary<string, string>> CheckAsync(IEnumerable<ServerEntry> servers, TimeSpan timeout, CancellationToken ct = default)
    {
        using var slots = new SemaphoreSlim(16);
        var checks = servers.Select(s => (s.Id, Target: TargetOf(s))).Where(x => x.Target != null).Select(async x =>
        {
            await slots.WaitAsync(ct);
            try { return (x.Id, State: await ProbeAsync(x.Target!.Value.Host, x.Target.Value.Port, timeout, ct)); }
            finally { slots.Release(); }
        }).ToList();
        return (await Task.WhenAll(checks)).ToDictionary(r => r.Id, r => r.State);
    }
}
