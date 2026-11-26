using System.Security.Cryptography;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace MultiServerView.Core.Sftp;

public sealed partial class SftpSession
{
    /// <summary>
    /// Copies a file or a whole folder from this server straight to another one. The data passes through this PC but is never
    /// written to its disk. Every file is first written next to its target and only then put in its place, so a cancelled or
    /// failed copy does not damage a file that was already there. Links are not followed.
    /// </summary>
    public async Task CopyToAsync(SftpSession destination, string sourcePath, string destinationDir, Action<TransferProgress> progress, CancellationToken ct)
    {
        var source = Client;
        var target = destination.Client;
        sourcePath = sourcePath.Length > 1 ? sourcePath.TrimEnd('/') : sourcePath;
        var name = SafeRemoteName(sourcePath.Split('/').Last());
        var rootTarget = Combine(destinationDir, name);

        var files = await Task.Run(() =>
        {
            RefuseSameFolder(source, target, sourcePath, destinationDir);
            var attributes = source.GetAttributes(sourcePath);
            var list = new List<(string From, string To, long Size, int Mode)>();
            var folders = new List<string>();
            if (attributes.IsDirectory)
            {
                folders.Add(rootTarget);
                CollectForCopy(source, sourcePath, rootTarget, list, folders, ct, 0);
            }
            else list.Add((sourcePath, rootTarget, attributes.Size, PermissionBits(attributes)));
            foreach (var folder in folders)
                if (!target.Exists(folder)) target.CreateDirectory(folder);
            return list;
        }, ct);

        var total = files.Sum(f => f.Size);
        long done = 0;
        progress(new TransferProgress(name, 0, total, false));
        foreach (var (from, to, size, mode) in files)
        {
            ct.ThrowIfCancellationRequested();
            var before = done;
            var temporary = to + ".msv-part-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
            try
            {
                await using (var input = await Task.Run(() => source.OpenRead(from), ct))
                    await target.UploadFileAsync(input, temporary, true,
                        new Progress<UploadFileProgressReport>(r => progress(new TransferProgress(name, before + (long)r.TotalBytesUploaded, total, false))), ct);
                await Task.Run(() => PutInPlace(target, temporary, to, mode), ct);
            }
            catch
            {
                try { if (target.Exists(temporary)) target.DeleteFile(temporary); }
                catch (Exception) { /* the connection may be gone; the leftover is clearly named */ }
                throw;
            }
            done += size;
        }
        progress(new TransferProgress(name, total, total, true));
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

    private static void CollectForCopy(SftpClient source, string folder, string targetFolder, List<(string, string, long, int)> files, List<string> folders, CancellationToken ct, int depth)
    {
        if (depth > 64) return;
        foreach (var f in source.ListDirectory(folder))
        {
            ct.ThrowIfCancellationRequested();
            if (f.Name is "." or ".." || f.IsSymbolicLink) continue;
            var to = Combine(targetFolder, SafeRemoteName(f.Name));
            if (f.IsDirectory)
            {
                folders.Add(to);
                CollectForCopy(source, f.FullName, to, files, folders, ct, depth + 1);
            }
            else files.Add((f.FullName, to, f.Length, PermissionBits(f.Attributes)));
        }
    }

    private static void PutInPlace(SftpClient target, string temporary, string final, int sourceMode)
    {
        SftpFileAttributes? existing = target.Exists(final) ? target.GetAttributes(final) : null;
        if (existing is { IsDirectory: true }) throw new InvalidOperationException($"\"{final}\" is a folder on the target.");
        try
        {
            target.RenameFile(temporary, final, isPosix: true); // replaces in one step where the server supports it
        }
        catch (Exception ex) when (ex is NotSupportedException or Renci.SshNet.Common.SshException)
        {
            if (existing != null) target.DeleteFile(final);
            target.RenameFile(temporary, final);
        }
        // a replaced file keeps its permissions, a new one gets those of the original (so scripts stay executable)
        var mode = existing != null ? PermissionBits(existing) : sourceMode;
        try { target.ChangePermissions(final, short.Parse(Convert.ToString(mode, 8))); }
        catch (Exception ex) when (ex is Renci.SshNet.Common.SshException or NotSupportedException) { /* some servers do not allow it */ }
    }
}
