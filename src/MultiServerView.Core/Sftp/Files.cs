namespace MultiServerView.Core.Sftp;

/// <summary>An entry in a file list (local or on the server).</summary>
public sealed record FileEntry(string Name, string Path, bool IsDir, long Size, DateTimeOffset Modified, string Mode, bool IsLink = false);

/// <summary>Details shown in the properties dialog (local or on the server).</summary>
public sealed record FileStat(string Name, string Path, bool IsDir, bool IsLink, long Size, int Items, int Permissions, string Mode, uint Owner, uint Group,
    DateTimeOffset Modified, DateTimeOffset Accessed);

public sealed record BrowseResult(string Path, string Parent, List<FileEntry> Entries);

public sealed record TransferProgress(string Name, long Done, long Total, bool Finished);

/// <summary>Local file system for the left SFTP pane.</summary>
public static class LocalFs
{
    public static string Home() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Lists a folder (folders first). Empty path = drives (Windows) or "/".</summary>
    public static BrowseResult Browse(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            if (!OperatingSystem.IsWindows()) path = "/";
            else
            {
                var drives = DriveInfo.GetDrives()
                    .Where(d => { try { return d.IsReady; } catch (IOException) { return false; } })
                    .Select(d => new FileEntry(d.Name, d.Name, true, 0, DateTimeOffset.MinValue, ""))
                    .ToList();
                return new BrowseResult("", "", drives);
            }
        }

        var full = Path.GetFullPath(path!);
        var dir = new DirectoryInfo(full);
        if (!dir.Exists) throw new DirectoryNotFoundException("Folder not found: " + full);

        var entries = new List<FileEntry>();
        try
        {
            foreach (var info in dir.EnumerateFileSystemInfos())
            {
                try
                {
                    var isDir = (info.Attributes & FileAttributes.Directory) != 0;
                    entries.Add(new FileEntry(info.Name, info.FullName, isDir, isDir ? 0 : ((FileInfo)info).Length,
                        new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), ""));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* skip the entry */ }
            }
        }
        catch (UnauthorizedAccessException)
        {
            throw new UnauthorizedAccessException("No access to this folder.");
        }

        entries = entries.OrderByDescending(e => e.IsDir).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var parent = dir.Parent?.FullName ?? "";
        return new BrowseResult(dir.FullName, parent, entries);
    }

    public static void MakeDir(string path) => Directory.CreateDirectory(path);

    public static void CreateFile(string path)
    {
        if (File.Exists(path) || Directory.Exists(path)) throw new IOException("\"" + Path.GetFileName(path) + "\" already exists.");
        using (File.Create(path)) { }
    }

    public static void Rename(string from, string to)
    {
        if (File.Exists(to) || Directory.Exists(to)) throw new IOException("\"" + Path.GetFileName(to) + "\" already exists.");
        if (Directory.Exists(from)) Directory.Move(from, to);
        else File.Move(from, to);
    }

    /// <summary>Size, item count and dates. For folders the size is summed up over everything inside.</summary>
    public static Task<FileStat> StatAsync(string path, CancellationToken ct = default) => Task.Run(() =>
    {
        var isDir = Directory.Exists(path);
        FileSystemInfo info = isDir ? new DirectoryInfo(path) : new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("Not found: " + path);

        long size = 0;
        var items = 0;
        if (isDir)
        {
            foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }))
            {
                ct.ThrowIfCancellationRequested();
                size += f.Length;
                items++;
            }
        }
        else size = ((FileInfo)info).Length;

        return new FileStat(info.Name, info.FullName, isDir, info.LinkTarget != null, size, items, -1, info.Attributes.ToString(), 0, 0,
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), new DateTimeOffset(info.LastAccessTimeUtc, TimeSpan.Zero));
    }, ct);

    public static async Task<string> ChecksumAsync(string path, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
    }
}
