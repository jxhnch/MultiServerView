namespace MultiServerView.Core.Proxmox;

/// <summary>What is taken over from Proxmox as the group (second level below the workspace).</summary>
public enum ProxmoxGroupBy
{
    None,

    /// <summary>The Proxmox pool of a VM (VMs without a pool get no group).</summary>
    Pool,

    /// <summary>The Proxmox node the VM runs on.</summary>
    Node
}

/// <summary>How imported VMs are sorted in.</summary>
/// <param name="Workspace">Workspace for all (or, with <paramref name="WorkspaceFromPool"/>, only for VMs without a pool). Empty = none.</param>
/// <param name="WorkspaceFromPool">The Proxmox pool of a VM becomes its workspace. Otherwise all go into the fixed <paramref name="Workspace"/>, no matter which pool they are in.</param>
/// <param name="IdentityId">Credentials (identity) used by all imported hosts (or empty).</param>
/// <param name="GroupBy">What is taken over as the group.</param>
/// <param name="KeyId">SSH key used by all imported SSH hosts (or empty). Not together with an identity.</param>
/// <param name="Username">User name that goes with the key (empty = none yet).</param>
public sealed record ProxmoxImportOptions(
    string Workspace, bool WorkspaceFromPool, string IdentityId, ProxmoxGroupBy GroupBy, string KeyId = "", string Username = "");

public sealed record ProxmoxImportResult(IReadOnlyList<ServerEntry> Servers, int NoIp, int Skipped);

/// <summary>Turns Proxmox VMs and containers into ready-made hosts.</summary>
public static class ProxmoxImporter
{
    /// <summary>Unique origin of a host: "connection id:node:VM number".</summary>
    public static string Ref(string connectionId, string node, long vmId) => $"{connectionId}:{node}:{vmId}";

    /// <summary>Origin of the host for the web interface of Proxmox itself (two parts, so it is never taken for a VM).</summary>
    public static string WebRef(string connectionId) => $"{connectionId}:web";

    /// <summary>A "Web" host that opens the interface of the Proxmox server.</summary>
    public static ServerEntry WebInterface(ProxmoxEntry connection, string workspace)
    {
        var url = ProxmoxClient.NormalizeUrl(connection.Url);
        var target = Web.WebTarget.Parse(url, 0);
        return new ServerEntry
        {
            Name = $"{connection.Name} (web interface)",
            Host = Web.UrlResolver.Exact(url),
            Protocol = Protocol.Web,
            Port = target.Port,
            Os = "proxmox",
            Workspace = workspace.Trim(),
            Notes = "Web interface of Proxmox",
            ProxmoxRef = WebRef(connection.Id)
        };
    }

    public static async Task<ProxmoxImportResult> BuildAsync(
        ProxmoxClient client, string connectionId, IEnumerable<ProxmoxVm> vms, ProxmoxImportOptions options,
        ISet<string> existingRefs, CancellationToken ct = default)
    {
        var wanted = vms.Where(v => !existingRefs.Contains(Ref(connectionId, v.Node, v.VmId))).ToList();
        var skipped = vms.Count() - wanted.Count;

        // A few queries per VM: a few at the same time, but not all at once.
        using var gate = new SemaphoreSlim(6);
        var guests = await Task.WhenAll(wanted.Select(async vm =>
        {
            await gate.WaitAsync(ct);
            try
            {
                return await client.DescribeAsync(vm, ct);
            }
            catch (ProxmoxException)
            {
                return new ProxmoxGuest(null, Protocol.Ssh, "");
            }
            finally
            {
                gate.Release();
            }
        }));

        var servers = new List<ServerEntry>();
        var noIp = 0;
        for (var i = 0; i < wanted.Count; i++)
        {
            var vm = wanted[i];
            var guest = guests[i];
            if (guest.Ip == null) noIp++;
            var useKey = options.KeyId.Length > 0 && guest.Protocol == Protocol.Ssh;

            servers.Add(new ServerEntry
            {
                Auth = useKey ? AuthMethod.Key : AuthMethod.Password,
                KeyId = useKey ? options.KeyId : "",
                Username = useKey ? options.Username.Trim() : "",
                Name = vm.Name,
                // Without an IP (VM off, no guest agent) the name stays as the address - it can be adjusted in the host form.
                Host = guest.Ip ?? vm.Name,
                Protocol = guest.Protocol,
                Port = ServerEntry.DefaultPort(guest.Protocol),
                Os = guest.Os,
                Workspace = options.WorkspaceFromPool && vm.Pool.Length > 0 ? vm.Pool : options.Workspace.Trim(),
                Group = options.GroupBy switch { ProxmoxGroupBy.Pool => vm.Pool, ProxmoxGroupBy.Node => vm.Node, _ => "" },
                Tags = vm.Tags.ToList(),
                IdentityId = options.IdentityId,
                Notes = $"Proxmox {vm.Node} · {(vm.IsContainer ? "Container" : "VM")} {vm.VmId}",
                ProxmoxRef = Ref(connectionId, vm.Node, vm.VmId)
            });
        }
        return new ProxmoxImportResult(servers, noIp, skipped);
    }
}
