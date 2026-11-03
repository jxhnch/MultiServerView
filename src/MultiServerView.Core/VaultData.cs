using System.Text.Json;

namespace MultiServerView.Core;

/// <summary>Der komplette (entschlüsselte) Inhalt des Vaults.</summary>
public sealed class VaultData
{
    public int Version { get; set; } = 2;
    public List<ServerEntry> Servers { get; set; } = new();
    public List<KeyEntry> Keys { get; set; } = new();

    /// <summary>"host:port" -> SHA256-Fingerprint (Trust on first use).</summary>
    public Dictionary<string, string> KnownHosts { get; set; } = new();

    /// <summary>Id -> Zeitpunkt der Löschung. Damit Löschungen beim Cloud-Sync nicht "wiederauferstehen".</summary>
    public Dictionary<string, DateTimeOffset> Tombstones { get; set; } = new();

    public VaultData Clone() =>
        JsonSerializer.Deserialize<VaultData>(JsonSerializer.SerializeToUtf8Bytes(this, Json.Options), Json.Options)
        ?? new VaultData();
}

/// <summary>
/// Führt zwei Stände desselben Vaults zusammen (lokal + Cloud).
/// Pro Eintrag gewinnt der neuere <c>UpdatedAt</c>; Löschungen (Tombstones) gewinnen gegen ältere Änderungen.
/// Das Ergebnis ist deterministisch sortiert, damit zwei gleiche Stände auch gleich serialisieren.
/// </summary>
public static class VaultMerge
{
    public static VaultData Merge(VaultData x, VaultData y)
    {
        var a = x.Clone();
        var b = y.Clone();
        var result = new VaultData();

        // Tombstones: pro Id der spätere Zeitpunkt
        foreach (var src in new[] { a.Tombstones, b.Tombstones })
            foreach (var (id, at) in src)
                result.Tombstones[id] = result.Tombstones.TryGetValue(id, out var cur) && cur > at ? cur : at;

        result.Servers = MergeById(a.Servers, b.Servers, s => s.Id, s => s.UpdatedAt, result.Tombstones,
            (winner, loser) =>
            {
                // "Zuletzt benutzt" ist unabhängig von Änderungen: immer der spätere Wert.
                if (loser.LastUsed.HasValue && (!winner.LastUsed.HasValue || loser.LastUsed > winner.LastUsed))
                    winner.LastUsed = loser.LastUsed;
            });

        result.Keys = MergeById(a.Keys, b.Keys, k => k.Id, k => k.UpdatedAt, result.Tombstones, null);

        foreach (var src in new[] { a.KnownHosts, b.KnownHosts })
            foreach (var (host, fp) in src)
                result.KnownHosts.TryAdd(host, fp);

        // Deterministische Reihenfolge (Dictionary behält die Einfügereihenfolge beim Serialisieren).
        result.KnownHosts = result.KnownHosts.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        result.Tombstones = result.Tombstones.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        return result;
    }

    /// <summary>Stabile, vergleichbare Darstellung (für "hat sich etwas geändert?").</summary>
    public static string Fingerprint(VaultData d) =>
        JsonSerializer.Serialize(Merge(d, new VaultData()), Json.Options);

    private static List<T> MergeById<T>(
        List<T> a, List<T> b,
        Func<T, string> id, Func<T, DateTimeOffset> updated,
        Dictionary<string, DateTimeOffset> tombstones,
        Action<T, T>? onPair)
    {
        var map = new Dictionary<string, T>();
        foreach (var item in a) map[id(item)] = item;

        foreach (var item in b)
        {
            if (!map.TryGetValue(id(item), out var existing))
            {
                map[id(item)] = item;
                continue;
            }

            // Bei Gleichstand gewinnt der Cloud-Stand (b) - Hauptsache beide Seiten entscheiden gleich.
            var winner = updated(item) >= updated(existing) ? item : existing;
            var loser = ReferenceEquals(winner, item) ? existing : item;
            onPair?.Invoke(winner, loser);
            map[id(item)] = winner;
        }

        return map.Values
            .Where(item => !(tombstones.TryGetValue(id(item), out var deletedAt) && deletedAt >= updated(item)))
            .OrderBy(id, StringComparer.Ordinal)
            .ToList();
    }
}
