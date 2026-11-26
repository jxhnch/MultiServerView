using MultiServerView.Core;
using MultiServerView.Core.Sftp;
using Xunit;

namespace MultiServerView.Core.Tests;

/// <summary>Deleting or renaming a link must never touch what it points to (real local sshd, see SshIntegrationTests).</summary>
public class SftpLinkSafetyIntegrationTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("MSV_SSH_TEST_DIR") != null;
    private static string Password => Environment.GetEnvironmentVariable("MSV_SSH_TEST_PASSWORD") ?? "testpw-123";

    [Fact]
    public async Task Delete_and_rename_act_on_the_link_not_on_its_target()
    {
        if (!Enabled) return;
        using var sftp = new SftpSession(new ServerEntry { Name = "local", Host = "127.0.0.1", Port = 2222, Username = "msvtest", Password = Password }, null,
            (_, _, _) => HostKeyTrust.NewlyTrusted);
        await sftp.ConnectAsync();
        var root = "/tmp/msv-link-" + Guid.NewGuid().ToString("N");
        foreach (var dir in new[] { "", "/keep", "/keep/folder", "/work", "/work/sub" }) await sftp.MakeDirAsync(root + dir);
        await sftp.WriteTextAsync(root + "/keep/file.txt", "precious", "lf", false, 0, 0, force: true);
        await sftp.WriteTextAsync(root + "/keep/folder/inside.txt", "precious", "lf", false, 0, 0, force: true);
        using (var raw = new Renci.SshNet.SftpClient("127.0.0.1", 2222, "msvtest", Password))
        {
            raw.Connect();
            foreach (var link in new[] { "/work/single", "/work/renameme", "/work/sub/to-file" }) raw.SymbolicLink(root + "/keep/file.txt", root + link);
            raw.SymbolicLink(root + "/keep/folder", root + "/work/sub/to-folder");
        }
        async Task<string[]> Names(string dir) => (await sftp.ListAsync(root + dir)).Entries.Select(e => e.Name).Order().ToArray();

        await sftp.RenameAsync(root + "/work/renameme", root + "/work/renamed");
        Assert.Equal(new[] { "file.txt", "folder" }, (await Names("/keep")).Order().ToArray());
        Assert.Contains((await sftp.ListAsync(root + "/work")).Entries, e => e.Name == "renamed" && e.IsLink);

        await sftp.DeleteAsync(root + "/work/single");
        await sftp.DeleteAsync(root + "/work"); // a folder full of links
        Assert.Equal(new[] { "file.txt", "folder" }, (await Names("/keep")).Order().ToArray());
        Assert.Equal(new[] { "inside.txt" }, await Names("/keep/folder"));
        Assert.DoesNotContain("work", await Names(""));

        await sftp.DeleteAsync(root);
    }
}
