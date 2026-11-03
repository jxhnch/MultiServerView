using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;

namespace MultiServerView.Core.Ssh;

public static partial class KeyTools
{
    /// <summary>Result of <see cref="Generate"/>: private key (OpenSSH/PEM), public key line and type.</summary>
    public sealed record GeneratedKey(string PrivateKey, string PublicKey, string Kind);

    /// <summary>
    /// Generates a new key pair. <paramref name="type"/>: "ed25519" (default) or "rsa".
    /// The private key has no passphrase because it is only stored encrypted in the vault anyway.
    /// </summary>
    public static GeneratedKey Generate(string type, int rsaBits, string comment)
    {
        comment = CleanComment(comment);
        string privateKey, kind;
        if (string.Equals(type, "rsa", StringComparison.OrdinalIgnoreCase))
        {
            var bits = rsaBits is 2048 or 3072 or 4096 ? rsaBits : 3072;
            using var rsa = RSA.Create(bits);
            privateKey = rsa.ExportRSAPrivateKeyPem() + "\n";
            kind = "RSA";
        }
        else
        {
            privateKey = GenerateEd25519(comment);
            kind = "ED25519";
        }

        return new GeneratedKey(privateKey, PublicKeyLine(new KeyEntry { PrivateKey = privateKey }, comment), kind);
    }

    /// <summary>Public key line in authorized_keys format ("ssh-ed25519 AAAA… comment") for a private key.</summary>
    public static string PublicKeyLine(KeyEntry key, string comment = "")
    {
        var file = SshConnector.LoadKey(key);
        var algorithm = file.HostKeyAlgorithms.FirstOrDefault()
                        ?? throw new SshConnectException("No public key can be derived from this key.");
        var line = algorithm.Name + " " + Convert.ToBase64String(algorithm.Data);
        comment = CleanComment(comment);
        return comment.Length > 0 ? line + " " + comment : line;
    }

    /// <summary>SHA256 fingerprint of a public key line, as OpenSSH shows it.</summary>
    public static string Fingerprint(string publicKeyLine)
    {
        var parts = publicKeyLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return "";
        return "SHA256:" + Convert.ToBase64String(SHA256.HashData(Convert.FromBase64String(parts[1]))).TrimEnd('=');
    }

    private static string CleanComment(string? comment) =>
        new string((comment ?? "").Where(c => !char.IsControl(c) && c != '\'' && c != '"').ToArray()).Trim();

    /// <summary>Writes an unencrypted ed25519 key pair in the "openssh-key-v1" format.</summary>
    private static string GenerateEd25519(string comment)
    {
        var generator = new Ed25519KeyPairGenerator();
        generator.Init(new Ed25519KeyGenerationParameters(new Org.BouncyCastle.Security.SecureRandom()));
        var pair = generator.GenerateKeyPair();
        var seed = ((Ed25519PrivateKeyParameters)pair.Private).GetEncoded();
        var pub = ((Ed25519PublicKeyParameters)pair.Public).GetEncoded();

        var publicBlob = new List<byte>();
        WriteString(publicBlob, "ssh-ed25519");
        WriteString(publicBlob, pub);

        var check = RandomNumberGenerator.GetBytes(4);
        var priv = new List<byte>();
        priv.AddRange(check);
        priv.AddRange(check);
        WriteString(priv, "ssh-ed25519");
        WriteString(priv, pub);
        WriteString(priv, seed.Concat(pub).ToArray());
        WriteString(priv, comment);
        for (byte pad = 1; priv.Count % 8 != 0; pad++) priv.Add(pad);

        var file = new List<byte>();
        file.AddRange(Encoding.ASCII.GetBytes("openssh-key-v1"));
        file.Add(0);
        WriteString(file, "none");
        WriteString(file, "none");
        WriteString(file, Array.Empty<byte>());
        file.AddRange(new byte[] { 0, 0, 0, 1 });
        WriteString(file, publicBlob.ToArray());
        WriteString(file, priv.ToArray());

        var b64 = Convert.ToBase64String(file.ToArray());
        var sb = new StringBuilder("-----BEGIN OPENSSH PRIVATE KEY-----\n");
        for (var i = 0; i < b64.Length; i += 70) sb.Append(b64, i, Math.Min(70, b64.Length - i)).Append('\n');
        return sb.Append("-----END OPENSSH PRIVATE KEY-----\n").ToString();
    }

    private static void WriteString(List<byte> buffer, string value) => WriteString(buffer, Encoding.ASCII.GetBytes(value));

    private static void WriteString(List<byte> buffer, byte[] value)
    {
        buffer.Add((byte)(value.Length >> 24));
        buffer.Add((byte)(value.Length >> 16));
        buffer.Add((byte)(value.Length >> 8));
        buffer.Add((byte)value.Length);
        buffer.AddRange(value);
    }
}
