using MultiServerView.Core;
using MultiServerView.Core.Import;
using Xunit;

namespace MultiServerView.Core.Tests;

public class ImportCredentialTests
{
    private static string Xml(string protectedAttr, string password) => $"""
        <Connections Name="Connections" Export="false" EncryptionEngine="AES" BlockCipherMode="GCM" KdfIterations="1000" FullFileEncryption="false" Protected="{protectedAttr}" ConfVersion="2.6">
          <Node Name="web" Type="Connection" Hostname="10.0.0.5" Protocol="SSH2" Port="22" Username="deploy" Password="{password}" />
          <Node Name="empty" Type="Connection" Hostname="10.0.0.6" Protocol="SSH2" Username="deploy" Password="" />
        </Connections>
        """;

    [Fact]
    public void Gcm_round_trip_with_default_master_password()
    {
        var enc = MRemoteNgCrypto.EncryptGcm("s3cret!", MRemoteNgCrypto.DefaultMasterPassword, 1000);
        Assert.Equal("s3cret!", MRemoteNgCrypto.TryDecrypt(enc, MRemoteNgCrypto.DefaultMasterPassword, "GCM", 1000));
        Assert.Null(MRemoteNgCrypto.TryDecrypt(enc, "wrong", "GCM", 1000));
    }

    [Fact]
    public void Passwords_in_mremoteng_file_are_decrypted()
    {
        var check = MRemoteNgCrypto.EncryptGcm("ThisIsProtected", "mR3m", 1000);
        var result = MRemoteNgImporter.Parse(Xml(check, MRemoteNgCrypto.EncryptGcm("hunter2", "mR3m", 1000)));
        Assert.Equal("hunter2", result.Servers.Single(s => s.Name == "web").Password);
        Assert.Equal("", result.Servers.Single(s => s.Name == "empty").Password);
        Assert.Equal(0, result.PasswordsFailed);
    }

    [Fact]
    public void Custom_master_password_is_requested_and_then_works()
    {
        var check = MRemoteNgCrypto.EncryptGcm("ThisIsProtected", "my-master", 1000);
        var xml = Xml(check, MRemoteNgCrypto.EncryptGcm("hunter2", "my-master", 1000));

        var ex = Assert.Throws<MasterPasswordRequiredException>(() => MRemoteNgImporter.Parse(xml));
        Assert.Contains("MREMOTENG_PASSWORD", ex.Message);
        Assert.Equal("hunter2", MRemoteNgImporter.Parse(xml, "my-master").Servers.First().Password);
    }

    [Fact]
    public void Ssh_config_reports_identity_files_with_default_fallback()
    {
        var r = SshConfigImporter.Parse("""
            Host web
              HostName 10.0.0.5
              IdentityFile ~/.ssh/web_key
            Host db
              HostName 10.0.0.6
            Host *
              IdentityFile ~/.ssh/id_ed25519
            """);
        Assert.Equal("~/.ssh/web_key", r.KeyFiles![r.Servers.Single(s => s.Name == "web").Id]);
        Assert.Equal("~/.ssh/id_ed25519", r.KeyFiles[r.Servers.Single(s => s.Name == "db").Id]);
    }
}
