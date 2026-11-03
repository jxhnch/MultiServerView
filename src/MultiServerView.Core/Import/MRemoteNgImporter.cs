using System.Xml.Linq;

namespace MultiServerView.Core.Import;

public sealed record ImportResult(List<ServerEntry> Servers, int Skipped);

/// <summary>
/// Reads an mRemoteNG file (confCons.xml). Folders become groups.
/// Supported: RDP, SSH (1/2), VNC. Everything else (Telnet, HTTP, ...) is skipped.
/// Passwords are deliberately NOT imported (mRemoteNG encrypts them with its own method) -
/// please enter them again after the import.
/// </summary>
public static class MRemoteNgImporter
{
    public static ImportResult Parse(string xml)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (System.Xml.XmlException)
        {
            throw new ArgumentException("This is not a valid mRemoteNG file (confCons.xml).");
        }

        if (doc.Root == null || !doc.Root.Name.LocalName.Equals("Connections", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("This is not an mRemoteNG file (the root \"Connections\" is missing).");

        var servers = new List<ServerEntry>();
        var skipped = 0;
        Walk(doc.Root, new List<string>(), servers, ref skipped);
        return new ImportResult(servers, skipped);
    }

    private static void Walk(XElement parent, List<string> path, List<ServerEntry> output, ref int skipped)
    {
        foreach (var node in parent.Elements().Where(e => e.Name.LocalName == "Node"))
        {
            var type = (string?)node.Attribute("Type") ?? "Connection";
            var name = (string?)node.Attribute("Name") ?? "";

            if (type.Equals("Container", StringComparison.OrdinalIgnoreCase))
            {
                path.Add(name);
                Walk(node, path, output, ref skipped);
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
                Group = string.Join(" / ", path.Where(p => p.Length > 0)),
                Notes = ((string?)node.Attribute("Descr") ?? "").Trim()
            });
        }
    }
}
