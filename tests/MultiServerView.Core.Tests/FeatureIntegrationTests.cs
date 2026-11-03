using System.Net;
using System.Net.Sockets;
using System.Text;
using MultiServerView.Core;
using MultiServerView.Core.Sftp;
using MultiServerView.Core.Ssh;
using Xunit;

namespace MultiServerView.Core.Tests;

/// <summary>SFTP, Port-Weiterleitungen und OS-Erkennung gegen einen echten lokalen sshd (siehe SshIntegrationTests).</summary>
public class FeatureIntegrationTests : IDisposable
{
    private static string? Dir => Environment.GetEnvironmentVariable("MSV_SSH_TEST_DIR");
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "msv-int-" + Guid.NewGuid().ToString("N"));
    private readonly string _remoteRoot = "/tmp/msv-sftp-" + Guid.NewGuid().ToString("N");

    public FeatureIntegrationTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        if (Directory.Exists(_tmp)) Directory.Delete(_tmp, true);
    }

    private static ServerEntry Server() => new()
    {
        Name = "local", Host = "127.0.0.1", Port = 2222, Username = "msvtest", Password = "testpw-123"
    };

    private static Func<string, int, string, HostKeyTrust> Accept() => (_, _, _) => HostKeyTrust.NewlyTrusted;

    [Fact]
    public async Task Sftp_browse_upload_download_rename_delete()
    {
        if (Dir == null) return;
        using var sftp = new SftpSession(Server(), null, Accept());
        var home = await sftp.ConnectAsync();
        Assert.StartsWith("/", home);

        // Ordnerbaum lokal vorbereiten
        var src = Path.Combine(_tmp, "projekt");
        Directory.CreateDirectory(Path.Combine(src, "unter"));
        File.WriteAllText(Path.Combine(src, "a.txt"), "hallo welt");
        File.WriteAllBytes(Path.Combine(src, "unter", "gross.bin"), new byte[300_000]);

        await sftp.MakeDirAsync(_remoteRoot);
        var last = new TransferProgress("", 0, 0, false);
        await sftp.UploadAsync(src, _remoteRoot, p => last = p, CancellationToken.None);
        Assert.True(last.Finished);
        Assert.Equal(10 + 300_000, last.Total);

        var listing = await sftp.ListAsync(_remoteRoot + "/projekt");
        Assert.Equal(new[] { "unter", "a.txt" }, listing.Entries.Select(e => e.Name));
        Assert.StartsWith("d", listing.Entries[0].Mode);
        Assert.Equal(_remoteRoot, listing.Parent);

        // Download und Vergleich
        var dest = Path.Combine(_tmp, "zurueck");
        Directory.CreateDirectory(dest);
        await sftp.DownloadAsync(_remoteRoot + "/projekt", dest, _ => { }, CancellationToken.None);
        Assert.Equal("hallo welt", File.ReadAllText(Path.Combine(dest, "projekt", "a.txt")));
        Assert.Equal(300_000, new FileInfo(Path.Combine(dest, "projekt", "unter", "gross.bin")).Length);

        // Umbenennen, dann alles rekursiv löschen
        await sftp.RenameAsync(_remoteRoot + "/projekt/a.txt", _remoteRoot + "/projekt/b.txt");
        Assert.Contains((await sftp.ListAsync(_remoteRoot + "/projekt")).Entries, e => e.Name == "b.txt");
        await sftp.DeleteAsync(_remoteRoot);
        await Assert.ThrowsAnyAsync<Exception>(() => sftp.ListAsync(_remoteRoot));
    }

    [Fact]
    public async Task Sftp_upload_can_be_cancelled()
    {
        if (Dir == null) return;
        using var sftp = new SftpSession(Server(), null, Accept());
        await sftp.ConnectAsync();
        var big = Path.Combine(_tmp, "big.bin");
        File.WriteAllBytes(big, new byte[20_000_000]);
        using var cts = new CancellationTokenSource();
        await sftp.MakeDirAsync(_remoteRoot);

        var task = sftp.UploadAsync(big, _remoteRoot, p => { if (p.Done > 0) cts.Cancel(); }, cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        await sftp.DeleteAsync(_remoteRoot);
    }

    [Fact]
    public async Task Local_forward_reaches_the_target_through_ssh()
    {
        if (Dir == null) return;
        var port = FreePort();
        var rule = new PortForwardEntry { Id = "f1", Type = ForwardType.Local, BindPort = port, DestHost = "127.0.0.1", DestPort = 2222 };
        using var mgr = new PortForwardManager();
        await mgr.StartAsync(rule, Server(), null, Accept());
        Assert.True(mgr.IsRunning("f1"));

        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", port);
        var buf = new byte[32];
        var n = await tcp.GetStream().ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.StartsWith("SSH-2.0", Encoding.ASCII.GetString(buf, 0, n)); // Banner des sshd, durch den Tunnel

        mgr.Stop("f1");
        Assert.False(mgr.IsRunning("f1"));
    }

    [Fact]
    public async Task Dynamic_forward_speaks_socks5()
    {
        if (Dir == null) return;
        var port = FreePort();
        var rule = new PortForwardEntry { Id = "f2", Type = ForwardType.Dynamic, BindPort = port };
        using var mgr = new PortForwardManager();
        await mgr.StartAsync(rule, Server(), null, Accept());

        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", port);
        var s = tcp.GetStream();
        await s.WriteAsync(new byte[] { 0x05, 0x01, 0x00 });
        var reply = new byte[2];
        await s.ReadExactlyAsync(reply).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new byte[] { 0x05, 0x00 }, reply);
    }

    [Fact]
    public async Task Remote_forward_brings_a_local_service_to_the_server()
    {
        if (Dir == null) return;
        var local = new TcpListener(IPAddress.Loopback, 0);
        local.Start();
        var localPort = ((IPEndPoint)local.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            using var c = await local.AcceptTcpClientAsync();
            await c.GetStream().WriteAsync("HALLO-VOM-PC"u8.ToArray());
            await Task.Delay(500);
        });

        var remotePort = FreePort();
        var rule = new PortForwardEntry { Id = "f3", Type = ForwardType.Remote, BindPort = remotePort, DestHost = "127.0.0.1", DestPort = localPort };
        using var mgr = new PortForwardManager();
        await mgr.StartAsync(rule, Server(), null, Accept());

        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", remotePort); // sshd läuft auf derselben Maschine
        var buf = new byte[32];
        var n = await tcp.GetStream().ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("HALLO-VOM-PC", Encoding.ASCII.GetString(buf, 0, n));
        local.Stop();
    }

    [Fact]
    public async Task Forward_with_busy_port_gives_clear_error()
    {
        if (Dir == null) return;
        var busy = new TcpListener(IPAddress.Loopback, 0);
        busy.Start();
        var rule = new PortForwardEntry { Id = "f4", Type = ForwardType.Local, BindPort = ((IPEndPoint)busy.LocalEndpoint).Port, DestHost = "127.0.0.1", DestPort = 2222 };
        using var mgr = new PortForwardManager();

        var ex = await Assert.ThrowsAsync<SshConnectException>(() => mgr.StartAsync(rule, Server(), null, Accept()));
        Assert.Contains("konnte nicht geöffnet werden", ex.Message);
        busy.Stop();
    }

    [Fact]
    public async Task Os_is_detected_after_login()
    {
        if (Dir == null) return;
        using var s = new SshTerminalSession(Server(), null, Accept());
        await s.ConnectAsync(80, 24);
        var os = await s.DetectOsAsync();
        Assert.NotEqual("", os);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }
}
