using System.Text.RegularExpressions;

namespace MultiServerView.Core.Import;

public sealed record PuttySession(string Name, string HostName, int Port, string Protocol, string User);

/// <summary>
/// Importiert gespeicherte PuTTY-Sitzungen - entweder aus einer exportierten .reg-Datei oder direkt aus
/// der Windows-Registry (die App liest sie und übergibt die Sitzungen an <see cref="FromSessions"/>).
/// Nur SSH-Sitzungen werden übernommen (Telnet, Serial, rlogin, raw werden übersprungen).
/// </summary>
public static partial class PuttyImporter
{
    public static ImportResult FromSessions(IEnumerable<PuttySession> sessions)
    {
        var servers = new List<ServerEntry>();
        var skipped = 0;
        foreach (var s in sessions)
        {
            if (!s.Protocol.Equals("ssh", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(s.HostName) || s.Name == "Default Settings")
            {
                skipped++;
                continue;
            }

            // PuTTY erlaubt "user@host" im Host-Feld.
            var host = s.HostName.Trim();
            var user = s.User;
            var at = host.IndexOf('@');
            if (at > 0)
            {
                if (user.Length == 0) user = host[..at];
                host = host[(at + 1)..];
            }

            servers.Add(new ServerEntry
            {
                Name = s.Name,
                Host = host,
                Port = s.Port > 0 ? s.Port : 22,
                Protocol = Protocol.Ssh,
                Username = user,
                Group = "PuTTY"
            });
        }
        return new ImportResult(servers, skipped);
    }

    /// <summary>Liest den Text einer exportierten .reg-Datei (HKCU\Software\SimonTatham\PuTTY\Sessions).</summary>
    public static ImportResult ParseReg(string reg)
    {
        if (!reg.Contains("SimonTatham\\PuTTY\\Sessions", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Das ist keine PuTTY-Exportdatei (Schlüssel «SimonTatham\\PuTTY\\Sessions» fehlt). Export: regedit → HKEY_CURRENT_USER\\Software\\SimonTatham\\PuTTY\\Sessions → Datei → Exportieren.");

        var sessions = new List<PuttySession>();
        string? name = null;
        string host = "", protocol = "", user = "";
        var port = 22;

        void Flush()
        {
            if (name != null) sessions.Add(new PuttySession(name, host, port, protocol.Length > 0 ? protocol : "ssh", user));
            name = null;
            host = protocol = user = "";
            port = 22;
        }

        foreach (var raw in reg.Split('\n'))
        {
            var line = raw.Trim().TrimStart('﻿');
            if (line.StartsWith('['))
            {
                Flush();
                var m = SessionKey().Match(line);
                if (m.Success) name = Uri.UnescapeDataString(m.Groups[1].Value);
                continue;
            }
            if (name == null) continue;

            var v = Value().Match(line);
            if (!v.Success) continue;
            var key = v.Groups[1].Value;
            var val = v.Groups[2].Value;
            switch (key)
            {
                case "HostName": host = Unescape(val); break;
                case "Protocol": protocol = Unescape(val); break;
                case "UserName": user = Unescape(val); break;
                case "PortNumber":
                    if (val.StartsWith("dword:", StringComparison.OrdinalIgnoreCase))
                        port = Convert.ToInt32(val[6..], 16);
                    break;
            }
        }
        Flush();
        return FromSessions(sessions);
    }

    private static string Unescape(string quoted) => quoted.Trim('"').Replace("\\\\", "\\").Replace("\\\"", "\"");

    [GeneratedRegex(@"^\[HKEY_CURRENT_USER\\Software\\SimonTatham\\PuTTY\\Sessions\\([^\]\\]+)\]$", RegexOptions.IgnoreCase)]
    private static partial Regex SessionKey();

    [GeneratedRegex("^\"([^\"]+)\"=(.*)$")]
    private static partial Regex Value();
}
