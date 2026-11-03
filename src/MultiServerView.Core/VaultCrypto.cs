using System.Security.Cryptography;

namespace MultiServerView.Core;

/// <summary>Abgeleiteter Schlüssel. Wird im Speicher gehalten solange der Vault offen ist.</summary>
public sealed class VaultKey
{
    public VaultKey(byte[] key, byte[] salt, int iterations)
    {
        Key = key;
        Salt = salt;
        Iterations = iterations;
    }

    public byte[] Key { get; }
    public byte[] Salt { get; }
    public int Iterations { get; }
}

/// <summary>
/// Verschlüsselung des Vaults: PBKDF2-SHA256 (Passwort -> Key) + AES-256-GCM.
/// Dateiformat: "MSV1" | iterations(int32 LE) | salt(16) | nonce(12) | tag(16) | ciphertext.
/// Header (magic + iterations + salt) wird als Associated Data mit authentifiziert.
/// </summary>
public static class VaultCrypto
{
    private static readonly byte[] Magic = "MSV1"u8.ToArray();
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int HeaderSize = 4 + 4 + SaltSize;

    public const int DefaultIterations = 600_000;

    public static VaultKey Derive(string password, byte[]? salt = null, int iterations = DefaultIterations)
    {
        salt ??= RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, KeySize);
        return new VaultKey(key, salt, iterations);
    }

    /// <summary>Verschlüsselt mit bereits abgeleitetem Key. Pro Aufruf neue Nonce.</summary>
    public static byte[] Seal(byte[] plaintext, VaultKey key)
    {
        var header = BuildHeader(key);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var tag = new byte[TagSize];
        var ciphertext = new byte[plaintext.Length];

        using var aes = new AesGcm(key.Key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, header);

        var blob = new byte[HeaderSize + NonceSize + TagSize + ciphertext.Length];
        header.CopyTo(blob, 0);
        nonce.CopyTo(blob, HeaderSize);
        tag.CopyTo(blob, HeaderSize + NonceSize);
        ciphertext.CopyTo(blob, HeaderSize + NonceSize + TagSize);
        return blob;
    }

    /// <summary>Entschlüsselt. Wirft <see cref="CryptographicException"/> bei falschem Passwort oder manipulierter Datei.</summary>
    public static byte[] Open(byte[] blob, string password, out VaultKey key)
    {
        if (blob.Length < HeaderSize + NonceSize + TagSize || !blob.AsSpan(0, 4).SequenceEqual(Magic))
            throw new CryptographicException("Keine gültige Vault-Datei.");

        var iterations = BitConverter.ToInt32(blob, 4);
        if (iterations < 100_000 || iterations > 10_000_000)
            throw new CryptographicException("Ungültige Vault-Datei (Iterationen).");

        var salt = blob.AsSpan(8, SaltSize).ToArray();
        key = Derive(password, salt, iterations);

        var header = blob.AsSpan(0, HeaderSize).ToArray();
        var nonce = blob.AsSpan(HeaderSize, NonceSize);
        var tag = blob.AsSpan(HeaderSize + NonceSize, TagSize);
        var ciphertext = blob.AsSpan(HeaderSize + NonceSize + TagSize);
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(key.Key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, header);
        return plaintext;
    }

    private static byte[] BuildHeader(VaultKey key)
    {
        var header = new byte[HeaderSize];
        Magic.CopyTo(header, 0);
        BitConverter.GetBytes(key.Iterations).CopyTo(header, 4);
        key.Salt.CopyTo(header, 8);
        return header;
    }
}
