using System.Security.Cryptography;
using System.Text;
using MultiServerView.Core.Sync;

namespace MultiServerView.App;

/// <summary>Windows DPAPI: Nur dieser Windows-Benutzer auf diesem PC kann das Cloud-Passwort wieder lesen.</summary>
internal sealed class DpapiProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MultiServerView.sync.v1");

    public string Protect(string plain) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));

    public string Unprotect(string protectedText) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedText), Entropy, DataProtectionScope.CurrentUser));
}
