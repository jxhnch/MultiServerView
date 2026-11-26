using System.Security.Cryptography;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace MultiServerView.Core.Sftp;

/// <summary>
/// Writing a file without damaging the one that is already there: the data goes into a new file next to the target, which
/// takes its place only when it is complete. A cancelled or broken transfer leaves the old file as it was.
/// </summary>
public sealed partial class SftpSession
{
    private static string PartName(string fileName)
        => "." + (fileName.Length > 60 ? fileName[..60] : fileName) + ".msv-part-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();

    /// <summary>What is already in the target folders of one transfer (each folder is listed once).</summary>
    private sealed class FolderCache
    {
        private readonly Dictionary<string, Dictionary<string, ISftpFile>?> _folders = new(StringComparer.Ordinal);

        /// <summary>The entry itself (a link stays a link). <c>Known</c> is false if the folder cannot be listed.</summary>
        public (bool Known, ISftpFile? Entry) Find(SftpClient client, string path)
        {
            var folder = ParentOf(path);
            if (!_folders.TryGetValue(folder, out var entries))
            {
                try { entries = client.ListDirectory(folder).ToDictionary(f => f.Name, StringComparer.Ordinal); }
                catch (SftpPermissionDeniedException) { entries = null; }
                _folders[folder] = entries;
            }
            return entries == null ? (false, null) : (true, entries.GetValueOrDefault(path.Split('/').Last()));
        }
    }

    /// <param name="open">Opens the source again (it may be needed twice).</param>
    /// <param name="newFileMode">Permissions of a file that did not exist yet; null = what the server gives new files.</param>
    private static async Task SafeUploadAsync(SftpClient target, Func<Stream> open, string final, int? newFileMode, FolderCache cache,
        Action<long> progress, CancellationToken ct)
    {
        async Task Send(string path)
        {
            await using var input = await Task.Run(open, ct);
            await target.UploadFileAsync(input, path, true, new Progress<UploadFileProgressReport>(r => progress((long)r.TotalBytesUploaded)), ct);
        }

        var (known, existing) = await Task.Run(() => cache.Find(target, final), ct);
        if (existing is { IsDirectory: true }) throw new InvalidOperationException($"\"{final}\" is a folder on the target.");

        // Writing into the existing file is the only way to keep it what it is: a link, or a file with special bits.
        if (!known || existing is { IsSymbolicLink: true } || existing is { } e && HasSpecialBits(e.Attributes))
        {
            await Send(final);
            return;
        }

        var temporary = Combine(ParentOf(final), PartName(final.Split('/').Last()));
        try
        {
            try { await Send(temporary); }
            catch (SftpPermissionDeniedException) when (existing != null)
            {
                await Send(final); // the folder does not allow new files, the file itself may still be writable
                return;
            }

            if (existing != null)
            {
                // A new file belongs to whoever uploads it. If the old one belongs to someone else, it has to stay that file.
                var written = await Task.Run(() => target.GetAttributes(temporary), ct);
                if (written.UserId != existing.Attributes.UserId || written.GroupId != existing.Attributes.GroupId)
                {
                    await Task.Run(() => target.DeleteFile(temporary), ct);
                    await Send(final);
                    return;
                }
            }
            var mode = existing != null ? PermissionBits(existing.Attributes) : newFileMode;
            await Task.Run(() => PutInPlace(target, temporary, final, existing != null, mode), ct);
        }
        catch
        {
            try { if (target.Exists(temporary)) target.DeleteFile(temporary); }
            catch (Exception) { /* the connection may be gone; the leftover is clearly named */ }
            throw;
        }
    }

    private static bool HasSpecialBits(SftpFileAttributes a) => a.IsUIDBitSet || a.IsGroupIDBitSet || a.IsStickyBitSet;

    private static void PutInPlace(SftpClient target, string temporary, string final, bool replaces, int? mode)
    {
        try
        {
            target.RenameFile(temporary, final, isPosix: true); // replaces in one step where the server supports it
        }
        catch (Exception ex) when (ex is NotSupportedException or SshException)
        {
            if (replaces) target.DeleteFile(final);
            target.RenameFile(temporary, final);
        }
        if (mode is not { } bits) return;
        try { target.ChangePermissions(final, short.Parse(Convert.ToString(bits, 8))); }
        catch (Exception ex) when (ex is SshException or NotSupportedException) { /* some servers do not allow it */ }
    }

    /// <summary>The same for a download: the file on this PC is replaced only by a complete one.</summary>
    public static async Task SafeLocalWriteAsync(string final, Func<Stream, Task> write)
    {
        var existing = new FileInfo(final);
        // writing through a link keeps it a link
        if (existing.Exists && existing.LinkTarget != null)
        {
            await using var through = File.Create(final);
            await write(through);
            return;
        }
        if (existing.Exists && existing.IsReadOnly) throw new UnauthorizedAccessException($"\"{final}\" is write-protected.");

        var temporary = Path.Combine(Path.GetDirectoryName(final)!, PartName(Path.GetFileName(final)));
        try
        {
            await using (var stream = File.Create(temporary))
                await write(stream);
            if (!File.Exists(final)) File.Move(temporary, final);
            else if (OperatingSystem.IsWindows())
            {
                // Replace keeps the permissions of the old file; not every drive supports it
                try { File.Replace(temporary, final, null, ignoreMetadataErrors: true); }
                catch (Exception ex) when (ex is IOException or PlatformNotSupportedException) { File.Move(temporary, final, overwrite: true); }
            }
            else
            {
                File.SetUnixFileMode(temporary, File.GetUnixFileMode(final));
                File.Move(temporary, final, overwrite: true);
            }
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { /* still held by a virus scanner: the leftover is clearly named */ }
        }
    }
}
