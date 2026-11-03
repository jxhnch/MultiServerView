using System.Net;
using System.Net.Sockets;
using System.Text;
using MultiServerView.Core;
using MultiServerView.Core.Sftp;
using MultiServerView.Core.Ssh;
using Xunit;

namespace MultiServerView.Core.Tests;

/// <summary>SFTP, port forwards and OS detection against a real local sshd (see SshIntegrationTests).</summary>
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

        // Prepare a folder tree locally
        var src = Path.Combine(_tmp, "project");
        Directory.CreateDirectory(Path.Combine(src, "under"));
        File.WriteAllText(Path.Combine(src, "a.txt"), "hello world");
        File.WriteAllBytes(Path.Combine(src, "under", "large.bin"), new byte[300_000]);

        await sftp.MakeDirAsync(_remoteRoot);
        var last = new TransferProgress("", 0, 0, false);
        await sftp.UploadAsync(src, _remoteRoot, p => last = p, CancellationToken.None);
        Assert.True(last.Finished);
        Assert.Equal(10 + 300_000, last.Total);

        var listing = await sftp.ListAsync(_remoteRoot + "/project");
        Assert.Equal(new[] { "under", "a.txt" }, listing.Entries.Select(e => e.Name));
        Assert.StartsWith("d", listing.Entries[0].Mode);
        Assert.Equal(_remoteRoot, listing.Parent);

        // Download and comparison
        var dest = Path.Combine(_tmp, "zurueck");
        Directory.CreateDirectory(dest);
        await sftp.DownloadAsync(_remoteRoot + "/project", dest, _ => { }, CancellationToken.None);
        Assert.Equal("hello world", File.ReadAllText(Path.Combine(dest, "project", "a.txt")));
        Assert.Equal(300_000, new FileInfo(Path.Combine(dest, "project", "under", "large.bin")).Length);

        // Rename, then delete everything recursively
        await sftp.RenameAsync(_remoteRoot + "/project/a.txt", _remoteRoot + "/project/b.txt");
        Assert.Contains((await sftp.ListAsync(_remoteRoot + "/project")).Entries, e => e.Name == "b.txt");
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
        Assert.StartsWith("SSH-2.0", Encoding.ASCII.GetString(buf, 0, n)); // banner of the sshd, through the tunnel

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
            await c.GetStream().WriteAsync("HELLO-FROM-PC"u8.ToArray());
            await Task.Delay(500);
        });

        var remotePort = FreePort();
        var rule = new PortForwardEntry { Id = "f3", Type = ForwardType.Remote, BindPort = remotePort, DestHost = "127.0.0.1", DestPort = localPort };
        using var mgr = new PortForwardManager();
        await mgr.StartAsync(rule, Server(), null, Accept());

        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", remotePort); // sshd runs on the same machine
        var buf = new byte[32];
        var n = await tcp.GetStream().ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("HELLO-FROM-PC", Encoding.ASCII.GetString(buf, 0, n));
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
        Assert.Contains("could not be opened", ex.Message);
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

    [Fact]
    public async Task Sftp_edit_text_keeps_line_endings_bom_and_detects_foreign_changes()
    {
        if (Dir == null) return;
        using var sftp = new SftpSession(Server(), null, Accept());
        await sftp.ConnectAsync();
        await sftp.MakeDirAsync(_remoteRoot);

        // Create a file with Windows line endings, BOM and umlauts.
        var local = Path.Combine(_tmp, "conf.txt");
        File.WriteAllBytes(local, new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("line one\r\nämter\r\n")).ToArray());
        await sftp.UploadAsync(local, _remoteRoot, _ => { }, CancellationToken.None);
        var path = _remoteRoot + "/conf.txt";

        var file = await sftp.ReadTextAsync(path);
        Assert.Equal("line one\nämter\n", file.Content);
        Assert.Equal("crlf", file.LineEnding);
        Assert.True(file.Bom);

        // Edit and write back: line endings and BOM stay as in the original.
        var saved = await sftp.WriteTextAsync(path, "line one\nneu: 日本\n", file.LineEnding, file.Bom, file.Modified, file.Size, force: false);
        var back = Path.Combine(_tmp, "back");
        Directory.CreateDirectory(back);
        await sftp.DownloadAsync(path, back, _ => { }, CancellationToken.None);
        var bytes = File.ReadAllBytes(Path.Combine(back, "conf.txt"));
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
        Assert.Equal("line one\r\nneu: 日本\r\n", Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
        Assert.True(saved.Modified >= file.Modified);

        // Someone else changes the file: a normal save is rejected, with force it works.
        var other = await sftp.ReadTextAsync(path);
        await Task.Delay(1100); // SFTP timestamps have a resolution of one second
        await sftp.WriteTextAsync(path, "from someone else\n", other.LineEnding, other.Bom, other.Modified, other.Size, force: true);
        await Assert.ThrowsAsync<RemoteChangedException>(() =>
            sftp.WriteTextAsync(path, "my change\n", other.LineEnding, other.Bom, other.Modified, other.Size, force: false));
        await sftp.WriteTextAsync(path, "my change\n", other.LineEnding, other.Bom, other.Modified, other.Size, force: true);
        Assert.Equal("my change\n", (await sftp.ReadTextAsync(path)).Content);

        await sftp.DeleteAsync(_remoteRoot);
    }

    [Fact]
    public async Task Sftp_edit_text_rejects_binary_non_utf8_folders_and_huge_files()
    {
        if (Dir == null) return;
        using var sftp = new SftpSession(Server(), null, Accept());
        await sftp.ConnectAsync();
        await sftp.MakeDirAsync(_remoteRoot);

        File.WriteAllBytes(Path.Combine(_tmp, "bin.dat"), new byte[] { 1, 2, 0, 3 });
        File.WriteAllBytes(Path.Combine(_tmp, "latin1.txt"), new byte[] { 0x68, 0xE4, 0x6C, 0x6C, 0x6F }); // "hällo" in Latin-1 = invalid UTF-8
        File.WriteAllBytes(Path.Combine(_tmp, "gross.txt"), Enumerable.Repeat((byte)'a', (int)SftpSession.MaxEditBytes + 1).ToArray());
        foreach (var name in new[] { "bin.dat", "latin1.txt", "gross.txt" })
            await sftp.UploadAsync(Path.Combine(_tmp, name), _remoteRoot, _ => { }, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sftp.ReadTextAsync(_remoteRoot + "/bin.dat"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sftp.ReadTextAsync(_remoteRoot + "/latin1.txt"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sftp.ReadTextAsync(_remoteRoot + "/gross.txt"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sftp.ReadTextAsync(_remoteRoot));

        await sftp.DeleteAsync(_remoteRoot);
    }
}
