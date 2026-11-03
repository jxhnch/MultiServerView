using System.Security.Cryptography;

namespace MultiServerView.Core.Sync;

/// <summary>A place where the encrypted vault blob is stored (Supabase or a folder).</summary>
public interface ISyncRemote
{
    /// <summary>Current state, or null if nothing is stored there yet.</summary>
    Task<RemoteVault?> GetAsync(CancellationToken ct = default);

    /// <summary>Stores the blob for the first time and returns the new version.</summary>
    Task<long> InsertAsync(byte[] blob, CancellationToken ct = default);

    /// <summary>Replaces the blob if it still has the expected version; null = someone was faster.</summary>
    Task<long?> UpdateAsync(long expectedVersion, byte[] blob, CancellationToken ct = default);
}

/// <summary>A copy of the vault that a folder service created when two devices changed it at the same time.</summary>
public sealed record ConflictCopy(string Path, byte[] Blob);

/// <summary>A store that knows conflict copies from other devices (folder services create them on simultaneous changes).</summary>
public interface IConflictAware
{
    Task<IReadOnlyList<ConflictCopy>> GetConflictCopiesAsync(CancellationToken ct = default);

    /// <summary>Moves an imported copy out of the way (into a subfolder, not deleted).</summary>
    void Archive(ConflictCopy copy);
}

/// <summary>Supabase as the store: signs in on the first access and uses the same token afterwards.</summary>
public sealed class SupabaseRemote : ISyncRemote
{
    private readonly SupabaseClient _client;
    private readonly string _email;
    private readonly string _password;
    private string? _token;

    public SupabaseRemote(SupabaseClient client, string email, string password)
    {
        _client = client;
        _email = email;
        _password = password;
    }

    private async Task<string> TokenAsync(CancellationToken ct) => _token ??= await _client.SignInAsync(_email, _password, ct);

    public async Task<RemoteVault?> GetAsync(CancellationToken ct = default) => await _client.GetVaultAsync(await TokenAsync(ct), ct);

    public async Task<long> InsertAsync(byte[] blob, CancellationToken ct = default) =>
        await _client.InsertVaultAsync(await TokenAsync(ct), blob, ct);

    public async Task<long?> UpdateAsync(long expectedVersion, byte[] blob, CancellationToken ct = default) =>
        await _client.UpdateVaultAsync(await TokenAsync(ct), expectedVersion, blob, ct);
}

/// <summary>
/// A folder as the store, e.g. in OneDrive, iCloud Drive, Dropbox or via Syncthing. The folder service brings the file
/// to the other devices; the app merges the states. The file is encrypted exactly like the local vault.
/// The "version" is a hash of the content, which is how the app notices that another device changed the file since it was read.
/// </summary>
public sealed class FolderRemote : ISyncRemote, IConflictAware
{
    public const string FileName = "MultiServerView-Vault.msv";

    /// <summary>Subfolder that imported conflict copies are moved into.</summary>
    public const string ArchiveFolder = "Conflict copies (imported)";

    private readonly string _folder;

    public FolderRemote(string folder)
    {
        _folder = folder.Trim();
        if (_folder.Length == 0) throw new SyncException("Please choose a sync folder.");
    }

    public string FilePath => Path.Combine(_folder, FileName);

    public async Task<RemoteVault?> GetAsync(CancellationToken ct = default)
    {
        EnsureFolder();
        if (!File.Exists(FilePath)) return null;
        var blob = await ReadAsync(ct);
        return new RemoteVault(VersionOf(blob), blob);
    }

    public async Task<long> InsertAsync(byte[] blob, CancellationToken ct = default)
    {
        EnsureFolder();
        await WriteAsync(blob, ct);
        return VersionOf(blob);
    }

    public async Task<long?> UpdateAsync(long expectedVersion, byte[] blob, CancellationToken ct = default)
    {
        EnsureFolder();
        if (File.Exists(FilePath) && VersionOf(await ReadAsync(ct)) != expectedVersion) return null;
        await WriteAsync(blob, ct);
        return VersionOf(blob);
    }

    /// <summary>
    /// Finds copies of the vault that folder services create on conflicts: OneDrive "MultiServerView-Vault-PCNAME.msv",
    /// iCloud "… 2.msv", Dropbox "… (conflicted copy …).msv", Syncthing "….sync-conflict-….msv". Everything that starts with the
    /// name of the vault file and ends with .msv.
    /// </summary>
    public async Task<IReadOnlyList<ConflictCopy>> GetConflictCopiesAsync(CancellationToken ct = default)
    {
        EnsureFolder();
        var stem = Path.GetFileNameWithoutExtension(FileName);
        var result = new List<ConflictCopy>();
        foreach (var path in Directory.EnumerateFiles(_folder, stem + "*.msv"))
        {
            if (string.Equals(Path.GetFileName(path), FileName, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                result.Add(new ConflictCopy(path, await File.ReadAllBytesAsync(path, ct)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still locked (being synchronized right now): try again on the next sync.
            }
        }
        return result;
    }

    public void Archive(ConflictCopy copy)
    {
        try
        {
            var dir = Path.Combine(_folder, ArchiveFolder);
            Directory.CreateDirectory(dir);
            File.Move(copy.Path, Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Path.GetFileName(copy.Path)}"), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Stays where it is and is read again on the next sync - harmless because merging is repeatable.
        }
    }

    /// <summary>Checks whether the folder exists and is writable.</summary>
    public void CheckWritable()
    {
        EnsureFolder();
        var probe = Path.Combine(_folder, $".msv-test-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(probe, "test");
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SyncException("Cannot write to the sync folder: " + ex.Message, ex);
        }
    }

    private void EnsureFolder()
    {
        if (!Directory.Exists(_folder))
            throw new SyncException($"The sync folder is not reachable: {_folder}");
    }

    private static long VersionOf(byte[] blob) => BitConverter.ToInt64(SHA256.HashData(blob), 0);

    /// <summary>Reading with a few retries: sync programs lock the file briefly while they upload it.</summary>
    private async Task<byte[]> ReadAsync(CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await File.ReadAllBytesAsync(FilePath, ct);
            }
            catch (IOException) when (attempt < 4)
            {
                await Task.Delay(300, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new SyncException("The vault file in the sync folder cannot be read: " + ex.Message, ex);
            }
        }
    }

    /// <summary>Writes to a helper file first and then replaces the real one - so no other device sees a half-written file.</summary>
    private async Task WriteAsync(byte[] blob, CancellationToken ct)
    {
        var temp = Path.Combine(_folder, $".{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temp, blob, ct);
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temp); } catch (Exception) { /* cleaning up is only a best effort */ }
            throw new SyncException("Cannot write to the sync folder: " + ex.Message, ex);
        }
    }
}
