using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;

namespace MultiServerView.Core.Import;

/// <summary>
/// Decrypts the passwords inside an mRemoteNG file. mRemoteNG encrypts them with a "master password"
/// that defaults to "mR3m". Supported: AES-GCM (new files: salt + nonce + ciphertext + tag, PBKDF2) and
/// AES-CBC (old files: IV + ciphertext, MD5 of the master password as key).
/// </summary>
public static class MRemoteNgCrypto
{
    public const string DefaultMasterPassword = "mR3m";

    public static string? TryDecrypt(string base64, string masterPassword, string mode, int kdfIterations)
    {
        if (string.IsNullOrWhiteSpace(base64)) return "";
        try
        {
            var data = Convert.FromBase64String(base64);
            return mode.Equals("GCM", StringComparison.OrdinalIgnoreCase)
                ? DecryptGcm(data, masterPassword, kdfIterations)
                : DecryptCbc(data, masterPassword);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException or Org.BouncyCastle.Crypto.InvalidCipherTextException)
        {
            return null;
        }
    }

    /// <summary>Encrypts like mRemoteNG does (only used by the tests to prove that decryption is the inverse).</summary>
    public static string EncryptGcm(string plain, string masterPassword, int kdfIterations)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(16);
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(masterPassword), salt, kdfIterations, HashAlgorithmName.SHA1, 32);
        var cipher = new GcmBlockCipher(new AesEngine());
        cipher.Init(true, new AeadParameters(new KeyParameter(key), 128, nonce, salt));
        var input = Encoding.UTF8.GetBytes(plain);
        var output = new byte[cipher.GetOutputSize(input.Length)];
        var n = cipher.ProcessBytes(input, 0, input.Length, output, 0);
        cipher.DoFinal(output, n);
        return Convert.ToBase64String(salt.Concat(nonce).Concat(output).ToArray());
    }

    private static string? DecryptGcm(byte[] data, string masterPassword, int kdfIterations)
    {
        if (data.Length < 16 + 16 + 16) return null;
        var salt = data[..16];
        var nonce = data[16..32];
        var body = data[32..];
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(masterPassword), salt, kdfIterations, HashAlgorithmName.SHA1, 32);

        foreach (var aad in new[] { salt, Array.Empty<byte>() })
        {
            try
            {
                var cipher = new GcmBlockCipher(new AesEngine());
                cipher.Init(false, new AeadParameters(new KeyParameter(key), 128, nonce, aad));
                var output = new byte[cipher.GetOutputSize(body.Length)];
                var n = cipher.ProcessBytes(body, 0, body.Length, output, 0);
                n += cipher.DoFinal(output, n);
                return Encoding.UTF8.GetString(output, 0, n);
            }
            catch (Org.BouncyCastle.Crypto.InvalidCipherTextException) { /* wrong password or different AAD */ }
        }
        return null;
    }

    private static string? DecryptCbc(byte[] data, string masterPassword)
    {
        if (data.Length < 32) return null;
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = MD5.HashData(Encoding.UTF8.GetBytes(masterPassword));
        aes.IV = data[..16];
        return Encoding.UTF8.GetString(aes.DecryptCbc(data[16..], aes.IV, PaddingMode.PKCS7));
    }
}
