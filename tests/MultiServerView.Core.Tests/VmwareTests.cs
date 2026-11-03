using MultiServerView.Core;
using MultiServerView.Core.Vmware;
using Xunit;

namespace MultiServerView.Core.Tests;

internal sealed class FakeVmrun : IVmrun
{
    public readonly List<string> Running = new();
    public readonly Dictionary<string, string?> Ips = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<string> IpAsked = new();

    public Task<IReadOnlyList<string>> ListRunningAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>(Running);

    public Task<string?> GetGuestIpAsync(string vmxPath, CancellationToken ct = default)
    {
        IpAsked.Add(vmxPath);
        return Task.FromResult(Ips.TryGetValue(vmxPath, out var ip) ? ip : null);
    }
}

public class VmwareTests
{
    // This is what inventory.vmls looks like in VMware Workstation (the first VM has an empty entry).
    private const string Inventory = """
        .encoding = "UTF-8"
        vmlist1.config = ""
        vmlist2.config = "C:\VMs\Windows 11 x64 (2)\Windows 11 x64 (2).vmx"
        vmlist2.DisplayName = "Windows 11 x64 (2)"
        vmlist2.ParentID = "0"
        vmlist3.config = "C:\VMs\ubuntu\ubuntu.vmx"
        """;

    private const string WindowsVmx = """
        .encoding = "windows-1252"
        displayName = "Windows 11 x64"
        guestOS = "windows11-64"
        ethernet0.connectionType = "nat"
        ethernet0.addressType = "generated"
        ethernet0.present = "TRUE"
        ethernet0.generatedAddress = "00:0c:29:fd:bc:ae"
        """;

    [Fact]
    public void Inventory_lists_vmx_paths_and_skips_empty_entries() =>
        Assert.Equal(new[] { @"C:\VMs\Windows 11 x64 (2)\Windows 11 x64 (2).vmx", @"C:\VMs\ubuntu\ubuntu.vmx" }, VmwareLibrary.ParseInventory(Inventory).ToArray());

    [Fact]
    public void Vmx_is_read_for_name_guest_nic_and_connection()
    {
        var vm = VmwareLibrary.FromVmx(@"C:\VMs\w.vmx", WindowsVmx);

        Assert.Equal("Windows 11 x64", vm.Name);
        Assert.Equal("windows11-64", vm.GuestOs);
        Assert.Equal("00:0c:29:fd:bc:ae", vm.Mac);
        Assert.Equal("nat", vm.ConnectionType);
        Assert.False(vm.Running);
        Assert.Null(vm.Ip);
    }

    [Fact]
    public void Missing_display_name_falls_back_to_the_file_name_and_fixed_mac_wins()
    {
        var vm = VmwareLibrary.FromVmx(@"C:\VMs\kali\kali-box.vmx", """
            ethernet0.present = "TRUE"
            ethernet0.address = "00-50-56-AA-BB-CC"
            ethernet0.generatedAddress = "00:0c:29:00:00:01"
            """);

        Assert.Equal("kali-box", vm.Name);
        Assert.Equal("00:50:56:aa:bb:cc", vm.Mac);
    }

    [Fact]
    public void Disabled_first_nic_is_skipped()
    {
        var vm = VmwareLibrary.FromVmx(@"C:\x.vmx", """
            ethernet0.present = "FALSE"
            ethernet0.generatedAddress = "00:0c:29:11:11:11"
            ethernet1.present = "TRUE"
            ethernet1.generatedAddress = "00:0c:29:22:22:22"
            ethernet1.connectionType = "bridged"
            """);

        Assert.Equal("00:0c:29:22:22:22", vm.Mac);
        Assert.Equal("bridged", vm.ConnectionType);
    }

    [Theory]
    [InlineData("windows11-64", Protocol.Rdp, "windows")]
    [InlineData("windows2019srv-64", Protocol.Rdp, "windows")]
    [InlineData("winxppro", Protocol.Rdp, "windows")]
    [InlineData("ubuntu-64", Protocol.Ssh, "ubuntu")]
    [InlineData("debian11-64", Protocol.Ssh, "debian")]
    [InlineData("rhel8-64", Protocol.Ssh, "redhat")]
    [InlineData("sles15-64", Protocol.Ssh, "opensuse")]
    [InlineData("other4xlinux-64", Protocol.Ssh, "linux")]
    [InlineData("darwin23-64", Protocol.Ssh, "macos")]
    [InlineData("", Protocol.Ssh, "")]
    public void Guest_os_decides_protocol_and_icon(string guest, Protocol protocol, string os) =>
        Assert.Equal((protocol, os), VmwareLibrary.MapGuest(guest));

    [Fact]
    public void Leases_use_the_latest_entry_per_mac_and_ignore_comments()
    {
        const string text = """
            # All times in this file are in UTC (GMT), not your local timezone.
            lease 192.168.40.128 {
            	starts 4 2026/11/04 10:00:00;
            	ends 4 2026/11/04 10:30:00;
            	hardware ethernet 00:0c:29:fd:bc:ae;
            	client-hostname "old";
            }
            lease 192.168.40.130 {
            	starts 5 2026/11/05 10:00:00;
            	ends 5 2026/11/05 10:30:00;
            	hardware ethernet 00:0C:29:FD:BC:AE;
            }
            lease 192.168.40.131 {
            	ends 4 2026/11/04 09:00:00;
            	hardware ethernet 00:0c:29:99:99:99;
            }
            """;

        var leases = VmwareLibrary.ParseLeases(text);

        Assert.Equal(2, leases.Count);
        Assert.Equal("192.168.40.130", leases["00:0c:29:fd:bc:ae"].Ip); // the later end wins, case does not matter
        Assert.Equal("192.168.40.131", leases["00:0c:29:99:99:99"].Ip);
        Assert.Empty(VmwareLibrary.ParseLeases("# only comments\n"));
    }

    [Fact]
    public void Vm_gets_its_ip_from_the_lease_by_mac()
    {
        var leases = VmwareLibrary.ParseLeases("lease 192.168.40.128 { ends 4 2026/11/04 10:30:00; hardware ethernet 00:0c:29:fd:bc:ae; }");

        Assert.Equal("192.168.40.128", VmwareLibrary.FromVmx(@"C:\w.vmx", WindowsVmx, running: false, leases).Ip);
    }

    [Fact]
    public void Vmrun_output_is_parsed()
    {
        Assert.Equal(new[] { @"C:\VMs\a.vmx", @"C:\VMs\b.vmx" }, Vmrun.ParseList("Total running VMs: 2\r\nC:\\VMs\\a.vmx\r\nC:\\VMs\\b.vmx\r\n"));
        Assert.Empty(Vmrun.ParseList("Total running VMs: 0\r\n"));
        Assert.Equal("192.168.40.128", Vmrun.ParseIp("192.168.40.128\r\n"));
        Assert.Null(Vmrun.ParseIp("Error: Unable to get the IP address."));
        Assert.Null(Vmrun.ParseIp("169.254.1.1"));
    }

    [Fact]
    public void FindVmx_combines_inventory_and_folders_without_duplicates()
    {
        var dir = Path.Combine(Path.GetTempPath(), "msv-vmw-" + Guid.NewGuid().ToString("N"));
        try
        {
            var a = Path.Combine(dir, "a", "a.vmx");
            var b = Path.Combine(dir, "b", "deep", "b.vmx");
            Directory.CreateDirectory(Path.GetDirectoryName(a)!);
            Directory.CreateDirectory(Path.GetDirectoryName(b)!);
            File.WriteAllText(a, WindowsVmx);
            File.WriteAllText(b, WindowsVmx);
            File.WriteAllText(Path.Combine(dir, "a", "a.vmxf"), "not meant");
            var inventory = Path.Combine(dir, "inventory.vmls");
            File.WriteAllText(inventory, $"vmlist1.config = \"{a}\"\nvmlist2.config = \"{Path.Combine(dir, "does-not-exist.vmx")}\"\n");

            var found = VmwareLibrary.FindVmx(inventory, new[] { dir, Path.Combine(dir, "folder-missing") });

            Assert.Equal(new[] { a, b }.OrderBy(x => x, StringComparer.OrdinalIgnoreCase), found); // a only once, missing files ignored, .vmxf not included
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    // ------------------------------------------------------------------------------------------------ Import

    [Fact]
    public async Task Import_builds_hosts_asks_vmrun_only_for_running_vms_without_ip_and_skips_known_ones()
    {
        var win = new VmwareVm(@"C:\VMs\win\win.vmx", "win11", "windows11-64", "00:0c:29:00:00:01", "nat", Running: true, Ip: null);
        var ubu = new VmwareVm(@"C:\VMs\ubu\ubu.vmx", "ubuntu", "ubuntu-64", "00:0c:29:00:00:02", "nat", Running: false, Ip: "192.168.40.5");
        var off = new VmwareVm(@"C:\VMs\off\off.vmx", "off", "debian11-64", "00:0c:29:00:00:03", "nat", Running: false, Ip: null);
        var known = new VmwareVm(@"C:\VMs\alt\alt.vmx", "old", "ubuntu-64", "", "", Running: false, Ip: null);

        var vmrun = new FakeVmrun();
        vmrun.Ips[win.VmxPath] = "192.168.40.9";

        var result = await VmwareImporter.BuildAsync(new[] { win, ubu, off, known }, new VmwareImportOptions("  School ", "ident-1"),
            new HashSet<string> { VmwareImporter.Ref(known.VmxPath) }, vmrun);

        var by = result.Servers.ToDictionary(s => s.Name);
        Assert.Equal(3, by.Count);
        Assert.Equal(1, result.Skipped);

        Assert.Equal("192.168.40.9", by["win11"].Host);   // running VM without a DHCP entry: from vmrun
        Assert.Equal(Protocol.Rdp, by["win11"].Protocol);
        Assert.Equal(3389, by["win11"].Port);
        Assert.Equal("windows", by["win11"].Os);
        Assert.Equal("192.168.40.5", by["ubuntu"].Host);  // address from the DHCP entry
        Assert.Equal("ubuntu", by["ubuntu"].Os);
        Assert.Equal("off", by["off"].Host);              // nothing known: the name stays
        Assert.Equal(1, result.NoIp);
        Assert.All(result.Servers, s =>
        {
            Assert.Equal("School", s.Workspace);
            Assert.Equal("ident-1", s.IdentityId);
            Assert.False(string.IsNullOrEmpty(s.VmwareRef));
        });
        Assert.Equal(new[] { win.VmxPath }, vmrun.IpAsked); // stopped VMs are not even asked
    }

    [Fact]
    public void Refs_ignore_case_so_the_same_vm_is_never_imported_twice() =>
        Assert.Equal(VmwareImporter.Ref(@"C:\VMs\A\A.VMX"), VmwareImporter.Ref(@"c:\vms\a\a.vmx"));
}
