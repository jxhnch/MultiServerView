using MultiServerView.Core;
using MultiServerView.Core.Sftp;
using Xunit;

namespace MultiServerView.Core.Tests;

/// <summary>Server-to-server copy against a real local sshd (see SshIntegrationTests). Both sides use that one server.</summary>
public class SftpServerCopyIntegrationTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("MSV_SSH_TEST_DIR") != null;

    private static async Task<SftpSession> Connect()
    {
        var session = new SftpSession(new ServerEntry { Name = "local", Host = "127.0.0.1", Port = 2222, Username = "msvtest", Password = Environment.GetEnvironmentVariable("MSV_SSH_TEST_PASSWORD") ?? "testpw-123" }, null,
            (_, _, _) => HostKeyTrust.NewlyTrusted);
        await session.ConnectAsync();
        return session;
    }

    [Fact]
    public async Task Folders_are_copied_with_content_and_existing_files_are_replaced()
    {
        if (!Enabled) return;
        using var from = await Connect();
        using var to = await Connect();
        var root = "/tmp/msv-copy-" + Guid.NewGuid().ToString("N");
        foreach (var dir in new[] { "", "/a", "/a/sub", "/b" }) await from.MakeDirAsync(root + dir);
        var big = new string('x', 700_000);
        await from.WriteTextAsync(root + "/a/big.txt", big, "lf", false, 0, 0, force: true);
        await from.WriteTextAsync(root + "/a/sub/run.sh", "echo hi\n", "lf", false, 0, 0, force: true);
        await from.ChmodAsync(root + "/a/sub/run.sh", 0x1ED, false);

        await from.CopyToAsync(to, root + "/a", root + "/b", _ => { }, CancellationToken.None);
        Assert.Equal(await from.ChecksumAsync(root + "/a/big.txt"), await to.ChecksumAsync(root + "/b/a/big.txt"));
        Assert.Equal(0x1ED, (await to.StatAsync(root + "/b/a/sub/run.sh")).Permissions);

        // a second copy replaces the file and keeps the permissions the target had
        await to.ChmodAsync(root + "/b/a/big.txt", 0x180, false);
        await from.WriteTextAsync(root + "/a/big.txt", "new", "lf", false, 0, 0, force: true);
        await from.CopyToAsync(to, root + "/a/big.txt", root + "/b/a", _ => { }, CancellationToken.None);
        Assert.Equal(3, (await to.StatAsync(root + "/b/a/big.txt")).Size);
        Assert.Equal(0x180, (await to.StatAsync(root + "/b/a/big.txt")).Permissions);
        Assert.DoesNotContain((await to.ListAsync(root + "/b/a")).Entries, e => e.Name.Contains(".msv-"));

        await from.DeleteAsync(root);
    }

    [Fact]
    public async Task Links_are_counted_and_an_interrupted_write_keeps_the_old_file()
    {
        if (!Enabled) return;
        using var from = await Connect();
        using var to = await Connect();
        var root = "/tmp/msv-copy-" + Guid.NewGuid().ToString("N");
        foreach (var dir in new[] { "", "/a", "/b", "/out" }) await from.MakeDirAsync(root + dir);
        await from.WriteTextAsync(root + "/a/real.txt", "real", "lf", false, 0, 0, force: true);
        using (var raw = new Renci.SshNet.SftpClient("127.0.0.1", 2222, "msvtest", Environment.GetEnvironmentVariable("MSV_SSH_TEST_PASSWORD") ?? "testpw-123"))
        {
            raw.Connect();
            raw.SymbolicLink(root + "/a/real.txt", root + "/a/link.txt");
        }

        Assert.Equal(1, await from.CopyToAsync(to, root + "/a", root + "/b", _ => { }, CancellationToken.None));
        var local = Path.Combine(Path.GetTempPath(), "msv-dl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(local);
        Assert.Equal(1, await from.DownloadAsync(root + "/a", local, _ => { }, CancellationToken.None));

        // uploading over a link writes into the file it points to and keeps the link
        var upload = Path.Combine(local, "link.txt");
        await File.WriteAllTextAsync(upload, "through the link");
        await to.UploadAsync(upload, root + "/a", _ => { }, CancellationToken.None);
        Assert.True((await to.ListAsync(root + "/a")).Entries.Single(e => e.Name == "link.txt").IsLink);
        Assert.Equal(16, (await to.StatAsync(root + "/a/real.txt")).Size);

        // a normal upload over an existing file keeps its permissions and leaves no part file behind
        await to.ChmodAsync(root + "/b/a/real.txt", 0x180, false);
        var second = Path.Combine(local, "real.txt");
        await File.WriteAllTextAsync(second, "second version");
        await to.UploadAsync(second, root + "/b/a", _ => { }, CancellationToken.None);
        Assert.Equal(0x180, (await to.StatAsync(root + "/b/a/real.txt")).Permissions);
        Assert.DoesNotContain((await to.ListAsync(root + "/b/a")).Entries, e => e.Name.Contains(".msv-part"));

        Directory.Delete(local, true);
        await from.DeleteAsync(root);
    }

    [Fact]
    public async Task Copying_onto_or_into_itself_is_refused()
    {
        if (!Enabled) return;
        using var from = await Connect();
        using var to = await Connect();
        var root = "/tmp/msv-copy-" + Guid.NewGuid().ToString("N");
        foreach (var dir in new[] { "", "/a", "/a/sub" }) await from.MakeDirAsync(root + dir);
        await from.WriteTextAsync(root + "/a/f.txt", "keep", "lf", false, 0, 0, force: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => from.CopyToAsync(to, root + "/a/f.txt", root + "/a", _ => { }, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => from.CopyToAsync(to, root + "/a", root + "/a/sub", _ => { }, CancellationToken.None));
        Assert.Equal(4, (await from.StatAsync(root + "/a/f.txt")).Size);
        Assert.Equal(2, (await from.ListAsync(root + "/a")).Entries.Count);

        await from.DeleteAsync(root);
    }
}
