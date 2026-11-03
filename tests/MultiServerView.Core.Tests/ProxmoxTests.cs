using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using MultiServerView.Core;
using MultiServerView.Core.Proxmox;
using Xunit;

namespace MultiServerView.Core.Tests;

/// <summary>Imitates a Proxmox API: path (without /api2/json) -> JSON response. Unknown paths return 500 like a missing guest agent.</summary>
internal sealed class FakeProxmox : HttpMessageHandler
{
    public readonly Dictionary<string, string> Routes = new();
    public readonly List<string> Requests = new();
    public string? LastAuthorization;
    public HttpStatusCode? Force;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request.RequestUri!.PathAndQuery);
        LastAuthorization = request.Headers.TryGetValues("Authorization", out var v) ? v.Single() : null;
        if (Force is { } status) return Task.FromResult(new HttpResponseMessage(status));

        var path = request.RequestUri.PathAndQuery.Replace("/api2/json", "");
        if (!Routes.TryGetValue(path, out var json))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError) { ReasonPhrase = "QEMU guest agent is not running" });
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
}

public class ProxmoxTests
{
    private static (ProxmoxClient Client, FakeProxmox Fake) NewClient()
    {
        var fake = new FakeProxmox();
        var client = new ProxmoxClient(new HttpClient(fake), "pve.example.com", "root@pam!msv", "secret-123");
        return (client, fake);
    }

    private const string Resources = """
    {"data":[
      {"type":"node","node":"pve","status":"online"},
      {"type":"storage","storage":"local","node":"pve"},
      {"type":"qemu","vmid":100,"name":"win11-miller","node":"pve","status":"running","pool":"Miller","tags":"school;windows","template":0},
      {"type":"qemu","vmid":101,"name":"debian-miller","node":"pve","status":"running","pool":"Miller","tags":"","template":0},
      {"type":"qemu","vmid":102,"name":"ubuntu-off","node":"pve2","status":"stopped","pool":"Smith","template":0},
      {"type":"lxc","vmid":200,"name":"web","node":"pve","status":"running","tags":"","template":0},
      {"type":"qemu","vmid":9000,"name":"vorlage","node":"pve","status":"stopped","template":1}
    ]}
    """;

    [Theory]
    [InlineData("pve.example.com", "https://pve.example.com:8006")]
    [InlineData("https://pve.example.com/", "https://pve.example.com:8006")]
    [InlineData("https://pve.example.com:8443", "https://pve.example.com:8443")]
    [InlineData("  192.168.0.121:8006 ", "https://192.168.0.121:8006")]
    public void Url_is_normalised_to_https_with_port(string input, string expected) =>
        Assert.Equal(expected, ProxmoxClient.NormalizeUrl(input));

    [Fact]
    public void Plain_http_and_empty_urls_are_rejected()
    {
        Assert.Throws<ProxmoxException>(() => ProxmoxClient.NormalizeUrl("http://pve:8006"));
        Assert.Throws<ProxmoxException>(() => ProxmoxClient.NormalizeUrl("  "));
    }

    [Fact]
    public async Task Requests_carry_the_api_token_and_version_is_read()
    {
        var (client, fake) = NewClient();
        fake.Routes["/version"] = """{"data":{"version":"9.2.3","release":"9.2"}}""";

        Assert.Equal("9.2.3", await client.VersionAsync());
        Assert.Equal("PVEAPIToken=root@pam!msv=secret-123", fake.LastAuthorization);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "token ID or secret")]
    [InlineData(HttpStatusCode.Forbidden, "PVEAuditor")]
    public async Task Auth_errors_are_explained(HttpStatusCode status, string expected)
    {
        var (client, fake) = NewClient();
        fake.Force = status;

        var ex = await Assert.ThrowsAsync<ProxmoxException>(() => client.VersionAsync());
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public async Task List_returns_vms_and_containers_only_sorted_and_marks_templates()
    {
        var (client, fake) = NewClient();
        fake.Routes["/cluster/resources?type=vm"] = Resources;

        var vms = await client.ListVmsAsync();

        Assert.Equal(5, vms.Count); // nodes and storage are not VMs
        Assert.Equal(new[] { 9000L, 200L, 101L, 100L, 102L }, vms.Select(v => v.VmId).ToArray()); // first without a pool, then Miller, Smith
        var win = vms.Single(v => v.VmId == 100);
        Assert.Equal(new[] { "school", "windows" }, win.Tags);
        Assert.Equal("Miller", win.Pool);
        Assert.True(vms.Single(v => v.VmId == 9000).Template);
        Assert.True(vms.Single(v => v.VmId == 200).IsContainer);
    }

    [Fact]
    public async Task Windows_vm_with_agent_becomes_rdp_and_takes_the_first_real_ipv4()
    {
        var (client, fake) = NewClient();
        fake.Routes["/nodes/pve/qemu/100/config"] = """{"data":{"ostype":"win11","agent":"1"}}""";
        fake.Routes["/nodes/pve/qemu/100/agent/network-get-interfaces"] = """
        {"data":{"result":[
          {"name":"lo","ip-addresses":[{"ip-address-type":"ipv4","ip-address":"127.0.0.1"}]},
          {"name":"docker0","ip-addresses":[{"ip-address-type":"ipv4","ip-address":"172.17.0.1"}]},
          {"name":"Ethernet","ip-addresses":[{"ip-address-type":"ipv6","ip-address":"fe80::1"},{"ip-address-type":"ipv4","ip-address":"10.1.2.3"}]}
        ]}}
        """;
        var vm = new ProxmoxVm("pve", 100, "qemu", "win11", "running", "", Array.Empty<string>(), false);

        var guest = await client.DescribeAsync(vm);

        Assert.Equal("10.1.2.3", guest.Ip);
        Assert.Equal(Protocol.Rdp, guest.Protocol);
        Assert.Equal("windows", guest.Os);
    }

    [Fact]
    public async Task Linux_vm_without_agent_falls_back_to_static_ip_and_stays_ssh()
    {
        var (client, fake) = NewClient();
        fake.Routes["/nodes/pve/qemu/101/config"] = """{"data":{"ostype":"l26","ipconfig0":"ip=192.168.5.20/24,gw=192.168.5.1"}}""";
        var vm = new ProxmoxVm("pve", 101, "qemu", "deb", "running", "", Array.Empty<string>(), false);

        var guest = await client.DescribeAsync(vm);

        Assert.Equal("192.168.5.20", guest.Ip);
        Assert.Equal(Protocol.Ssh, guest.Protocol);
    }

    [Fact]
    public async Task Stopped_vm_without_static_ip_has_no_ip()
    {
        var (client, fake) = NewClient();
        fake.Routes["/nodes/pve2/qemu/102/config"] = """{"data":{"ostype":"l26"}}""";
        var vm = new ProxmoxVm("pve2", 102, "qemu", "off", "stopped", "", Array.Empty<string>(), false);

        Assert.Null((await client.DescribeAsync(vm)).Ip);
        Assert.DoesNotContain(fake.Requests, r => r.Contains("agent")); // off = do not even ask the agent
    }

    [Fact]
    public async Task Container_uses_live_interfaces_for_dhcp_and_maps_the_os()
    {
        var (client, fake) = NewClient();
        fake.Routes["/nodes/pve/lxc/200/config"] = """{"data":{"ostype":"archlinux","net0":"name=eth0,bridge=vmbr0,ip=dhcp"}}""";
        fake.Routes["/nodes/pve/lxc/200/interfaces"] = """{"data":[{"name":"lo","inet":"127.0.0.1/8"},{"name":"eth0","inet":"192.168.0.57/24","hwaddr":"AA"}]}""";
        var vm = new ProxmoxVm("pve", 200, "lxc", "web", "running", "", Array.Empty<string>(), false);

        var guest = await client.DescribeAsync(vm);

        Assert.Equal("192.168.0.57", guest.Ip);
        Assert.Equal("arch", guest.Os);
        Assert.Equal(Protocol.Ssh, guest.Protocol);
    }

    [Theory]
    [InlineData("win10", true)]
    [InlineData("win11", true)]
    [InlineData("w2k8", true)]
    [InlineData("wxp", true)]
    [InlineData("l26", false)]
    [InlineData("other", false)]
    [InlineData("solaris", false)]
    public void Windows_types_are_recognised(string ostype, bool windows) =>
        Assert.Equal(windows, ProxmoxClient.IsWindowsType(ostype));

    [Theory]
    [InlineData("192.168.1.5", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.3.4", false)]
    [InlineData("fe80::1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("no-ip", false)]
    public void Only_usable_ipv4_addresses_count(string ip, bool usable) =>
        Assert.Equal(usable, ProxmoxClient.UsableIpv4(ip));

    [Fact]
    public void Fingerprints_round_trip_between_colon_and_plain_form()
    {
        Assert.Equal("AB:CD:EF", ProxmoxClient.Group("ABCDEF"));
        Assert.Equal("ABCDEF", ProxmoxClient.Clean("ab:cd:ef"));
        Assert.Equal("ABCDEF", ProxmoxClient.Clean(" AB CD EF "));
    }

    // ------------------------------------------------------------------------------------------------ Import

    private static async Task<(ProxmoxImportResult Result, ProxmoxClient Client)> ImportAsync(ProxmoxImportOptions options, ISet<string>? existing = null)
    {
        var (client, fake) = NewClient();
        fake.Routes["/cluster/resources?type=vm"] = Resources;
        fake.Routes["/nodes/pve/qemu/100/config"] = """{"data":{"ostype":"win11"}}""";
        fake.Routes["/nodes/pve/qemu/100/agent/network-get-interfaces"] = """{"data":{"result":[{"name":"Ethernet","ip-addresses":[{"ip-address-type":"ipv4","ip-address":"10.1.2.3"}]}]}}""";
        fake.Routes["/nodes/pve/qemu/101/config"] = """{"data":{"ostype":"l26"}}""";
        fake.Routes["/nodes/pve2/qemu/102/config"] = """{"data":{"ostype":"l26"}}""";
        var vms = (await client.ListVmsAsync()).Where(v => v.VmId is 100 or 101 or 102).ToList();
        return (await ProxmoxImporter.BuildAsync(client, "conn1", vms, options, existing ?? new HashSet<string>()), client);
    }

    [Fact]
    public async Task Import_builds_hosts_with_pool_as_workspace_and_identity()
    {
        var (result, _) = await ImportAsync(new ProxmoxImportOptions("", WorkspaceFromPool: true, IdentityId: "ident-1", GroupBy: ProxmoxGroupBy.Node));

        var byName = result.Servers.ToDictionary(s => s.Name);
        Assert.Equal(3, byName.Count);

        var win = byName["win11-miller"];
        Assert.Equal("10.1.2.3", win.Host);
        Assert.Equal(Protocol.Rdp, win.Protocol);
        Assert.Equal(3389, win.Port);
        Assert.Equal("Miller", win.Workspace);
        Assert.Equal("pve", win.Group);
        Assert.Equal("ident-1", win.IdentityId);
        Assert.Equal("conn1:pve:100", win.ProxmoxRef);
        Assert.Contains("school", win.Tags);

        // Without an IP the name stays as the address and is counted.
        Assert.Equal("debian-miller", byName["debian-miller"].Host);
        Assert.Equal(22, byName["debian-miller"].Port);
        Assert.Equal("Smith", byName["ubuntu-off"].Workspace);
        Assert.Equal("pve2", byName["ubuntu-off"].Group);
        Assert.Equal(2, result.NoIp);
    }

    [Fact]
    public async Task Import_uses_fixed_workspace_when_pool_is_not_wanted_and_skips_known_vms()
    {
        var existing = new HashSet<string> { ProxmoxImporter.Ref("conn1", "pve", 101) };
        var (result, _) = await ImportAsync(new ProxmoxImportOptions("Class 5", WorkspaceFromPool: false, IdentityId: "", GroupBy: ProxmoxGroupBy.None), existing);

        Assert.Equal(2, result.Servers.Count);
        Assert.Equal(1, result.Skipped);
        Assert.DoesNotContain(result.Servers, s => s.Name == "debian-miller");
        Assert.All(result.Servers, s =>
        {
            Assert.Equal("Class 5", s.Workspace);
            Assert.Equal("", s.Group);
        });
    }

    [Fact]
    public async Task One_fixed_workspace_for_everything_with_the_pool_as_group()
    {
        // "All into one workspace, the pool is the group": the pool does not determine the workspace.
        var (result, _) = await ImportAsync(new ProxmoxImportOptions("Class 5", WorkspaceFromPool: false, IdentityId: "", GroupBy: ProxmoxGroupBy.Pool));

        var byName = result.Servers.ToDictionary(s => s.Name);
        Assert.All(result.Servers, s => Assert.Equal("Class 5", s.Workspace));
        Assert.Equal("Miller", byName["win11-miller"].Group);
        Assert.Equal("Miller", byName["debian-miller"].Group);
        Assert.Equal("Smith", byName["ubuntu-off"].Group);
    }

    [Fact]
    public async Task Pool_group_is_empty_for_vms_without_a_pool()
    {
        var (client, fake) = NewClient();
        fake.Routes["/cluster/resources?type=vm"] = Resources;
        fake.Routes["/nodes/pve/lxc/200/config"] = """{"data":{"ostype":"debian"}}""";
        var vm = (await client.ListVmsAsync()).Single(v => v.VmId == 200);

        var result = await ProxmoxImporter.BuildAsync(client, "c", new[] { vm }, new ProxmoxImportOptions("X", false, "", ProxmoxGroupBy.Pool), new HashSet<string>());

        Assert.Equal("", result.Servers.Single().Group);
    }

    [Fact]
    public async Task Pool_workspace_falls_back_to_the_fixed_one_for_vms_outside_a_pool()
    {
        var (client, fake) = NewClient();
        fake.Routes["/cluster/resources?type=vm"] = Resources;
        fake.Routes["/nodes/pve/lxc/200/config"] = """{"data":{"ostype":"debian"}}""";
        var vm = (await client.ListVmsAsync()).Single(v => v.VmId == 200);

        var result = await ProxmoxImporter.BuildAsync(client, "c", new[] { vm }, new ProxmoxImportOptions("General", true, "", ProxmoxGroupBy.None), new HashSet<string>());

        Assert.Equal("General", result.Servers.Single().Workspace);
        Assert.Equal("debian", result.Servers.Single().Os);
    }

    // ------------------------------------------------------------------------------------------------ Vault

    [Fact]
    public void Proxmox_connections_persist_in_the_vault_and_merge_by_newest()
    {
        var dir = Path.Combine(Path.GetTempPath(), "msv-pve-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(dir, "vault.msv");
            var store = new VaultStore(path);
            store.Create("master-password");
            var saved = store.UpsertProxmox(new ProxmoxEntry { Name = "pve", Url = "https://pve:8006", TokenId = "root@pam!msv", TokenSecret = "s3cret", CertFingerprint = "AB" });

            var again = new VaultStore(path);
            again.Unlock("master-password");
            var loaded = again.FindProxmox(saved.Id)!;
            Assert.Equal("s3cret", loaded.TokenSecret);
            Assert.DoesNotContain("s3cret", Encoding.UTF8.GetString(File.ReadAllBytes(path))); // only encrypted on disk

            var older = new ProxmoxEntry { Id = "x", Name = "old", UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-3) };
            var newer = new ProxmoxEntry { Id = "x", Name = "new", UpdatedAt = DateTimeOffset.UtcNow };
            var merged = VaultMerge.Merge(new VaultData { Proxmox = { older } }, new VaultData { Proxmox = { newer } });
            Assert.Equal("new", merged.Proxmox.Single().Name);

            again.DeleteProxmox(saved.Id);
            Assert.Null(again.FindProxmox(saved.Id));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    // ------------------------------------------------------------------------------------------------ Deleting and clearing

    private static (VaultStore Store, string Dir) NewStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), "msv-clear-" + Guid.NewGuid().ToString("N"));
        var store = new VaultStore(Path.Combine(dir, "vault.msv"));
        store.Create("master-password");
        return (store, dir);
    }

    [Fact]
    public void DeleteMany_removes_hosts_and_their_forwards_and_leaves_tombstones()
    {
        var (store, dir) = NewStore();
        try
        {
            var a = store.Upsert(new ServerEntry { Name = "a", Host = "a" });
            var b = store.Upsert(new ServerEntry { Name = "b", Host = "b" });
            var keep = store.Upsert(new ServerEntry { Name = "keep", Host = "k" });
            store.UpsertForward(new PortForwardEntry { Name = "f", ServerId = a.Id, BindPort = 1, DestHost = "x", DestPort = 2 });

            Assert.Equal(2, store.DeleteMany(new[] { a.Id, b.Id, "does-not-exist" }));

            Assert.Equal(new[] { keep.Id }, store.List().Select(s => s.Id).ToArray());
            Assert.Empty(store.ListForwards());
            Assert.Equal(0, store.DeleteMany(new[] { "does-not-exist" }));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Clear_needs_the_right_password_and_empties_everything_but_keeps_the_password()
    {
        var (store, dir) = NewStore();
        try
        {
            store.Upsert(new ServerEntry { Name = "a", Host = "a" });
            store.UpsertKey(new KeyEntry { Name = "k", PrivateKey = "x" });
            store.UpsertIdentity(new IdentityEntry { Name = "i" });
            store.UpsertSnippet(new SnippetEntry { Name = "s", Command = "ls" });
            store.UpsertProxmox(new ProxmoxEntry { Name = "p" });
            store.CheckHostKey("h", 22, "SHA256:abc");

            Assert.Throws<WrongPasswordException>(() => store.Clear("wrong-password"));
            Assert.Single(store.List()); // nothing happened

            var before = store.Clear("master-password");

            Assert.Equal(1, before.Servers);
            Assert.Equal(1, before.Proxmox);
            Assert.Equal(1, before.KnownHosts);
            Assert.Empty(store.List());
            Assert.Empty(store.ListKeys());
            Assert.Empty(store.ListProxmox());
            Assert.Equal(0, store.Summary().KnownHosts);

            // The master password still applies.
            var again = new VaultStore(Path.Combine(dir, "vault.msv"));
            again.Unlock("master-password");
            Assert.Empty(again.List());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Cleared_items_do_not_come_back_from_another_device()
    {
        var (store, dir) = NewStore();
        try
        {
            var host = store.Upsert(new ServerEntry { Name = "a", Host = "a" });
            var otherDevice = store.Snapshot(); // another device still has the old state

            store.Clear("master-password");

            var merged = VaultMerge.Merge(store.Snapshot(), otherDevice);
            Assert.DoesNotContain(merged.Servers, s => s.Id == host.Id); // the tombstone wins
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
