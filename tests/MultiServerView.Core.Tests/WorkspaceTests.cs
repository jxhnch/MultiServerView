using MultiServerView.Core;
using MultiServerView.Core.Import;
using Xunit;

namespace MultiServerView.Core.Tests;

public class WorkspaceTests
{
    [Fact]
    public void Csv_import_reads_workspace_column()
    {
        var r = CsvImporter.Parse("name,host,workspace\nvm-1,10.0.0.1,Miller\nvm-2,10.0.0.2,Smith\nvm-3,10.0.0.3,\n");

        Assert.Equal(new[] { "Miller", "Smith", "" }, r.Servers.Select(s => s.Workspace).ToArray());
    }

    [Fact]
    public void Workspace_survives_save_and_unlock_and_old_entries_have_none()
    {
        var path = Path.Combine(Path.GetTempPath(), "msv-ws-" + Guid.NewGuid().ToString("N"), "vault.msv");
        try
        {
            var store = new VaultStore(path);
            store.Create("master-password");
            store.Upsert(new ServerEntry { Name = "vm-1", Host = "10.0.0.1", Workspace = "Miller" });
            store.Upsert(new ServerEntry { Name = "vm-2", Host = "10.0.0.2" });

            var again = new VaultStore(path);
            again.Unlock("master-password");
            var byName = again.List().ToDictionary(s => s.Name);
            Assert.Equal("Miller", byName["vm-1"].Workspace);
            Assert.Equal("", byName["vm-2"].Workspace);
        }
        finally
        {
            var dir = Path.GetDirectoryName(path)!;
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Merge_keeps_the_newer_workspace_assignment()
    {
        var older = new ServerEntry { Id = "a", Name = "vm", Host = "h", Workspace = "Miller", UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5) };
        var newer = new ServerEntry { Id = "a", Name = "vm", Host = "h", Workspace = "Smith", UpdatedAt = DateTimeOffset.UtcNow };
        var local = new VaultData { Servers = { older } };
        var remote = new VaultData { Servers = { newer } };

        Assert.Equal("Smith", VaultMerge.Merge(local, remote).Servers.Single().Workspace);
        Assert.Equal("Smith", VaultMerge.Merge(remote, local).Servers.Single().Workspace);
    }
}
