namespace MultiServerView.Core.Sftp;

/// <summary>Ein Eintrag in einer Dateiliste (lokal oder auf dem Server).</summary>
public sealed record FileEntry(string Name, string Path, bool IsDir, long Size, DateTimeOffset Modified, string Mode);

public sealed record BrowseResult(string Path, string Parent, List<FileEntry> Entries);

public sealed record TransferProgress(string Name, long Done, long Total, bool Finished);

/// <summary>Lokales Dateisystem für das linke SFTP-Fenster.</summary>
public static class LocalFs
{
    public static string Home() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Listet einen Ordner (Ordner zuerst). Leerer Pfad = Laufwerke (Windows) bzw. "/".</summary>
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
        if (!dir.Exists) throw new DirectoryNotFoundException("Ordner nicht gefunden: " + full);

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
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Eintrag überspringen */ }
            }
        }
        catch (UnauthorizedAccessException)
        {
            throw new UnauthorizedAccessException("Kein Zugriff auf diesen Ordner.");
        }

        entries = entries.OrderByDescending(e => e.IsDir).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var parent = dir.Parent?.FullName ?? "";
        return new BrowseResult(dir.FullName, parent, entries);
    }

    public static void MakeDir(string path) => Directory.CreateDirectory(path);
}
