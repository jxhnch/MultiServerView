using MultiServerView.Core;
using MultiServerView.Core.Ssh;
using Xunit;

namespace MultiServerView.Core.Tests;

public class KeyGeneratorTests
{
    [Theory]
    [InlineData("ed25519", "ED25519", "ssh-ed25519")]
    [InlineData("rsa", "RSA", "ssh-rsa")]
    public void Generated_key_can_be_loaded_and_has_matching_public_key(string type, string kind, string prefix)
    {
        var generated = KeyTools.Generate(type, 2048, "test@msv");

        Assert.Equal(kind, generated.Kind);
        Assert.StartsWith(prefix + " ", generated.PublicKey);
        Assert.EndsWith(" test@msv", generated.PublicKey);
        Assert.Equal(kind, KeyTools.Inspect(generated.PrivateKey, ""));

        // Derived from the private key the same public key line must come out.
        var derived = KeyTools.PublicKeyLine(new KeyEntry { PrivateKey = generated.PrivateKey }, "test@msv");
        Assert.Equal(generated.PublicKey, derived);
        Assert.StartsWith("SHA256:", KeyTools.Fingerprint(derived));
    }

    [Fact]
    public void Two_generated_keys_differ()
    {
        Assert.NotEqual(KeyTools.Generate("ed25519", 0, "").PublicKey, KeyTools.Generate("ed25519", 0, "").PublicKey);
    }

    [Fact]
    public void Comment_is_sanitised()
    {
        var generated = KeyTools.Generate("ed25519", 0, "a'b\"c\nd");
        Assert.DoesNotContain('\n', generated.PublicKey);
        Assert.DoesNotContain('\'', generated.PublicKey);
        Assert.EndsWith(" abcd", generated.PublicKey);
    }
}
