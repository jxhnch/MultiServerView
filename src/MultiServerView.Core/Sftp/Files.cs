namespace MultiServerView.Core.Sftp;

/// <summary>An entry in a file list (local or on the server).</summary>
public sealed record FileEntry(string Name, string Path, bool IsDir, long Size, DateTimeOffset Modified, string Mode);

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
}
