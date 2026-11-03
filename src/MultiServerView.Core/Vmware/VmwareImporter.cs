namespace MultiServerView.Core.Vmware;

public sealed record VmwareImportOptions(string Workspace, string IdentityId);

public sealed record VmwareImportResult(IReadOnlyList<ServerEntry> Servers, int NoIp, int Skipped);

/// <summary>Turns VMware Workstation VMs into ready-made hosts.</summary>
public static class VmwareImporter
{
    /// <summary>Same VM = same path (case-insensitive, like on Windows).</summary>
    public static string Ref(string vmxPath) => Path.GetFullPath(vmxPath).ToLowerInvariant();

    /// <summary>
    /// Builds the hosts. If a running VM has no address from the DHCP entry, it asks VMware directly (<paramref name="vmrun"/>),
    /// which knows the address live from the VMware Tools.
    /// </summary>
    public static async Task<VmwareImportResult> BuildAsync(
        IEnumerable<VmwareVm> vms, VmwareImportOptions options, ISet<string> existingRefs, IVmrun? vmrun, CancellationToken ct = default)
    {
        var all = vms.ToList();
        var wanted = all.Where(v => !existingRefs.Contains(Ref(v.VmxPath))).ToList();

        using var gate = new SemaphoreSlim(4);
        var ips = await Task.WhenAll(wanted.Select(async vm =>
        {
            if (vm.Ip != null || !vm.Running || vmrun == null) return vm.Ip;
            await gate.WaitAsync(ct);
            try
            {
                return await vmrun.GetGuestIpAsync(vm.VmxPath, ct);
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
            var ip = ips[i];
            if (ip == null) noIp++;

            var (protocol, os) = VmwareLibrary.MapGuest(vm.GuestOs);
            servers.Add(new ServerEntry
            {
                Name = vm.Name,
                // Without a known address (VM never started) the name stays; adjustable in the host form.
                Host = ip ?? vm.Name,
                Protocol = protocol,
                Port = ServerEntry.DefaultPort(protocol),
                Os = os,
                Workspace = options.Workspace.Trim(),
                IdentityId = options.IdentityId,
                Notes = "VMware Workstation · " + vm.VmxPath,
                VmwareRef = Ref(vm.VmxPath)
            });
        }
        return new VmwareImportResult(servers, noIp, all.Count - wanted.Count);
    }
}
