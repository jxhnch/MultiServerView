using MultiServerView.Core;
using MultiServerView.Core.Sync;
using Xunit;

namespace MultiServerView.Core.Tests;

/// <summary>The vault lives at one source (no merging): choosing, saving, taking over, conflicts, offline.</summary>
public class SourceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "msv-src-" + Guid.NewGuid().ToString("N"));

    public SourceTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
    }

    private static async Task<Device> MainWithVaultAtSourceAsync(FakeSupabase cloud, params string[] hosts)
    {
        var main = new Device(cloud);
        main.Store.Create("main-password");
        foreach (var h in hosts) main.Store.Upsert(new ServerEntry { Name = h, Host = h });
        await main.Source.MoveHereAsync(Device.Config(cloud), overwrite: false);
        return main;
    }

    private static string[] Names(Device d) => d.Store.List().Select(s => s.Name).Order().ToArray();

    [Fact]
    public async Task Probe_tells_what_is_at_the_target_without_changing_it()
    {
        var cloud = new FakeSupabase();
        using var main = new Device(cloud);
        main.Store.Create("main-password");
        Assert.Equal(ProbeKind.Empty, (await main.Source.ProbeAsync(Device.Config(cloud))).Kind);

        await main.Source.MoveHereAsync(Device.Config(cloud), false);
        var same = await main.Source.ProbeAsync(Device.Config(cloud));
        Assert.Equal((ProbeKind.Vault, true), (same.Kind, same.SameVault));

        using var other = new Device(cloud);
        other.Store.Create("other-password");
        var different = await other.Source.ProbeAsync(Device.Config(cloud));
        Assert.Equal((ProbeKind.Vault, false), (different.Kind, different.SameVault));

        cloud.Blob = Convert.ToBase64String(new byte[60]);
        cloud.Version = 7;
        var junk = cloud.Blob;
        Assert.Equal(ProbeKind.NotAVault, (await other.Source.ProbeAsync(Device.Config(cloud))).Kind);
        Assert.Equal(junk, cloud.Blob);
    }

    [Fact]
    public async Task Moving_a_vault_to_a_source_with_a_vault_needs_an_explicit_overwrite()
    {
        var cloud = new FakeSupabase();
        using var main = await MainWithVaultAtSourceAsync(cloud, "from-main");
        var before = cloud.Blob;

        using var other = new Device(cloud);
        other.Store.Create("other-password");
        var ex = await Assert.ThrowsAsync<SyncException>(() => other.Source.MoveHereAsync(Device.Config(cloud), overwrite: false));
        Assert.Contains("already a vault", ex.Message);
        Assert.Equal(before, cloud.Blob);

        await other.Source.MoveHereAsync(Device.Config(cloud), overwrite: true);
        Assert.NotEqual(before, cloud.Blob);
    }

    [Fact]
    public async Task A_second_device_takes_over_the_vault_and_keeps_a_backup_of_its_own()
    {
        var cloud = new FakeSupabase();
        using var main = await MainWithVaultAtSourceAsync(cloud, "from-main");

        using var second = new Device(cloud);
        second.Store.Create("second-password");
        second.Store.Upsert(new ServerEntry { Name = "only-second", Host = "s" });
        var oldBytes = File.ReadAllBytes(second.Store.FilePath);

        var backup = await second.Source.UseExistingAsync(Device.Config(cloud));

        Assert.False(second.Store.IsUnlocked);
        Assert.Equal(oldBytes, File.ReadAllBytes(backup));
        Assert.Throws<WrongPasswordException>(() => second.Store.Unlock("second-password"));
        second.Store.Unlock("main-password");
        Assert.Equal(new[] { "from-main" }, Names(second));
        Assert.Equal(SourceState.Ready, second.Source.Status.State);
    }

    [Fact]
    public async Task Taking_over_from_an_empty_target_or_a_foreign_file_changes_nothing()
    {
        var cloud = new FakeSupabase();
        using var d = new Device(cloud);
        d.Store.Create("my-password");
        var before = File.ReadAllBytes(d.Store.FilePath);

        await Assert.ThrowsAsync<SyncException>(() => d.Source.UseExistingAsync(Device.Config(cloud)));
        cloud.Blob = Convert.ToBase64String(new byte[60]);
        cloud.Version = 3;
        var ex = await Assert.ThrowsAsync<SyncException>(() => d.Source.UseExistingAsync(Device.Config(cloud)));
        Assert.Contains("not a MultiServerView vault", ex.Message);

        Assert.Equal(before, File.ReadAllBytes(d.Store.FilePath));
        Assert.Empty(Directory.GetFiles(d.Dir, "*.bak-*"));
    }

    [Fact]
    public async Task Changes_travel_both_ways_without_merging()
    {
        var cloud = new FakeSupabase();
        using var main = await MainWithVaultAtSourceAsync(cloud, "one");
        using var second = new Device(cloud);
        await second.Source.UseExistingAsync(Device.Config(cloud));
        second.Store.Unlock("main-password");

        second.Store.Upsert(new ServerEntry { Name = "two", Host = "2" });
        second.Source.MarkDirty();
        Assert.Equal(RefreshOutcome.NoChange, await second.Source.PushAsync());
        Assert.Equal(SourceState.Ready, second.Source.Status.State);

        Assert.Equal(RefreshOutcome.Pulled, await main.Source.RefreshAsync());
        Assert.Equal(new[] { "one", "two" }, Names(main));
        Assert.Equal(RefreshOutcome.NoChange, await main.Source.RefreshAsync());
    }

    [Fact]
    public async Task Both_devices_changed_means_a_question_not_a_silent_overwrite()
    {
        var cloud = new FakeSupabase();
        using var main = await MainWithVaultAtSourceAsync(cloud, "base");
        using var second = new Device(cloud);
        await second.Source.UseExistingAsync(Device.Config(cloud));
        second.Store.Unlock("main-password");

        main.Store.Upsert(new ServerEntry { Name = "from-main", Host = "m" });
        main.Source.MarkDirty();
        await main.Source.PushAsync();
        var atSource = cloud.Blob;

        second.Store.Upsert(new ServerEntry { Name = "from-second", Host = "s" });
        second.Source.MarkDirty();
        Assert.Equal(RefreshOutcome.Conflict, await second.Source.PushAsync());
        Assert.Equal(SourceState.Conflict, second.Source.Status.State);
        Assert.Equal(atSource, cloud.Blob); // nothing was overwritten

        // keep what is at the source: the own version is kept as a backup file, the vault is locked
        var backup = await second.Source.ResolveAsync("source");
        Assert.True(File.Exists(backup));
        Assert.False(second.Store.IsUnlocked);
        second.Store.Unlock("main-password");
        Assert.Equal(new[] { "base", "from-main" }, Names(second));
    }

    [Fact]
    public async Task Keeping_my_version_overwrites_the_source_after_a_conflict()
    {
        var cloud = new FakeSupabase();
        using var main = await MainWithVaultAtSourceAsync(cloud, "base");
        using var second = new Device(cloud);
        await second.Source.UseExistingAsync(Device.Config(cloud));
        second.Store.Unlock("main-password");

        main.Store.Upsert(new ServerEntry { Name = "from-main", Host = "m" });
        main.Source.MarkDirty();
        await main.Source.PushAsync();

        second.Store.Upsert(new ServerEntry { Name = "from-second", Host = "s" });
        second.Source.MarkDirty();
        Assert.Equal(RefreshOutcome.Conflict, await second.Source.RefreshAsync()); // dirty + source changed

        await second.Source.ResolveAsync("mine");
        Assert.Equal(SourceState.Ready, second.Source.Status.State);
        Assert.Equal(RefreshOutcome.Pulled, await main.Source.RefreshAsync());
        Assert.Equal(new[] { "base", "from-second" }, Names(main));
    }

    [Fact]
    public async Task Without_a_connection_changes_wait_and_are_saved_later()
    {
        var cloud = new FakeSupabase();
        using var main = await MainWithVaultAtSourceAsync(cloud, "one");
        var before = cloud.Blob;

        cloud.Down = true;
        main.Store.Upsert(new ServerEntry { Name = "offline-edit", Host = "x" });
        main.Source.MarkDirty();
        Assert.Equal(RefreshOutcome.Offline, await main.Source.PushAsync());
        Assert.Equal(SourceState.Pending, main.Source.Status.State);
        Assert.True(main.Source.Settings.Dirty);
        Assert.Equal(RefreshOutcome.Offline, await main.Source.RefreshAsync());

        cloud.Down = false;
        Assert.Equal(RefreshOutcome.NoChange, await main.Source.RefreshAsync()); // the waiting change is saved now
        Assert.NotEqual(before, cloud.Blob);
        Assert.False(main.Source.Settings.Dirty);
        Assert.Equal(SourceState.Ready, main.Source.Status.State);
    }

    [Fact]
    public async Task A_master_password_change_on_one_device_reaches_the_other()
    {
        var cloud = new FakeSupabase();
        using var main = await MainWithVaultAtSourceAsync(cloud, "one");
        using var second = new Device(cloud);
        await second.Source.UseExistingAsync(Device.Config(cloud));
        second.Store.Unlock("main-password");

        main.Store.ChangePassword("main-password", "brand-new-password");
        await main.Source.PushAsync();

        Assert.Equal(RefreshOutcome.KeyChanged, await second.Source.RefreshAsync());
        Assert.False(second.Store.IsUnlocked);
        Assert.Throws<WrongPasswordException>(() => second.Store.Unlock("main-password"));
        second.Store.Unlock("brand-new-password");
        Assert.Equal(new[] { "one" }, Names(second));
    }

    [Fact]
    public async Task Settings_of_the_old_sync_ask_which_side_is_right_instead_of_guessing()
    {
        var cloud = new FakeSupabase();
        using var main = await MainWithVaultAtSourceAsync(cloud, "one");
        var cfg = main.Source.Settings;
        cfg.LastVersion = 0; // what the old sync left behind
        main.Source.Save(cfg);
        var before = cloud.Blob;

        Assert.Equal(RefreshOutcome.Conflict, await main.Source.RefreshAsync());
        Assert.Equal(before, cloud.Blob);

        await main.Source.ResolveAsync("mine");
        Assert.Equal(RefreshOutcome.NoChange, await main.Source.RefreshAsync());
    }

    [Fact]
    public async Task A_folder_works_as_a_source_too()
    {
        var cloud = new FakeSupabase();
        using var main = new Device(cloud);
        main.Store.Create("main-password");
        main.Store.Upsert(new ServerEntry { Name = "in-folder", Host = "f" });
        var cfg = new SyncSettings { Mode = SyncSettings.ModeFolder, Folder = _folder };

        await main.Source.MoveHereAsync(cfg, false);
        Assert.True(File.Exists(Path.Combine(_folder, FolderRemote.FileName)));

        using var second = new Device(cloud);
        await second.Source.UseExistingAsync(cfg);
        second.Store.Unlock("main-password");
        Assert.Equal(new[] { "in-folder" }, Names(second));

        second.Store.Upsert(new ServerEntry { Name = "added", Host = "a" });
        second.Source.MarkDirty();
        await second.Source.PushAsync();
        Assert.Equal(RefreshOutcome.Pulled, await main.Source.RefreshAsync());
        Assert.Equal(new[] { "added", "in-folder" }, Names(main));
    }

    [Fact]
    public async Task Going_back_to_this_PC_keeps_the_vault_and_leaves_the_source_alone()
    {
        var cloud = new FakeSupabase();
        using var main = await MainWithVaultAtSourceAsync(cloud, "one");
        var atSource = cloud.Blob;

        main.Source.Disconnect();
        Assert.Equal(SourceState.Local, main.Source.Status.State);
        Assert.False(main.Source.Settings.IsConfigured);
        Assert.Equal(new[] { "one" }, Names(main));
        Assert.Equal(atSource, cloud.Blob);
    }

    [Fact]
    public async Task Local_backups_are_limited_to_the_newest_ten()
    {
        var cloud = new FakeSupabase();
        using var d = new Device(cloud);
        d.Store.Create("my-password");
        for (var i = 0; i < 14; i++) d.Store.BackupLocal();
        Assert.Equal(10, Directory.GetFiles(d.Dir, "vault.msv.bak-*").Length);
        await Task.CompletedTask;
    }
}
