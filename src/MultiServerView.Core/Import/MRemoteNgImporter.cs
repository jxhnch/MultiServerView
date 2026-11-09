using System.Xml.Linq;

namespace MultiServerView.Core.Import;

/// <param name="KeyFiles">Optional: id of an imported host -> path of its private key file (the app turns them into vault keys).</param>
public sealed record ImportResult(List<ServerEntry> Servers, int Skipped, Dictionary<string, string>? KeyFiles = null)
{
    /// <summary>Passwords that could not be decrypted (wrong master password or unknown format).</summary>
    public int PasswordsFailed { get; init; }
}

/// <summary>
/// Reads an mRemoteNG file (confCons.xml). Folders become groups.
/// Supported: RDP, SSH (1/2), VNC. Everything else (Telnet, HTTP, ...) is skipped.
/// Saved passwords are decrypted with the mRemoteNG master password (default "mR3m"). If the file uses a custom
/// master password, <see cref="MasterPasswordRequiredException"/> is thrown so the app can ask for it.
/// </summary>
public static class MRemoteNgImporter
{
    public static ImportResult Parse(string xml, string masterPassword = MRemoteNgCrypto.DefaultMasterPassword)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (System.Xml.XmlException)
        {
            throw new ArgumentException("This is not a valid mRemoteNG file (confCons.xml).");
        }

        if (doc.Root == null || !doc.Root.Name.LocalName.Equals("Connections", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("This is not an mRemoteNG file (the root \"Connections\" is missing).");

        var mode = (string?)doc.Root.Attribute("BlockCipherMode") ?? "CBC";
        _ = int.TryParse((string?)doc.Root.Attribute("KdfIterations"), out var kdf);
        if (kdf <= 0) kdf = 1000;

        // The "Protected" attribute holds a known text encrypted with the master password: use it to check the password.
        var check = (string?)doc.Root.Attribute("Protected");
        if (!string.IsNullOrEmpty(check) && MRemoteNgCrypto.TryDecrypt(check, masterPassword, mode, kdf) == null)
            throw new MasterPasswordRequiredException();

        var ctx = new Crypto(masterPassword, mode, kdf);
        var servers = new List<ServerEntry>();
        var skipped = 0;
        Walk(doc.Root, new List<string>(), servers, ref skipped, ctx);
        return new ImportResult(servers, skipped) { PasswordsFailed = ctx.Failed };
    }

    private sealed class Crypto(string password, string mode, int kdf)
    {
        public int Failed;
        public string Decrypt(string? text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var plain = MRemoteNgCrypto.TryDecrypt(text, password, mode, kdf);
            if (plain == null) Failed++;
            return plain ?? "";
        }
    }

    private static void Walk(XElement parent, List<string> path, List<ServerEntry> output, ref int skipped, Crypto ctx)
    {
        foreach (var node in parent.Elements().Where(e => e.Name.LocalName == "Node"))
        {
            var type = (string?)node.Attribute("Type") ?? "Connection";
            var name = (string?)node.Attribute("Name") ?? "";

            if (type.Equals("Container", StringComparison.OrdinalIgnoreCase))
            {
                path.Add(name);
                Walk(node, path, output, ref skipped, ctx);
                path.RemoveAt(path.Count - 1);
                continue;
            }

            var protocolText = ((string?)node.Attribute("Protocol") ?? "").ToUpperInvariant();
            Protocol? protocol = protocolText switch
            {
                "RDP" => Protocol.Rdp,
                "SSH1" or "SSH2" => Protocol.Ssh,
                "VNC" => Protocol.Vnc,
                _ => null
            };
            var host = ((string?)node.Attribute("Hostname") ?? "").Trim();

            if (protocol == null || host.Length == 0)
            {
                skipped++;
                continue;
            }

            var user = ((string?)node.Attribute("Username") ?? "").Trim();
            var domain = ((string?)node.Attribute("Domain") ?? "").Trim();
            if (protocol == Protocol.Rdp && domain.Length > 0 && user.Length > 0 && !user.Contains('\\'))
                user = domain + "\\" + user;

            _ = int.TryParse((string?)node.Attribute("Port"), out var port);

            output.Add(new ServerEntry
            {
                Name = name.Length > 0 ? name : host,
                Host = host,
                Port = port > 0 ? port : ServerEntry.DefaultPort(protocol.Value),
                Protocol = protocol.Value,
                Username = user,
                Password = ctx.Decrypt((string?)node.Attribute("Password")),
                Group = string.Join(" / ", path.Where(p => p.Length > 0)),
                Notes = ((string?)node.Attribute("Descr") ?? "").Trim()
            });
        }
    }
}

/// <summary>The mRemoteNG file is protected with a custom master password.</summary>
public sealed class MasterPasswordRequiredException() : ArgumentException("MREMOTENG_PASSWORD: This mRemoteNG file uses a custom master password. Enter it to import the saved passwords.");
