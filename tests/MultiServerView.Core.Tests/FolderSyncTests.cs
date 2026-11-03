using MultiServerView.Core;
using MultiServerView.Core.Sync;
using Xunit;

namespace MultiServerView.Core.Tests;

/// <summary>Sync through a shared folder (OneDrive, iCloud, Syncthing …) instead of Supabase.</summary>
public class FolderSyncTests : IDisposable
{
    private readonly string _shared = Path.Combine(Path.GetTempPath(), "msv-folder-" + Guid.NewGuid().ToString("N"));

    public FolderSyncTests() => Directory.CreateDirectory(_shared);

    public void Dispose()
    {
        if (Directory.Exists(_shared)) Directory.Delete(_shared, true);
    }

    private SyncSettings Config() => new() { Mode = SyncSettings.ModeFolder, Folder = _shared };

    private static Device NewDevice() => new(new FakeSupabase());

    [Fact]
    public async Task Two_devices_sync_through_a_folder_and_the_file_is_encrypted()
    {
        using var a = NewDevice();
        using var b = NewDevice();

        a.Store.Create("master-password");
        a.Store.Upsert(new ServerEntry { Name = "web-1", Host = "h", Password = "secret" });
        a.Sync.Save(Config());

        Assert.Equal(SyncOutcome.Created, (await a.Sync.SyncAsync()).Outcome);
        var file = Path.Combine(_shared, FolderRemote.FileName);
        Assert.True(File.Exists(file));
        var text = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(file));
        Assert.DoesNotContain("secret", text);
        Assert.DoesNotContain("web-1", text);
        Assert.Empty(Directory.GetFiles(_shared, "*.tmp")); // no helper files left

        await b.Sync.DownloadInitialAsync(Config());
        b.Store.Unlock("master-password");
        Assert.Equal("secret", b.Store.List().Single().Password);
    }

    [Fact]
    public async Task Changes_on_both_devices_are_merged_through_the_folder()
    {
        using var a = NewDevice();
        using var b = NewDevice();

        a.Store.Create("master-password");
        a.Sync.Save(Config());
        await a.Sync.SyncAsync();
        await b.Sync.DownloadInitialAsync(Config());
        b.Store.Unlock("master-password");
        b.Sync.Save(Config());

        a.Store.Upsert(new ServerEntry { Name = "from-a", Host = "a" });
        b.Store.Upsert(new ServerEntry { Name = "from-b", Host = "b" });

        Assert.Equal(SyncOutcome.Pushed, (await a.Sync.SyncAsync()).Outcome);
        Assert.Equal(SyncOutcome.Merged, (await b.Sync.SyncAsync()).Outcome);
        Assert.Equal(SyncOutcome.Pulled, (await a.Sync.SyncAsync()).Outcome);
        Assert.Equal(new[] { "from-a", "from-b" }, a.Store.List().Select(s => s.Name).Order().ToArray());
        Assert.Equal(SyncOutcome.UpToDate, (await b.Sync.SyncAsync()).Outcome);
    }

    [Fact]
    public async Task Update_detects_a_change_made_by_another_device()
    {
        var remote = new FolderRemote(_shared);
        var first = await remote.InsertAsync(new byte[] { 1, 2, 3 });

        Assert.NotNull(await remote.UpdateAsync(first, new byte[] { 4, 5, 6 })); // fits: the version matches
        Assert.Null(await remote.UpdateAsync(first, new byte[] { 7, 8, 9 }));    // outdated: someone was faster
        Assert.Equal(new byte[] { 4, 5, 6 }, (await remote.GetAsync())!.Blob);
    }

    [Fact]
    public async Task Missing_folder_and_missing_vault_give_clear_errors()
    {
        using var a = NewDevice();
        var gone = new SyncSettings { Mode = SyncSettings.ModeFolder, Folder = Path.Combine(_shared, "does-not-exist") };

        await Assert.ThrowsAsync<SyncException>(() => a.Sync.TestConnectionAsync(gone));
        await Assert.ThrowsAsync<SyncException>(() => a.Sync.DownloadInitialAsync(Config())); // folder empty
        await a.Sync.TestConnectionAsync(Config()); // exists and is writable: no error
    }

    [Fact]
    public void Settings_remember_the_mode()
    {
        var path = Path.Combine(_shared, "sync.json");
        var store = new SyncSettingsStore(path, new PlainProtector());
        store.Save(Config());

        var loaded = store.Load();
        Assert.True(loaded.IsFolder);
        Assert.True(loaded.IsConfigured);
        Assert.Equal(_shared, loaded.Folder);

        // Old settings without a mode still count as Supabase.
        File.WriteAllText(path, """{"url":"https://x.supabase.co","anonKey":"k","email":"e@x.ch","passwordProtected":""}""");
        Assert.False(store.Load().IsFolder);
    }

    [Theory]
    [InlineData("MultiServerView-Vault-LAPTOP-B.msv")]                                    // OneDrive
    [InlineData("MultiServerView-Vault 2.msv")]                                           // iCloud
    [InlineData("MultiServerView-Vault (B's conflicted copy 2026-11-03).msv")]            // Dropbox
    [InlineData("MultiServerView-Vault.sync-conflict-20261103-120000-ABCDEFG.msv")]       // Syncthing
    public async Task Conflict_copy_from_another_device_is_merged_and_archived(string copyName)
    {
        using var a = NewDevice();
        using var b = NewDevice();

        a.Store.Create("master-password");
        a.Sync.Save(Config());
        await a.Sync.SyncAsync();
        await b.Sync.DownloadInitialAsync(Config());
        b.Store.Unlock("master-password");

        // Both change offline; the folder service puts B's state next to the main file as a conflict copy.
        a.Store.Upsert(new ServerEntry { Name = "from-a", Host = "a" });
        b.Store.Upsert(new ServerEntry { Name = "from-b", Host = "b" });
        File.WriteAllBytes(Path.Combine(_shared, copyName), b.Store.ExportBlob());

        Assert.Equal(SyncOutcome.Merged, (await a.Sync.SyncAsync()).Outcome);
        Assert.Equal(new[] { "from-a", "from-b" }, a.Store.List().Select(x => x.Name).Order().ToArray());

        // The copy is moved away (not deleted) and the main file contains both changes.
        Assert.False(File.Exists(Path.Combine(_shared, copyName)));
        Assert.Single(Directory.GetFiles(Path.Combine(_shared, FolderRemote.ArchiveFolder)));
        Assert.Equal(SyncOutcome.Pulled, (await b.Sync.SyncAsync()).Outcome);
        Assert.Equal(2, b.Store.List().Count);
    }

    [Fact]
    public async Task Foreign_or_broken_copies_are_left_alone()
    {
        using var a = NewDevice();
        a.Store.Create("master-password");
        a.Sync.Save(Config());
        await a.Sync.SyncAsync();

        // Different vault (different master password) and a broken file.
        using var other = NewDevice();
        other.Store.Create("totally-different-password");
        var foreign = Path.Combine(_shared, "MultiServerView-Vault-FREMD.msv");
        var broken = Path.Combine(_shared, "MultiServerView-Vault-KAPUTT.msv");
        File.WriteAllBytes(foreign, other.Store.ExportBlob());
        File.WriteAllBytes(broken, new byte[] { 1, 2, 3 });

        Assert.Equal(SyncOutcome.UpToDate, (await a.Sync.SyncAsync()).Outcome);
        Assert.True(File.Exists(foreign));
        Assert.True(File.Exists(broken));
    }
}
