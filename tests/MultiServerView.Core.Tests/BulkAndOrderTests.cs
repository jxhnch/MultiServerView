using MultiServerView.Core;
using Xunit;

namespace MultiServerView.Core.Tests;

public class BulkAndOrderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "msv-bulk-" + Guid.NewGuid().ToString("N"));
    private readonly VaultStore _vault;

    public BulkAndOrderTests()
    {
        _vault = new VaultStore(Path.Combine(_dir, "vault.msv"));
        _vault.Create("master-password");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private ServerEntry Add(string name, Protocol protocol = Protocol.Ssh) => _vault.Upsert(new ServerEntry { Name = name, Host = name + ".lan", Protocol = protocol, Username = "u-" + name });

    private ServerEntry Get(ServerEntry s) => _vault.List().Single(x => x.Id == s.Id);

    [Fact]
    public void Order_is_saved_and_can_be_reset()
    {
        var a = Add("a");
        var b = Add("b");
        Assert.Equal(2, _vault.Reorder(new[] { b.Id, a.Id }));
        Assert.Equal(1, Get(b).SortOrder);
        Assert.Equal(2, Get(a).SortOrder);
        Assert.Equal(0, _vault.Reorder(new[] { b.Id, a.Id }));
        _vault.Reorder(new[] { a.Id, b.Id }, reset: true);
        Assert.All(_vault.List(), s => Assert.Equal(0, s.SortOrder));
    }

    [Fact]
    public void A_key_is_only_given_to_ssh_hosts()
    {
        var key = _vault.UpsertKey(new KeyEntry { Name = "k", PrivateKey = "x" });
        var ssh = Add("ssh");
        var rdp = Add("rdp", Protocol.Rdp);
        var (changed, skipped) = _vault.ApplyLogin(new[] { ssh.Id, rdp.Id }, new BulkLogin(BulkLogin.ByKey, KeyId: key.Id, Username: "root"));
        Assert.Equal((1, 1), (changed, skipped));
        Assert.Equal((AuthMethod.Key, key.Id, "root"), (Get(ssh).Auth, Get(ssh).KeyId, Get(ssh).Username));
        Assert.Equal("u-rdp", Get(rdp).Username);
    }

    [Fact]
    public void Password_keeps_empty_fields_and_removes_the_identity()
    {
        var identity = _vault.UpsertIdentity(new IdentityEntry { Name = "i", Username = "admin", Password = "old" });
        var s = Add("a");
        _vault.ApplyLogin(new[] { s.Id }, new BulkLogin(BulkLogin.ByIdentity, IdentityId: identity.Id));
        Assert.Equal(identity.Id, Get(s).IdentityId);
        _vault.ApplyLogin(new[] { s.Id }, new BulkLogin(BulkLogin.ByPassword, Password: "new"));
        Assert.Equal(("", "admin", "new", AuthMethod.Password), (Get(s).IdentityId, Get(s).Username, Get(s).Password, Get(s).Auth));
    }

    [Fact]
    public void Missing_key_or_empty_login_is_refused()
    {
        var s = Add("a");
        Assert.Throws<KeyNotFoundException>(() => _vault.ApplyLogin(new[] { s.Id }, new BulkLogin(BulkLogin.ByKey, KeyId: "nope")));
        Assert.Throws<ArgumentException>(() => _vault.ApplyLogin(new[] { s.Id }, new BulkLogin(BulkLogin.ByPassword)));
        Assert.Throws<ArgumentException>(() => _vault.ApplyLogin(new[] { s.Id }, new BulkLogin("other")));
        Assert.Equal("u-a", Get(s).Username);
    }
}
