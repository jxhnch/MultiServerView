using System.Text;

namespace MultiServerView.Core.Import;

/// <summary>
/// Importiert Hosts aus einer CSV-Datei (Trennzeichen , ; oder Tab - wird erkannt). Erste Zeile = Spaltennamen.
/// Erkannte Spalten (Gross-/Kleinschreibung egal): name/label, host/hostname/address/ip, port, protocol/type,
/// user/username, password, group/groups/folder, tags, notes/description.
/// </summary>
public static class CsvImporter
{
    public static ImportResult Parse(string csv)
    {
        var rows = ReadRows(csv.TrimStart('﻿'));
        if (rows.Count < 2) throw new ArgumentException("Die CSV-Datei braucht eine Kopfzeile und mindestens eine Datenzeile.");

        var header = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Col(params string[] names) => header.FindIndex(h => names.Contains(h));

        var iName = Col("name", "label", "title", "bezeichnung");
        var iHost = Col("host", "hostname", "hostname/ip", "address", "ip", "adresse");
        var iPort = Col("port");
        var iProto = Col("protocol", "type", "protokoll");
        var iUser = Col("user", "username", "login", "benutzer");
        var iPass = Col("password", "passwort");
        var iGroup = Col("group", "groups", "folder", "gruppe", "ordner");
        var iTags = Col("tags", "tag");
        var iNotes = Col("notes", "note", "description", "notizen", "comment");

        if (iHost < 0) throw new ArgumentException("In der CSV fehlt eine Spalte «host» (oder hostname / address / ip).");

        string Get(List<string> r, int i) => i >= 0 && i < r.Count ? r[i].Trim() : "";

        var servers = new List<ServerEntry>();
        var skipped = 0;
        foreach (var r in rows.Skip(1))
        {
            var host = Get(r, iHost);
            if (host.Length == 0) { skipped++; continue; }

            var protocolText = Get(r, iProto).ToLowerInvariant();
            Protocol? protocol = protocolText switch
            {
                "" or "ssh" or "ssh2" => Protocol.Ssh,
                "rdp" => Protocol.Rdp,
                "vnc" => Protocol.Vnc,
                _ => null
            };
            if (protocol == null) { skipped++; continue; }

            _ = int.TryParse(Get(r, iPort), out var port);
            servers.Add(new ServerEntry
            {
                Name = Get(r, iName).Length > 0 ? Get(r, iName) : host,
                Host = host,
                Port = port > 0 ? port : ServerEntry.DefaultPort(protocol.Value),
                Protocol = protocol.Value,
                Username = Get(r, iUser),
                Password = Get(r, iPass),
                Group = Get(r, iGroup),
                Tags = Get(r, iTags).Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                Notes = Get(r, iNotes)
            });
        }
        return new ImportResult(servers, skipped);
    }

    private static List<List<string>> ReadRows(string text)
    {
        var firstLine = text.Split('\n', 2)[0];
        var delimiter = new[] { ',', ';', '\t' }.OrderByDescending(d => firstLine.Count(c => c == d)).First();

        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == delimiter) { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString());
                field.Clear();
                if (row.Any(f => f.Length > 0)) rows.Add(row);
                row = new List<string>();
            }
            else field.Append(c);
        }
        row.Add(field.ToString());
        if (row.Any(f => f.Length > 0)) rows.Add(row);
        return rows;
    }
}
