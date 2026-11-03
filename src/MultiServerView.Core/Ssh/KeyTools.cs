namespace MultiServerView.Core.Ssh;

public static partial class KeyTools
{
    /// <summary>
    /// Checks a private key (and the passphrase) and returns the type ("ED25519", "RSA", "ECDSA").
    /// Throws <see cref="SshConnectException"/> if the key cannot be read.
    /// </summary>
    public static string Inspect(string privateKey, string passphrase)
    {
        var text = privateKey.Trim();
        if (!text.Contains("PRIVATE KEY", StringComparison.Ordinal))
            throw new SshConnectException("This does not look like a private SSH key (the line \"BEGIN … PRIVATE KEY\" is missing).");
        if (text.Contains("PuTTY-User-Key-File", StringComparison.Ordinal))
            throw new SshConnectException("PuTTY keys (.ppk) are not supported. In PuTTYgen: Conversions → Export OpenSSH key.");

        var file = SshConnector.LoadKey(new KeyEntry { PrivateKey = text, Passphrase = passphrase });
        var algorithm = file.HostKeyAlgorithms.FirstOrDefault()?.Name ?? "";
        return algorithm switch
        {
            var a when a.Contains("ed25519", StringComparison.OrdinalIgnoreCase) => "ED25519",
            var a when a.Contains("rsa", StringComparison.OrdinalIgnoreCase) => "RSA",
            var a when a.Contains("ecdsa", StringComparison.OrdinalIgnoreCase) => "ECDSA",
            _ => "Key"
        };
    }
}
