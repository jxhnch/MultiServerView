using MultiServerView.Core;
using MultiServerView.Core.Sftp;
using MultiServerView.Core.Ssh;
using Xunit;

namespace MultiServerView.Core.Tests;

public class FeatureStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "msv-feat-" + Guid.NewGuid().ToString("N"));
    private VaultStore NewStore()
    {
        var s = new VaultStore(Path.Combine(_dir, "vault.msv"));
        s.Create("master-passwort");
        return s;
    }
    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void Identity_overrides_credentials_and_deleting_it_detaches_hosts()
    {
        var store = NewStore();
        var id = store.UpsertIdentity(new IdentityEntry { Name = "admin", Username = "root", Password = "geheim" });
        var server = store.Upsert(new ServerEntry { Name = "a", Host = "h", Username = "alt", Password = "alt", IdentityId = id.Id });

        var resolved = store.FindResolved(server.Id)!;
        Assert.Equal("root", resolved.Username);
        Assert.Equal("geheim", resolved.Password);
        Assert.Equal("alt", store.Find(server.Id)!.Username); // Original bleibt unverändert

        store.DeleteIdentity(id.Id);
        Assert.Equal("", store.Find(server.Id)!.IdentityId);
        Assert.Equal("alt", store.FindResolved(server.Id)!.Username);
    }

    [Fact]
    public void Snippets_and_forwards_roundtrip_through_lock_and_unlock()
    {
        var store = NewStore();
        var server = store.Upsert(new ServerEntry { Name = "a", Host = "h" });
        store.UpsertSnippet(new SnippetEntry { Name = "Updates", Command = "apt update && apt upgrade -y" });
        store.UpsertForward(new PortForwardEntry { Name = "DB", ServerId = server.Id, BindPort = 5433, DestHost = "127.0.0.1", DestPort = 5432 });
        store.Lock();
        store.Unlock("master-passwort");

        Assert.Equal("apt update && apt upgrade -y", store.ListSnippets().Single().Command);
        Assert.Equal(5432, store.ListForwards().Single().DestPort);
    }

    [Fact]
    public void Deleting_a_server_removes_its_forwards()
    {
        var store = NewStore();
        var server = store.Upsert(new ServerEntry { Name = "a", Host = "h" });
        var fw = store.UpsertForward(new PortForwardEntry { ServerId = server.Id, BindPort = 1000, DestHost = "x", DestPort = 1 });

        store.Delete(server.Id);

        Assert.Empty(store.ListForwards());
        Assert.True(store.Snapshot().Tombstones.ContainsKey(fw.Id));
    }

    [Fact]
    public void KnownHosts_can_be_listed_and_removed()
    {
        var store = NewStore();
        store.CheckHostKey("b.example", 22, "SHA256:2");
        store.CheckHostKey("a.example", 22, "SHA256:1");

        Assert.Equal(new[] { "a.example:22", "b.example:22" }, store.ListKnownHosts().Select(k => k.Key));
        store.RemoveKnownHost("a.example:22");
        Assert.Single(store.ListKnownHosts());
    }

    [Fact]
    public void SetOs_is_stored_and_raises_Changed_only_on_change()
    {
        var store = NewStore();
        var s = store.Upsert(new ServerEntry { Name = "a", Host = "h" });
        var count = 0;
        store.Changed += () => count++;

        store.SetOs(s.Id, "debian");
        store.SetOs(s.Id, "debian");

        Assert.Equal("debian", store.Find(s.Id)!.Os);
        Assert.Equal(1, count);
    }

    [Fact]
    public void New_item_types_survive_a_cloud_merge()
    {
        var store = NewStore();
        store.UpsertSnippet(new SnippetEntry { Name = "lokal", Command = "ls" });
        var local = store.Snapshot();
        var remote = new VaultData { Snippets = { new SnippetEntry { Id = "r1", Name = "cloud", Command = "pwd", UpdatedAt = DateTimeOffset.UtcNow } } };

        var merged = VaultMerge.Merge(local, remote);

        Assert.Equal(2, merged.Snippets.Count);
    }
}

public class HistoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "msv-hist-" + Guid.NewGuid().ToString("N") + ".json");
    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void Records_start_and_end_and_lists_newest_first()
    {
        var h = new HistoryStore(_path);
        var s1 = new ServerEntry { Id = "1", Name = "eins", Host = "h1", Port = 22 };
        var s2 = new ServerEntry { Id = "2", Name = "zwei", Host = "h2", Port = 22 };
        var a = h.Begin(s1, "ssh");
        h.End(a, true);
        var b = h.Begin(s2, "sftp");
        h.End(b, false, "Zeitüberschreitung");

        var list = h.List();
        Assert.Equal(new[] { "zwei", "eins" }, list.Select(x => x.ServerName));
        Assert.Equal("error", list[0].Result);
        Assert.Equal("ok", list[1].Result);

        // bleibt nach Neustart erhalten, enthält keine Passwörter
        Assert.Equal(2, new HistoryStore(_path).List().Count);
        Assert.DoesNotContain("Passwort", File.ReadAllText(_path));
    }
}

public class OsDetectorTests
{
    [Theory]
    [InlineData("PRETTY_NAME=\"Debian GNU/Linux 13\"\nID=debian\n---\nLinux", "debian")]
    [InlineData("NAME=\"Ubuntu\"\nID=ubuntu\nID_LIKE=debian\n---\nLinux", "ubuntu")]
    [InlineData("ID=\"rhel\"\nID_LIKE=\"fedora\"\n---\nLinux", "redhat")]
    [InlineData("ID=linuxmint\nID_LIKE=\"ubuntu debian\"\n---\nLinux", "mint")]
    [InlineData("ID=somethingnew\nID_LIKE=\"arch\"\n---\nLinux", "arch")]
    [InlineData("ID=unknowndistro\n---\nLinux", "linux")]
    [InlineData("---\nDarwin", "macos")]
    [InlineData("---\nFreeBSD", "freebsd")]
    [InlineData("", "")]
    public void Parses_os_release(string output, string expected) => Assert.Equal(expected, OsDetector.Parse(output));
}

public class LocalFsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "msv-fs-" + Guid.NewGuid().ToString("N"));
    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void Browse_lists_folders_first_and_gives_parent()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "b-ordner"));
        File.WriteAllText(Path.Combine(_dir, "a-datei.txt"), "hallo");

        var r = LocalFs.Browse(_dir);

        Assert.Equal(new[] { "b-ordner", "a-datei.txt" }, r.Entries.Select(e => e.Name));
        Assert.True(r.Entries[0].IsDir);
        Assert.Equal(5, r.Entries[1].Size);
        Assert.Equal(Directory.GetParent(_dir)!.FullName, r.Parent);
    }

    [Fact]
    public void Browse_throws_for_missing_folder()
    {
        Assert.Throws<DirectoryNotFoundException>(() => LocalFs.Browse(Path.Combine(_dir, "gibts-nicht")));
    }

    [Theory]
    [InlineData("/home/user", ".", "/home/user")]
    [InlineData("/home/user", "docs", "/home/user/docs")]
    [InlineData("/home/user", "../x", "/home/x")]
    [InlineData("/home/user", "/etc/./ssh/..", "/etc")]
    [InlineData("/", "..", "/")]
    public void Sftp_path_normalization(string cwd, string input, string expected) =>
        Assert.Equal(expected, SftpSession.Normalize(cwd, input));
}
