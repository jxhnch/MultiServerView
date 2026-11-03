using System.Text.Json;

namespace MultiServerView.Core;

/// <summary>The complete (decrypted) content of the vault.</summary>
public sealed class VaultData
{
    public int Version { get; set; } = 3;
    public List<ServerEntry> Servers { get; set; } = new();
    public List<KeyEntry> Keys { get; set; } = new();
    public List<IdentityEntry> Identities { get; set; } = new();
    public List<SnippetEntry> Snippets { get; set; } = new();
    public List<PortForwardEntry> Forwards { get; set; } = new();
    public List<ProxmoxEntry> Proxmox { get; set; } = new();

    /// <summary>"host:port" -> SHA256-Fingerprint (Trust on first use).</summary>
    public Dictionary<string, string> KnownHosts { get; set; } = new();

    /// <summary>Id -> time of deletion. So that deletions do not "come back to life" during sync.</summary>
    public Dictionary<string, DateTimeOffset> Tombstones { get; set; } = new();

    public VaultData Clone() =>
        JsonSerializer.Deserialize<VaultData>(JsonSerializer.SerializeToUtf8Bytes(this, Json.Options), Json.Options)
        ?? new VaultData();
}

/// <summary>
/// Merges two states of the same vault (local + synced).
/// Per entry the newer <c>UpdatedAt</c> wins; deletions (tombstones) win against older changes.
/// The result is sorted deterministically so two identical states also serialize identically.
/// </summary>
public static class VaultMerge
{
    public static VaultData Merge(VaultData x, VaultData y)
    {
        var a = x.Clone();
        var b = y.Clone();
        var result = new VaultData();

        // Tombstones: the later time per id
        foreach (var src in new[] { a.Tombstones, b.Tombstones })
            foreach (var (id, at) in src)
                result.Tombstones[id] = result.Tombstones.TryGetValue(id, out var cur) && cur > at ? cur : at;

        result.Servers = MergeById(a.Servers, b.Servers, s => s.Id, s => s.UpdatedAt, result.Tombstones,
            (winner, loser) =>
            {
                // "Last used" is independent of changes: always the later value.
                if (loser.LastUsed.HasValue && (!winner.LastUsed.HasValue || loser.LastUsed > winner.LastUsed))
                    winner.LastUsed = loser.LastUsed;
            });

        result.Keys = MergeById(a.Keys, b.Keys, k => k.Id, k => k.UpdatedAt, result.Tombstones, null);
        result.Identities = MergeById(a.Identities, b.Identities, i => i.Id, i => i.UpdatedAt, result.Tombstones, null);
        result.Snippets = MergeById(a.Snippets, b.Snippets, i => i.Id, i => i.UpdatedAt, result.Tombstones, null);
        result.Forwards = MergeById(a.Forwards, b.Forwards, i => i.Id, i => i.UpdatedAt, result.Tombstones, null);
        result.Proxmox = MergeById(a.Proxmox, b.Proxmox, i => i.Id, i => i.UpdatedAt, result.Tombstones, null);

        foreach (var src in new[] { a.KnownHosts, b.KnownHosts })
            foreach (var (host, fp) in src)
                result.KnownHosts.TryAdd(host, fp);

        // Deterministic order (a dictionary keeps the insertion order when serializing).
        result.KnownHosts = result.KnownHosts.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        result.Tombstones = result.Tombstones.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        return result;
    }

    /// <summary>Stable, comparable representation (for "did something change?").</summary>
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

            // On a tie the synced state (b) wins - what matters is that both sides decide the same way.
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
