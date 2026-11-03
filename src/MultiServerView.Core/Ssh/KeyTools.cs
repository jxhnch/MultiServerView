namespace MultiServerView.Core.Ssh;

public static class KeyTools
{
    /// <summary>
    /// Prüft einen privaten Key (und die Passphrase) und gibt den Typ zurück ("ED25519", "RSA", "ECDSA").
    /// Wirft <see cref="SshConnectException"/>, wenn der Key nicht lesbar ist.
    /// </summary>
    public static string Inspect(string privateKey, string passphrase)
    {
        var text = privateKey.Trim();
        if (!text.Contains("PRIVATE KEY", StringComparison.Ordinal))
            throw new SshConnectException("Das sieht nicht nach einem privaten SSH-Key aus (Zeile «BEGIN … PRIVATE KEY» fehlt).");
        if (text.Contains("PuTTY-User-Key-File", StringComparison.Ordinal))
            throw new SshConnectException("PuTTY-Keys (.ppk) werden nicht unterstützt. In PuTTYgen: Conversions → Export OpenSSH key.");

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
