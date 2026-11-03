using MultiServerView.Core;
using Xunit;

namespace MultiServerView.Core.Tests;

public class MergeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static ServerEntry S(string id, string name, int minutes, DateTimeOffset? lastUsed = null) =>
        new() { Id = id, Name = name, Host = "h", Port = 22, UpdatedAt = T0.AddMinutes(minutes), LastUsed = lastUsed };

    [Fact]
    public void Union_of_distinct_entries()
    {
        var a = new VaultData { Servers = { S("1", "a", 0) } };
        var b = new VaultData { Servers = { S("2", "b", 0) } };

        var m = VaultMerge.Merge(a, b);

        Assert.Equal(new[] { "1", "2" }, m.Servers.Select(s => s.Id));
    }

    [Fact]
    public void Newer_update_wins_in_both_directions()
    {
        var older = new VaultData { Servers = { S("1", "alt", 1) } };
        var newer = new VaultData { Servers = { S("1", "neu", 5) } };

        Assert.Equal("neu", VaultMerge.Merge(older, newer).Servers.Single().Name);
        Assert.Equal("neu", VaultMerge.Merge(newer, older).Servers.Single().Name);
    }

    [Fact]
    public void Newer_delete_beats_older_edit()
    {
        var edited = new VaultData { Servers = { S("1", "a", 1) } };
        var deleted = new VaultData { Tombstones = { ["1"] = T0.AddMinutes(10) } };

        Assert.Empty(VaultMerge.Merge(edited, deleted).Servers);
        Assert.Empty(VaultMerge.Merge(deleted, edited).Servers);
    }

    [Fact]
    public void Newer_edit_beats_older_delete()
    {
        var edited = new VaultData { Servers = { S("1", "a", 20) } };
        var deleted = new VaultData { Tombstones = { ["1"] = T0.AddMinutes(10) } };

        Assert.Single(VaultMerge.Merge(edited, deleted).Servers);
    }

    [Fact]
    public void LastUsed_takes_the_latest_value_regardless_of_winner()
    {
        var a = new VaultData { Servers = { S("1", "neu", 10, T0.AddMinutes(1)) } };
        var b = new VaultData { Servers = { S("1", "alt", 1, T0.AddMinutes(99)) } };

        var merged = VaultMerge.Merge(a, b).Servers.Single();

        Assert.Equal("neu", merged.Name);
        Assert.Equal(T0.AddMinutes(99), merged.LastUsed);
    }

    [Fact]
    public void Merge_is_deterministic_and_does_not_mutate_inputs()
    {
        var a = new VaultData { Servers = { S("2", "b", 1), S("1", "a", 1) }, KnownHosts = { ["z:22"] = "x", ["a:22"] = "y" } };
        var b = new VaultData { Servers = { S("3", "c", 1) } };
        var before = VaultMerge.Fingerprint(a);

        var ab = VaultMerge.Merge(a, b);
        var ba = VaultMerge.Merge(b, a);

        Assert.Equal(VaultMerge.Fingerprint(ab), VaultMerge.Fingerprint(ba));
        Assert.Equal(new[] { "1", "2", "3" }, ab.Servers.Select(s => s.Id));
        Assert.Equal(before, VaultMerge.Fingerprint(a));
    }

    [Fact]
    public void KnownHosts_are_unioned()
    {
        var a = new VaultData { KnownHosts = { ["a:22"] = "1" } };
        var b = new VaultData { KnownHosts = { ["b:22"] = "2" } };

        Assert.Equal(2, VaultMerge.Merge(a, b).KnownHosts.Count);
    }
}
