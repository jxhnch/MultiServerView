using System.Security.Cryptography;
using Renci.SshNet;

namespace MultiServerView.Core.Sftp;

public sealed partial class SftpSession
{
    /// <summary>
    /// Copies a file or a whole folder from this server straight to another one. The data passes through this PC but is never
    /// written to its disk. Files are replaced safely (see SafeWrite.cs). Links are not followed; returns how many entries
    /// were left out.
    /// </summary>
    public async Task<int> CopyToAsync(SftpSession destination, string sourcePath, string destinationDir, Action<TransferProgress> progress, CancellationToken ct)
    {
        var source = Client;
        var target = destination.Client;
        sourcePath = sourcePath.Length > 1 ? sourcePath.TrimEnd('/') : sourcePath;
        var name = SafeRemoteName(sourcePath.Split('/').Last());
        var rootTarget = Combine(destinationDir, name);

        var skipped = 0;
        var files = await Task.Run(() =>
        {
            RefuseSameFolder(source, target, sourcePath, destinationDir);
            var attributes = source.GetAttributes(sourcePath);
            var list = new List<(string From, string To, long Size, int Mode)>();
            var folders = new List<string>();
            if (attributes.IsDirectory)
            {
                folders.Add(rootTarget);
                CollectForCopy(source, sourcePath, rootTarget, list, folders, ref skipped, ct, 0);
            }
            else list.Add((sourcePath, rootTarget, attributes.Size, PermissionBits(attributes)));
            foreach (var folder in folders)
                if (!target.Exists(folder)) target.CreateDirectory(folder);
            return list;
        }, ct);

        var total = files.Sum(f => f.Size);
        long done = 0;
        var cache = new FolderCache();
        progress(new TransferProgress(name, 0, total, false));
        foreach (var (from, to, size, mode) in files)
        {
            ct.ThrowIfCancellationRequested();
            var before = done;
            // a replaced file keeps its permissions, a new one gets those of the original (so scripts stay executable)
            await SafeUploadAsync(target, () => source.OpenRead(from), to, mode, cache,
                sent => progress(new TransferProgress(name, before + sent, total, false)), ct);
            done += size;
        }
        progress(new TransferProgress(name, total, total, true));
        return skipped;
    }

    private static string SafeRemoteName(string name)
        => name.Length == 0 || name is "." or ".." || name.Contains('/') || name.Contains('\0')
            ? throw new InvalidOperationException("Unsafe file name: " + name)
            : name;

    /// <summary>
    /// Copying a thing onto itself or into itself would end badly (a "move" would even delete it). Equal paths are normal on two
    /// different servers, so it is checked whether both connections really see the same folder: also when the two sides are
    /// two entries for one machine.
    /// </summary>
    private static void RefuseSameFolder(SftpClient source, SftpClient target, string sourcePath, string destinationDir)
    {
        var folder = destinationDir.Length > 1 ? destinationDir.TrimEnd('/') : destinationDir;
        var overlaps = folder == ParentOf(sourcePath) || folder == sourcePath || folder.StartsWith(sourcePath + "/", StringComparison.Ordinal);
        if (!overlaps) return;
        var probe = Combine(folder, ".msv-probe-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant());
        using (target.Create(probe)) { }
        try
        {
            if (source.Exists(probe))
                throw new InvalidOperationException("Both sides show the same folder (or the target is inside what you copy). Open another folder on the other side.");
        }
        finally
        {
            target.DeleteFile(probe);
        }
    }

    private static void CollectForCopy(SftpClient source, string folder, string targetFolder, List<(string, string, long, int)> files, List<string> folders, ref int skipped, CancellationToken ct, int depth)
    {
        if (depth > 64) { skipped++; return; }
        foreach (var f in source.ListDirectory(folder))
        {
            ct.ThrowIfCancellationRequested();
            if (f.Name is "." or "..") continue;
            if (f.IsSymbolicLink) { skipped++; continue; }
            var to = Combine(targetFolder, SafeRemoteName(f.Name));
            if (f.IsDirectory)
            {
                folders.Add(to);
                CollectForCopy(source, f.FullName, to, files, folders, ref skipped, ct, depth + 1);
            }
            else files.Add((f.FullName, to, f.Length, PermissionBits(f.Attributes)));
        }
    }
}
