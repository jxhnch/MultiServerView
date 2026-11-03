using System.Net;
using System.Net.Sockets;
using System.Text;
using MultiServerView.Core;
using MultiServerView.Core.Import;
using MultiServerView.Core.Sftp;
using MultiServerView.Core.Ssh;
using MultiServerView.Core.Sync;
using Xunit;

namespace MultiServerView.Core.Tests;

public class ImporterRound3Tests
{
    [Fact]
    public void SshConfig_imports_hosts_defaults_and_proxyjump()
    {
        const string cfg = """
            # my servers
            Host *
                User admin
                Port 2200

            Host bastion
                HostName bastion.example.ch
                User jump

            Host web db-alias
                HostName 10.0.0.5
                ProxyJump jump@bastion:22
                IdentityFile ~/.ssh/id_ed25519

            Host *.internal
                User nobody

            Match host foo
                User bar

            Host plain
            """;

        var r = SshConfigImporter.Parse(cfg);

        Assert.Equal(new[] { "bastion", "web", "plain" }, r.Servers.Select(s => s.Name));
        var bastion = r.Servers[0];
        Assert.Equal("bastion.example.ch", bastion.Host);
        Assert.Equal("jump", bastion.Username);
        Assert.Equal(2200, bastion.Port);                  // from "Host *"
        var web = r.Servers[1];
        Assert.Equal("10.0.0.5", web.Host);
        Assert.Equal("admin", web.Username);               // default from "Host *"
        Assert.Equal(bastion.Id, web.JumpHostId);          // ProxyJump -> imported host
        Assert.Equal("plain", r.Servers[2].Host);          // ohne HostName = Alias
        Assert.True(r.Skipped >= 2);                       // "*.internal" and "Match"
    }

    [Fact]
    public void Putty_reg_is_parsed_and_only_ssh_is_kept()
    {
        const string reg = "﻿Windows Registry Editor Version 5.00\r\n\r\n" +
            "[HKEY_CURRENT_USER\\Software\\SimonTatham\\PuTTY\\Sessions\\Default%20Settings]\r\n\"HostName\"=\"\"\r\n\r\n" +
            "[HKEY_CURRENT_USER\\Software\\SimonTatham\\PuTTY\\Sessions\\My%20Server]\r\n\"HostName\"=\"root@192.168.1.5\"\r\n\"Protocol\"=\"ssh\"\r\n\"PortNumber\"=dword:000008ae\r\n\r\n" +
            "[HKEY_CURRENT_USER\\Software\\SimonTatham\\PuTTY\\Sessions\\Switch]\r\n\"HostName\"=\"10.0.0.1\"\r\n\"Protocol\"=\"telnet\"\r\n\"PortNumber\"=dword:00000017\r\n\r\n" +
            "[HKEY_CURRENT_USER\\Software\\SimonTatham\\PuTTY\\Sessions\\Web]\r\n\"HostName\"=\"web.example.ch\"\r\n\"Protocol\"=\"ssh\"\r\n\"UserName\"=\"deploy\"\r\n";

        var r = PuttyImporter.ParseReg(reg);

        Assert.Equal(new[] { "My Server", "Web" }, r.Servers.Select(s => s.Name));
        Assert.Equal("192.168.1.5", r.Servers[0].Host);
        Assert.Equal("root", r.Servers[0].Username);       // from "user@host"
        Assert.Equal(2222, r.Servers[0].Port);              // 0x8ae
        Assert.Equal(22, r.Servers[1].Port);                // without PortNumber
        Assert.Equal(2, r.Skipped);                          // Default Settings + telnet
    }

    [Fact]
    public void Putty_rejects_other_files()
    {
        Assert.Throws<ArgumentException>(() => PuttyImporter.ParseReg("Windows Registry Editor Version 5.00\n[HKEY_CURRENT_USER\\Foo]"));
    }

    [Fact]
    public void Csv_comma_with_quotes_and_aliases()
    {
        const string csv = "Label,Hostname/IP,Port,Protocol,Username,Groups,Tags,Notes\n" +
            "\"web, prod\",10.0.0.5,2222,ssh,deploy,Production,\"nginx,linux\",\"Line 1\nLine 2\"\n" +
            "win,10.0.0.9,,rdp,Administrator,Production,,\n" +
            "router,10.0.0.1,23,telnet,admin,,,\n" +
            ",,,,,,,\n";

        var r = CsvImporter.Parse(csv);

        Assert.Equal(2, r.Servers.Count);
        Assert.Equal("web, prod", r.Servers[0].Name);
        Assert.Equal(2222, r.Servers[0].Port);
        Assert.Equal(new[] { "nginx", "linux" }, r.Servers[0].Tags);
        Assert.Equal("Line 1\nLine 2", r.Servers[0].Notes);
        Assert.Equal(3389, r.Servers[1].Port);               // Standard-Port RDP
        Assert.Equal(1, r.Skipped);                           // telnet
    }

    [Fact]
    public void Csv_semicolon_and_missing_host_column()
    {
        var r = CsvImporter.Parse("name;host;user\nA;1.2.3.4;root\n");
        Assert.Equal("root", r.Servers.Single().Username);
        Assert.Equal(22, r.Servers.Single().Port);

        Assert.Throws<ArgumentException>(() => CsvImporter.Parse("name;user\nA;root\n"));
    }
}

public class PasswordChangeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "msv-pw-" + Guid.NewGuid().ToString("N"));
    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void ChangePassword_requires_old_password_and_works_after_relock()
    {
        var path = Path.Combine(_dir, "v.msv");
        var store = new VaultStore(path);
        store.Create("old-password");
        store.Upsert(new ServerEntry { Name = "a", Host = "h", Password = "pw" });

        Assert.Throws<WrongPasswordException>(() => store.ChangePassword("wrong-wrong", "new-password"));
        Assert.Throws<ArgumentException>(() => store.ChangePassword("old-password", "short"));
        store.ChangePassword("old-password", "new-password");
        store.Lock();

        Assert.Throws<WrongPasswordException>(() => store.Unlock("old-password"));
        store.Unlock("new-password");
        Assert.Equal("pw", store.List().Single().Password);
    }

    [Fact]
    public void ExportTo_creates_a_loadable_backup()
    {
        var store = new VaultStore(Path.Combine(_dir, "v.msv"));
        store.Create("master-password");
        store.Upsert(new ServerEntry { Name = "a", Host = "h" });
        var backup = Path.Combine(_dir, "backup.msv");

        store.ExportTo(backup);

        var restored = new VaultStore(backup);
        restored.Unlock("master-password");
        Assert.Single(restored.List());
    }

    [Fact]
    public async Task Password_change_on_one_device_is_adopted_by_the_other()
    {
        var cloud = new FakeSupabase();
        using var a = new Device(cloud);
        using var b = new Device(cloud);

        a.Store.Create("password-one");
        a.Store.Upsert(new ServerEntry { Name = "from-a", Host = "a" });
        a.Sync.Save(Device.Config(cloud));
        await a.Sync.SyncAsync();
        await b.Sync.DownloadInitialAsync(Device.Config(cloud));
        b.Store.Unlock("password-one");
        b.Store.Upsert(new ServerEntry { Name = "only-on-b", Host = "b" });

        // Device A changes the password and overwrites the remote
        a.Store.ChangePassword("password-one", "password-two");
        await a.Sync.ForceUploadAsync();

        // Device B can no longer synchronize and gets a hint
        var ex = await Assert.ThrowsAsync<SyncException>(() => b.Sync.SyncAsync());
        Assert.Contains("Adopt new master password", ex.Message);

        await Assert.ThrowsAsync<WrongPasswordException>(() => b.Sync.AdoptCloudPasswordAsync("wrong-wrong"));
        await b.Sync.AdoptCloudPasswordAsync("password-two");

        Assert.Equal(new[] { "from-a", "only-on-b" }, b.Store.List().Select(s => s.Name).Order()); // local change is kept
        await b.Sync.SyncAsync();
        b.Store.Lock();
        b.Store.Unlock("password-two");
        await a.Sync.SyncAsync();
        Assert.Equal(2, a.Store.List().Count);
    }
}

public class SettingsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "msv-set-" + Guid.NewGuid().ToString("N") + ".json");
    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
        SshConnector.ConnectTimeout = TimeSpan.FromSeconds(15);
        SshConnector.KeepAlive = TimeSpan.FromSeconds(30);
    }

    [Fact]
    public void Settings_are_clamped_persisted_and_applied()
    {
        var store = new AppSettingsStore(_path);
        store.Save(new AppSettings { SshKeepAliveSeconds = 9999, SshConnectTimeoutSeconds = 1, RdpColorDepth = 7, RdpDrives = true });

        var again = new AppSettingsStore(_path).Current;
        Assert.Equal(600, again.SshKeepAliveSeconds);
        Assert.Equal(3, again.SshConnectTimeoutSeconds);
        Assert.Equal(32, again.RdpColorDepth);
        Assert.True(again.RdpDrives);
        Assert.Equal(TimeSpan.FromSeconds(3), SshConnector.ConnectTimeout);
    }

    [Fact]
    public void Keepalive_zero_means_off()
    {
        new AppSettingsStore(_path).Save(new AppSettings { SshKeepAliveSeconds = 0 });
        Assert.Equal(Timeout.InfiniteTimeSpan, SshConnector.KeepAlive);
    }
}

public class JumpTunnelTests
{
    private static ServerEntry S(string id, string jump = "") => new() { Id = id, Name = id, Host = id + ".example", Port = 22, JumpHostId = jump };

    [Fact]
    public async Task No_jump_host_means_no_tunnel()
    {
        Assert.Null(await JumpTunnel.OpenAsync(S("t"), _ => null, (_, _, _) => HostKeyTrust.Trusted));
    }

    [Fact]
    public async Task Loop_and_missing_hops_are_rejected_without_network()
    {
        var a = S("a", "b");
        var b = S("b", "a");
        JumpResolver loop = id => id == "a" ? (a, null) : id == "b" ? (b, null) : null;
        var ex = await Assert.ThrowsAsync<SshConnectException>(() => JumpTunnel.OpenAsync(S("t", "a"), loop, (_, _, _) => HostKeyTrust.Trusted));
        Assert.Contains("loop", ex.Message);

        var missing = await Assert.ThrowsAsync<SshConnectException>(() => JumpTunnel.OpenAsync(S("t", "does-not-exist"), _ => null, (_, _, _) => HostKeyTrust.Trusted));
        Assert.Contains("no longer exists", missing.Message);
    }

    [Fact]
    public async Task Chain_longer_than_five_hops_is_rejected()
    {
        var all = Enumerable.Range(1, 7).ToDictionary(i => $"h{i}", i => S($"h{i}", i < 7 ? $"h{i + 1}" : ""));
        JumpResolver resolver = id => all.TryGetValue(id, out var s) ? (s, null) : null;
        await Assert.ThrowsAsync<SshConnectException>(() => JumpTunnel.OpenAsync(S("t", "h1"), resolver, (_, _, _) => HostKeyTrust.Trusted));
    }
}

/// <summary>Jump hosts against the real local sshd (the same server serves as jump host and as target).</summary>
public class JumpIntegrationTests
{
    private static string? Dir => Environment.GetEnvironmentVariable("MSV_SSH_TEST_DIR");
    private static ServerEntry Sshd(string id, string password = "testpw-123", string jump = "") => new()
    {
        Id = id, Name = id, Host = "127.0.0.1", Port = 2222, Username = "msvtest", Password = password, JumpHostId = jump
    };
    private static Func<string, int, string, HostKeyTrust> Accept() => (_, _, _) => HostKeyTrust.NewlyTrusted;

    [Fact]
    public async Task Terminal_and_sftp_work_through_a_jump_host_chain()
    {
        if (Dir == null) return;
        // target -> jump2 -> jump1 -> (target host): two hops in a row
        var j1 = Sshd("j1");
        var j2 = Sshd("j2", jump: "j1");
        var target = Sshd("target", jump: "j2");
        JumpResolver resolver = id => id == "j1" ? (j1, null) : id == "j2" ? (j2, null) : null;

        var output = new StringBuilder();
        var seen = new TaskCompletionSource();
        using var term = new SshTerminalSession(target, null, Accept(), resolver);
        term.Data += d => { lock (output) { output.Append(Encoding.UTF8.GetString(d)); if (output.ToString().Contains("THROUGH:")) seen.TrySetResult(); } };
        await term.ConnectAsync(100, 30);
        term.Write(Encoding.UTF8.GetBytes("echo THROUGH:$((20+22))\n"));
        await seen.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(200);
        Assert.Contains("THROUGH:42", output.ToString());

        using var sftp = new SftpSession(target, null, Accept(), resolver);
        var home = await sftp.ConnectAsync();
        Assert.StartsWith("/", home);
        Assert.NotEmpty((await sftp.ListAsync(home)).Path);
    }

    [Fact]
    public async Task Wrong_jump_credentials_name_the_jump_host()
    {
        if (Dir == null) return;
        var jump = Sshd("jumpy", password: "wrong");
        var target = Sshd("target", jump: "jumpy");
        using var term = new SshTerminalSession(target, null, Accept(), id => id == "jumpy" ? (jump, null) : null);

        var ex = await Assert.ThrowsAsync<SshConnectException>(() => term.ConnectAsync(80, 24));
        Assert.Contains("Jump host \"jumpy\"", ex.Message);
        Assert.Contains("Authentication failed", ex.Message);
    }

    [Fact]
    public async Task Port_forward_works_through_a_jump_host()
    {
        if (Dir == null) return;
        var jump = Sshd("jumpy");
        var target = Sshd("target", jump: "jumpy");
        var free = new TcpListener(IPAddress.Loopback, 0);
        free.Start();
        var port = ((IPEndPoint)free.LocalEndpoint).Port;
        free.Stop();

        using var mgr = new PortForwardManager();
        await mgr.StartAsync(new PortForwardEntry { Id = "fj", Type = ForwardType.Local, BindPort = port, DestHost = "127.0.0.1", DestPort = 2222 },
            target, null, Accept(), id => id == "jumpy" ? (jump, null) : null);

        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", port);
        var buf = new byte[16];
        var n = await tcp.GetStream().ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.StartsWith("SSH-2.0", Encoding.ASCII.GetString(buf, 0, n));
    }
}
