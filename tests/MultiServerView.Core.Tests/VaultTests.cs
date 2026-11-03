using System.Security.Cryptography;
using MultiServerView.Core;
using Xunit;

namespace MultiServerView.Core.Tests;

public class VaultCryptoTests
{
    // Wenige Iterationen, damit die Tests schnell laufen (Format erlaubt >= 100_000).
    private const int FastIterations = 100_000;

    [Fact]
    public void Seal_then_Open_roundtrips()
    {
        var key = VaultCrypto.Derive("geheim-123", iterations: FastIterations);
        var plain = "hallo welt"u8.ToArray();

        var blob = VaultCrypto.Seal(plain, key);
        var opened = VaultCrypto.Open(blob, "geheim-123", out _);

        Assert.Equal(plain, opened);
    }

    [Fact]
    public void Open_with_wrong_password_throws()
    {
        var key = VaultCrypto.Derive("richtig-123", iterations: FastIterations);
        var blob = VaultCrypto.Seal("x"u8.ToArray(), key);

        Assert.ThrowsAny<CryptographicException>(() => VaultCrypto.Open(blob, "falsch-123", out _));
    }

    [Fact]
    public void Open_detects_tampering()
    {
        var key = VaultCrypto.Derive("geheim-123", iterations: FastIterations);
        var blob = VaultCrypto.Seal("daten"u8.ToArray(), key);
        blob[^1] ^= 0xFF;

        Assert.ThrowsAny<CryptographicException>(() => VaultCrypto.Open(blob, "geheim-123", out _));
    }

    [Fact]
    public void Seal_uses_fresh_nonce_each_time()
    {
        var key = VaultCrypto.Derive("geheim-123", iterations: FastIterations);
        var a = VaultCrypto.Seal("gleich"u8.ToArray(), key);
        var b = VaultCrypto.Seal("gleich"u8.ToArray(), key);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void OpenWithKey_rejects_blob_with_other_salt()
    {
        var mine = VaultCrypto.Derive("geheim-123", iterations: FastIterations);
        var other = VaultCrypto.Derive("geheim-123", iterations: FastIterations); // gleiches Passwort, anderer Salt
        var blob = VaultCrypto.Seal("daten"u8.ToArray(), other);

        Assert.Throws<VaultMismatchException>(() => VaultCrypto.OpenWithKey(blob, mine));
    }

    [Fact]
    public void OpenWithKey_roundtrips_with_same_key()
    {
        var key = VaultCrypto.Derive("geheim-123", iterations: FastIterations);
        var blob = VaultCrypto.Seal("daten"u8.ToArray(), key);

        Assert.Equal("daten"u8.ToArray(), VaultCrypto.OpenWithKey(blob, key));
    }
}

public class VaultStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "msv-tests-" + Guid.NewGuid().ToString("N"));
    private string VaultPath => Path.Combine(_dir, "vault.msv");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Create_Upsert_Lock_Unlock_keeps_data()
    {
        var store = new VaultStore(VaultPath);
        store.Create("master-passwort");
        store.Upsert(new ServerEntry { Name = "web-1", Host = "10.0.0.5", Protocol = Protocol.Ssh, Password = "pw" });
        store.Lock();

        var again = new VaultStore(VaultPath);
        again.Unlock("master-passwort");
        var list = again.List();

        Assert.Single(list);
        Assert.Equal("web-1", list[0].Name);
        Assert.Equal(22, list[0].Port); // Default-Port wurde gesetzt
        Assert.Equal("pw", list[0].Password);
        Assert.True(list[0].UpdatedAt > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public void Unlock_with_wrong_password_throws_WrongPassword()
    {
        var store = new VaultStore(VaultPath);
        store.Create("master-passwort");
        store.Lock();

        Assert.Throws<WrongPasswordException>(() => store.Unlock("falsches-passwort"));
        Assert.False(store.IsUnlocked);
    }

    [Fact]
    public void Vault_file_does_not_contain_plaintext()
    {
        var store = new VaultStore(VaultPath);
        store.Create("master-passwort");
        store.Upsert(new ServerEntry { Name = "geheimer-name", Host = "example.com", Password = "supersecret" });
        store.UpsertKey(new KeyEntry { Name = "mein-key", PrivateKey = "-----BEGIN OPENSSH PRIVATE KEY-----geheim" });

        var raw = File.ReadAllText(VaultPath, System.Text.Encoding.Latin1);
        Assert.DoesNotContain("supersecret", raw);
        Assert.DoesNotContain("geheimer-name", raw);
        Assert.DoesNotContain("example.com", raw);
        Assert.DoesNotContain("mein-key", raw);
        Assert.DoesNotContain("PRIVATE KEY", raw);
    }

    [Fact]
    public void Delete_and_Touch_work()
    {
        var store = new VaultStore(VaultPath);
        store.Create("master-passwort");
        var a = store.Upsert(new ServerEntry { Name = "a", Host = "a" });
        var b = store.Upsert(new ServerEntry { Name = "b", Host = "b" });

        var touched = store.Touch(a.Id);
        store.Delete(b.Id);

        Assert.NotNull(touched.LastUsed);
        Assert.Single(store.List());
        Assert.True(store.Snapshot().Tombstones.ContainsKey(b.Id));
    }

    [Fact]
    public void Locked_vault_rejects_access()
    {
        var store = new VaultStore(VaultPath);
        store.Create("master-passwort");
        store.Lock();

        Assert.Throws<InvalidOperationException>(() => store.List());
    }

    [Fact]
    public void Create_rejects_short_password()
    {
        var store = new VaultStore(VaultPath);
        Assert.Throws<ArgumentException>(() => store.Create("kurz"));
    }

    [Fact]
    public void Keys_roundtrip_and_deleting_a_key_resets_servers()
    {
        var store = new VaultStore(VaultPath);
        store.Create("master-passwort");
        var key = store.UpsertKey(new KeyEntry { Name = "k", PrivateKey = "x" });
        var server = store.Upsert(new ServerEntry { Name = "s", Host = "h", Auth = AuthMethod.Key, KeyId = key.Id });

        store.Lock();
        store.Unlock("master-passwort");
        Assert.Single(store.ListKeys());
        Assert.Equal(AuthMethod.Key, store.Find(server.Id)!.Auth);

        store.DeleteKey(key.Id);
        Assert.Empty(store.ListKeys());
        var after = store.Find(server.Id)!;
        Assert.Equal(AuthMethod.Password, after.Auth);
        Assert.Equal("", after.KeyId);
    }

    [Fact]
    public void HostKey_is_trusted_on_first_use_then_checked()
    {
        var store = new VaultStore(VaultPath);
        store.Create("master-passwort");

        Assert.Equal(HostKeyTrust.NewlyTrusted, store.CheckHostKey("Example.com", 22, "SHA256:aaa"));
        Assert.Equal(HostKeyTrust.Trusted, store.CheckHostKey("example.com", 22, "SHA256:aaa"));
        Assert.Equal(HostKeyTrust.Mismatch, store.CheckHostKey("example.com", 22, "SHA256:bbb"));

        store.ForgetHostKey("example.com", 22);
        Assert.Equal(HostKeyTrust.NewlyTrusted, store.CheckHostKey("example.com", 22, "SHA256:bbb"));
    }

    [Fact]
    public void Changed_event_fires_for_edits_but_not_for_touch()
    {
        var store = new VaultStore(VaultPath);
        store.Create("master-passwort");
        var count = 0;
        store.Changed += () => count++;

        var s = store.Upsert(new ServerEntry { Name = "a", Host = "a" });
        Assert.Equal(1, count);
        store.Touch(s.Id);
        Assert.Equal(1, count);
        store.Delete(s.Id);
        Assert.Equal(2, count);
    }

    [Fact]
    public void Old_v1_vault_without_keys_still_loads()
    {
        var store = new VaultStore(VaultPath);
        var key = VaultCrypto.Derive("master-passwort", iterations: 100_000);
        var oldJson = """{"version":1,"servers":[{"id":"a","name":"alt","host":"h","port":22,"protocol":"ssh"}]}""";
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(VaultPath, VaultCrypto.Seal(System.Text.Encoding.UTF8.GetBytes(oldJson), key));

        store.Unlock("master-passwort");

        Assert.Single(store.List());
        Assert.Empty(store.ListKeys());
    }
}
