using System.Security.Cryptography;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Sftp;
using MultiServerView.Core.Ssh;

namespace MultiServerView.Core.Sftp;

/// <summary>A text file from the server for editing. <see cref="Content"/> always uses "\n" as line ending; the original is in <see cref="LineEnding"/>.</summary>
public sealed record RemoteText(string Content, string LineEnding, bool Bom, long Size, long Modified);

/// <summary>The file was changed on the server since it was opened.</summary>
public sealed class RemoteChangedException : InvalidOperationException
{
    public const string Text = "The file was changed on the server since you opened it.";
    public RemoteChangedException() : base(Text) { }
}

/// <summary>An SFTP connection to a host: list, upload/download, create, rename and delete files.</summary>
public sealed partial class SftpSession : IDisposable
{
    private readonly ServerEntry _server;
    private readonly KeyEntry? _key;
    private readonly Func<string, int, string, HostKeyTrust> _check;
    private readonly JumpResolver? _jumps;
    private JumpTunnel? _tunnel;
    private SftpClient? _client;

    public SftpSession(ServerEntry server, KeyEntry? key, Func<string, int, string, HostKeyTrust> check, JumpResolver? jumps = null)
    {
        _jumps = jumps;
        _server = server;
        _key = key;
        _check = check;
    }

    private SftpClient Client => _client ?? throw new InvalidOperationException("The SFTP connection is not open.");

    /// <summary>True while the underlying SFTP client and any jump tunnel remain connected.</summary>
    public bool IsConnected => _client?.IsConnected == true && (_tunnel == null || _tunnel.IsConnected);

    public async Task<string> ConnectAsync(CancellationToken ct = default)
    {
        var tunnel = await JumpTunnel.OpenAsync(_server, _jumps, _check, ct);
        SftpClient? client = null;
        Func<string?>? hostKeyError = null;
        try
        {
            client = new SftpClient(SshConnector.BuildConnectionInfo(_server, _key, tunnel?.Host, tunnel?.Port)) { KeepAliveInterval = SshConnector.KeepAlive };
            hostKeyError = SshConnector.HookHostKey(client, _server, _check, null);
            await Task.Run(() => client.Connect(), ct);
        }
        catch (SshConnectException)
        {
            client?.Dispose();
            tunnel?.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            client?.Dispose();
            tunnel?.Dispose();
            throw new SshConnectException(hostKeyError?.Invoke() ?? SshConnector.Describe(ex), ex);
        }
        _client = client;
        _tunnel = tunnel;
        return client.WorkingDirectory;
    }

    public Task<BrowseResult> ListAsync(string path, CancellationToken ct = default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        var client = Client;
        var full = Normalize(client.WorkingDirectory, path);
        var entries = client.ListDirectory(full)
            .Where(f => f.Name != "." && f.Name != "..")
            .Select(f => new FileEntry(f.Name, f.FullName, f.IsDirectory, f.IsDirectory ? 0 : f.Length, new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero), Mode(f.Attributes), f.IsSymbolicLink))
            .OrderByDescending(e => e.IsDir)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new BrowseResult(full, ParentOf(full), entries);
    }, ct);

    public Task MakeDirAsync(string path) => Task.Run(() => Client.CreateDirectory(path));

    /// <summary>Creates an empty file (fails if the name is taken).</summary>
    public Task CreateFileAsync(string path) => Task.Run(() =>
    {
        var client = Client;
        if (client.Exists(path)) throw new InvalidOperationException("\"" + path.Split('/').Last() + "\" already exists.");
        using (client.Create(path)) { }
    });

    /// <summary>Properties of a file or folder. For folders the size and the number of files are summed up over everything inside.</summary>
    public Task<FileStat> StatAsync(string path, CancellationToken ct = default) => Task.Run(() =>
    {
        var client = Client;
        var file = client.Get(path);
        var a = file.Attributes;
        long size = file.IsDirectory ? 0 : file.Length;
        var items = 0;
        if (file.IsDirectory) SumFolder(client, path, ref size, ref items, ct, 0);
        return new FileStat(file.Name, file.FullName, file.IsDirectory, file.IsSymbolicLink, size, items, PermissionBits(a), Mode(a), file.UserId > 0 ? (uint)file.UserId : 0, file.GroupId > 0 ? (uint)file.GroupId : 0,
            new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero), new DateTimeOffset(file.LastAccessTimeUtc, TimeSpan.Zero));
    }, ct);

    private static void SumFolder(SftpClient client, string path, ref long size, ref int items, CancellationToken ct, int depth)
    {
        if (depth > 64) return; // protects against link loops
        foreach (var f in client.ListDirectory(path))
        {
            ct.ThrowIfCancellationRequested();
            if (f.Name is "." or "..") continue;
            if (f.IsDirectory && !f.IsSymbolicLink) SumFolder(client, f.FullName, ref size, ref items, ct, depth + 1);
            else if (!f.IsDirectory)
            {
                size += f.Length;
                items++;
            }
        }
    }

    /// <summary>Sets Unix permissions (e.g. 0x1ED = 755). Optionally for everything inside a folder as well.</summary>
    public Task ChmodAsync(string path, int mode, bool recursive) => Task.Run(() =>
    {
        var client = Client;
        if (mode is < 0 or > 0xFFF) throw new ArgumentException("The permissions must be between 000 and 7777.");
        void Apply(string p, int depth)
        {
            // SSH.NET wants the octal digits as a decimal number: 0o640 is passed as 640
            client.ChangePermissions(p, short.Parse(Convert.ToString(mode, 8)));
            if (!recursive || depth > 64) return;
            var f = client.Get(p);
            if (!f.IsDirectory || f.IsSymbolicLink) return;
            foreach (var child in client.ListDirectory(p))
                if (child.Name is not ("." or "..") && !child.IsSymbolicLink) Apply(child.FullName, depth + 1);
        }
        Apply(path, 0);
    });

    /// <summary>SHA-256 of a remote file (read over SFTP, nothing is stored).</summary>
    public async Task<string> ChecksumAsync(string path, CancellationToken ct = default)
    {
        var client = Client;
        await using var stream = await Task.Run(() => client.OpenRead(path), ct);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
    }

    public Task RenameAsync(string from, string to) => Task.Run(() =>
    {
        var client = Client;
        Entry(client, from).MoveTo(Normalize(client.WorkingDirectory, to));
    });

    public Task DeleteAsync(string path) => Task.Run(() => DeleteRecursive(Entry(Client, path), Client));

    /// <summary>
    /// The entry itself as the folder lists it. Needed for everything that removes or renames: SSH.NET's calls that take a path
    /// first resolve it, so for a link they would delete or move the file it points to. The entry of a listing does not.
    /// </summary>
    private static ISftpFile Entry(SftpClient client, string path)
    {
        var full = Normalize(client.WorkingDirectory, path);
        var name = full.Split('/').Last();
        return client.ListDirectory(ParentOf(full)).FirstOrDefault(f => f.Name == name && name.Length > 0 && name is not ("." or ".."))
               ?? throw new Renci.SshNet.Common.SftpPathNotFoundException("No such file: " + full);
    }

    /// <summary>Uploads a file or a whole folder (recursively) into <paramref name="remoteDir"/>.</summary>
    public async Task UploadAsync(string localPath, string remoteDir, Action<TransferProgress> progress, CancellationToken ct)
    {
        var client = Client;
        var name = Path.GetFileName(localPath.TrimEnd(Path.DirectorySeparatorChar, '/'));

        var (files, dirs) = await Task.Run(() =>
        {
            var f = new List<(string local, string remote, long size)>();
            var d = new List<string>();
            if (Directory.Exists(localPath))
            {
                var rootRemote = Combine(remoteDir, name);
                d.Add(rootRemote);
                foreach (var sub in Directory.EnumerateDirectories(localPath, "*", SearchOption.AllDirectories))
                    d.Add(Combine(rootRemote, Relative(localPath, sub)));
                foreach (var file in Directory.EnumerateFiles(localPath, "*", SearchOption.AllDirectories))
                    f.Add((file, Combine(rootRemote, Relative(localPath, file)), new FileInfo(file).Length));
            }
            else if (File.Exists(localPath)) f.Add((localPath, Combine(remoteDir, name), new FileInfo(localPath).Length));
            else throw new FileNotFoundException("File not found: " + localPath);

            foreach (var dir in d.OrderBy(x => x.Length))
                if (!client.Exists(dir)) client.CreateDirectory(dir);
            return (f, d);
        }, ct);

        var total = files.Sum(f => f.size);
        long done = 0;
        var cache = new FolderCache();
        progress(new TransferProgress(name, 0, total, false));
        foreach (var (local, remote, size) in files)
        {
            ct.ThrowIfCancellationRequested();
            var baseDone = done;
            // Async API: cancel via the token. Never throw in progress callbacks - they run on the thread pool.
            await SafeUploadAsync(client, () => File.OpenRead(local), remote, null, cache,
                sent => progress(new TransferProgress(name, baseDone + sent, total, false)), ct);
            done += size;
        }
        progress(new TransferProgress(name, total, total, true));
    }

    /// <summary>
    /// Combines <paramref name="localDir"/> and a single remote entry name and verifies the resulting path stays
    /// strictly inside <paramref name="localDir"/> (blocks traversal, path separators and rooted names).
    /// </summary>
    public static string SafeLocalChildPath(string localDir, string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName) || entryName is "." or ".."
            || entryName.IndexOfAny(new[] { '/', '\\', '\0', ':' }) >= 0)
            throw new InvalidOperationException("Unsafe remote file name: " + entryName);

        var fullRoot = Path.GetFullPath(localDir);
        var rootWithSep = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;
        var fullTarget = Path.GetFullPath(Path.Combine(fullRoot, entryName));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!fullTarget.StartsWith(rootWithSep, comparison))
            throw new InvalidOperationException("Unsafe remote file name: " + entryName);
        return fullTarget;
    }

    private static readonly HashSet<string> BlockedExternalExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".msi", ".msp", ".msu",
        ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".ps1", ".psm1", ".psd1",
        ".scr", ".pif", ".lnk", ".hta", ".cpl", ".msc", ".reg", ".inf",
        ".jar", ".dll", ".sys", ".appimage", ".desktop"
    };

    /// <summary>
    /// True if opening <paramref name="fileName"/> with the OS default handler (ShellExecute / xdg-open)
    /// could execute remote code instead of opening a text editor.
    /// </summary>
    public static bool IsBlockedExternalExtension(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return !string.IsNullOrEmpty(ext) && BlockedExternalExtensions.Contains(ext);
    }

    /// <summary>
    /// Downloads a file or a whole folder (recursively) into the local folder <paramref name="localDir"/>.
    /// Returns the number of links (and too deeply nested folders) that were left out.
    /// </summary>
    public async Task<int> DownloadAsync(string remotePath, string localDir, Action<TransferProgress> progress, CancellationToken ct)
    {
        var client = Client;
        var name = remotePath.TrimEnd('/').Split('/').Last();
        var rootLocal = SafeLocalChildPath(localDir, name);

        var skipped = 0;
        var (files, dirs) = await Task.Run(() =>
        {
            var attrs = client.GetAttributes(remotePath);
            var f = new List<(string remote, string local, long size)>();
            var d = new List<string>();
            if (attrs.IsDirectory)
            {
                d.Add(rootLocal);
                CollectRemote(client, remotePath, rootLocal, f, d, ref skipped);
            }
            else f.Add((remotePath, rootLocal, attrs.Size));
            foreach (var dir in d) Directory.CreateDirectory(dir);
            return (f, d);
        }, ct);

        var total = files.Sum(f => f.size);
        long done = 0;
        progress(new TransferProgress(name, 0, total, false));
        foreach (var (remote, local, size) in files)
        {
            ct.ThrowIfCancellationRequested();
            var baseDone = done;
            await SafeLocalWriteAsync(local, stream => client.DownloadFileAsync(remote, stream,
                new Progress<DownloadFileProgressReport>(r => progress(new TransferProgress(name, baseDone + (long)r.TotalBytesDownloaded, total, false))), ct));
            done += size;
        }
        progress(new TransferProgress(name, total, total, true));
        return skipped;
    }

    private static void CollectRemote(SftpClient client, string remoteDir, string localDir,
        List<(string, string, long)> files, List<string> dirs, ref int skipped, int depth = 0)
    {
        if (depth > 64) { skipped++; return; } // protects against link loops
        foreach (var f in client.ListDirectory(remoteDir))
        {
            if (f.Name is "." or "..") continue;
            if (f.IsSymbolicLink) { skipped++; continue; }
            var local = SafeLocalChildPath(localDir, f.Name);
            if (f.IsDirectory)
            {
                dirs.Add(local);
                CollectRemote(client, f.FullName, local, files, dirs, ref skipped, depth + 1);
            }
            else files.Add((f.FullName, local, f.Length));
        }
    }

    private static void DeleteRecursive(ISftpFile item, SftpClient client, int depth = 0)
    {
        if (depth > 64) throw new InvalidOperationException("Folder nesting is too deep to delete safely.");
        if (item.IsDirectory && !item.IsSymbolicLink)
            foreach (var f in client.ListDirectory(item.FullName))
                if (f.Name is not ("." or "..")) DeleteRecursive(f, client, depth + 1);
        item.Delete(); // a link is removed itself, never what it points to
    }

    /// <summary>Largest file the built-in editor opens.</summary>
    public const long MaxEditBytes = 2 * 1024 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Reads a UTF-8 text file for editing. Binary files and very large files are rejected.</summary>
    public Task<RemoteText> ReadTextAsync(string path, CancellationToken ct = default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        var client = Client;
        var attrs = client.GetAttributes(path);
        if (attrs.IsDirectory) throw new InvalidOperationException("A folder cannot be edited.");
        if (attrs.Size > MaxEditBytes) throw new InvalidOperationException($"The file is larger than {MaxEditBytes / (1024 * 1024)} MB - please download it instead.");

        var bytes = client.ReadAllBytes(path);
        if (Array.IndexOf(bytes, (byte)0) >= 0) throw new InvalidOperationException("This is not a text file (it contains binary data).");

        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidOperationException("The file is not UTF-8 encoded and cannot be edited safely here.");
        }

        var crlf = text.Contains("\r\n", StringComparison.Ordinal);
        return new RemoteText(crlf ? text.Replace("\r\n", "\n") : text, crlf ? "crlf" : "lf", hasBom, bytes.Length, UnixSeconds(attrs.LastWriteTimeUtc));
    }, ct);

    /// <summary>Reads a 1-based line range from a UTF-8 text file (works on files larger than 2 MB).</summary>
    public Task<string> ReadTextRangeAsync(string path, int startLine, int maxLines, CancellationToken ct = default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        var client = Client;
        var attrs = client.GetAttributes(path);
        if (attrs.IsDirectory) throw new InvalidOperationException("A folder cannot be edited.");
        using var stream = client.OpenRead(path);
        return ReadLineRange(stream, startLine, maxLines, ct);
    }, ct);

    /// <summary>Detects line ending ("crlf" or "lf") and UTF-8 BOM of an existing remote file, or ("lf", false) otherwise.</summary>
    public Task<(string LineEnding, bool Bom)> TryDetectFormatAsync(string path, CancellationToken ct = default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        var client = Client;
        try
        {
            if (!client.Exists(path)) return ("lf", false);
            using var stream = client.OpenRead(path);
            var buf = new byte[8192];
            var total = 0;
            while (total < buf.Length)
            {
                ct.ThrowIfCancellationRequested();
                var n = stream.Read(buf, total, buf.Length - total);
                if (n == 0) break;
                total += n;
            }
            return DetectTextFormat(buf.AsSpan(0, total));
        }
        catch (Exception ex) when (ex is Renci.SshNet.Common.SshException or IOException)
        {
            return ("lf", false);
        }
    }, ct);

    public static (string LineEnding, bool Bom) DetectTextFormat(ReadOnlySpan<byte> sample)
    {
        var bom = sample.Length >= 3 && sample[0] == 0xEF && sample[1] == 0xBB && sample[2] == 0xBF;
        var crlf = sample.IndexOf("\r\n"u8) >= 0;
        return (crlf ? "crlf" : "lf", bom);
    }

    public static string ReadLineRange(Stream stream, int startLine, int maxLines, CancellationToken ct = default)
    {
        if (startLine < 1) startLine = 1;
        if (maxLines < 1) maxLines = 1;
        var endLine = (long)startLine + maxLines;

        var decoder = StrictUtf8.GetDecoder();
        var byteBuf = new byte[16384];
        var charBuf = new char[16384];
        var sb = new StringBuilder();
        var line = new StringBuilder();
        long lineNo = 1;
        var collected = 0;
        var atStart = true;
        var pendingCr = false;
        var truncated = false;
        var keepReading = true;

        bool EmitLine()
        {
            if (lineNo >= startLine && lineNo < endLine)
            {
                if (collected > 0)
                {
                    if (sb.Length + 1 > MaxEditBytes) { truncated = true; return false; }
                    sb.Append('\n');
                }
                var take = Math.Min(line.Length, (int)MaxEditBytes - sb.Length);
                sb.Append(line, 0, take);
                collected++;
                if (take < line.Length) { truncated = true; return false; }
            }
            line.Clear();
            lineNo++;
            return lineNo < endLine;
        }

        while (keepReading)
        {
            ct.ThrowIfCancellationRequested();
            var n = stream.Read(byteBuf, 0, byteBuf.Length);
            if (n > 0 && Array.IndexOf(byteBuf, (byte)0, 0, n) >= 0)
                throw new InvalidOperationException("This is not a text file (it contains binary data).");

            int charCount;
            try
            {
                charCount = decoder.GetChars(byteBuf, 0, n, charBuf, 0, flush: n == 0);
            }
            catch (DecoderFallbackException)
            {
                throw new InvalidOperationException("The file is not UTF-8 encoded and cannot be edited safely here.");
            }

            for (var i = 0; i < charCount; i++)
            {
                var ch = charBuf[i];
                if (atStart)
                {
                    atStart = false;
                    if (ch == '\uFEFF') continue;
                }
                if (pendingCr)
                {
                    pendingCr = false;
                    if (!EmitLine()) { keepReading = false; break; }
                    if (ch == '\n') continue;
                }
                if (ch == '\r') { pendingCr = true; continue; }
                if (ch == '\n')
                {
                    if (!EmitLine()) { keepReading = false; break; }
                    continue;
                }
                if (lineNo >= startLine && line.Length < MaxEditBytes + 32) line.Append(ch);
            }

            if (n == 0) break;
        }

        if (keepReading && (pendingCr || line.Length > 0)) EmitLine();
        if (truncated) sb.Append("\n[output truncated]");
        return sb.ToString();
    }

    /// <summary>
    /// Writes the edited text back (line ending and BOM as in the original; permissions and owner stay because the same file
    /// is overwritten). Without <paramref name="force"/> it aborts if the file was changed since it was read.
    /// </summary>
    public Task<(long Modified, long Size)> WriteTextAsync(string path, string content, string lineEnding, bool bom, long expectedModified, long expectedSize, bool force, CancellationToken ct = default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        var client = Client;
        if (!force)
        {
            var now = client.GetAttributes(path);
            if (UnixSeconds(now.LastWriteTimeUtc) != expectedModified || now.Size != expectedSize) throw new RemoteChangedException();
        }

        var text = lineEnding == "crlf" ? content.Replace("\r\n", "\n").Replace("\n", "\r\n") : content;
        var body = StrictUtf8.GetBytes(text);
        var bytes = bom ? new byte[] { 0xEF, 0xBB, 0xBF }.Concat(body).ToArray() : body;
        using (var ms = new MemoryStream(bytes)) client.UploadFile(ms, path, true);
        var written = client.GetAttributes(path);
        return (UnixSeconds(written.LastWriteTimeUtc), written.Size);
    }, ct);

    private static long UnixSeconds(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    /// <summary>Turns relative paths into absolute ones and resolves ".", ".." and "~".</summary>
    public static string Normalize(string workingDirectory, string path)
    {
        var p = path.Trim();
        if (p == "~") p = ".";
        else if (p.StartsWith("~/", StringComparison.Ordinal)) p = p[2..];
        var raw = string.IsNullOrWhiteSpace(p) || p == "." ? workingDirectory : p.StartsWith('/') ? p : Combine(workingDirectory, p);
        var parts = new List<string>();
        foreach (var seg in raw.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".") continue;
            if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else parts.Add(seg);
        }
        return "/" + string.Join('/', parts);
    }

    public static string Combine(string dir, string name) => dir.EndsWith('/') ? dir + name : dir + "/" + name;

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    public static string ParentOf(string path)
    {
        var p = path.TrimEnd('/');
        var i = p.LastIndexOf('/');
        return i <= 0 ? "/" : p[..i];
    }

    /// <summary>The nine permission bits as a number (e.g. 0x1ED for rwxr-xr-x).</summary>
    public static int PermissionBits(SftpFileAttributes a) =>
        (a.OwnerCanRead ? 0x100 : 0) | (a.OwnerCanWrite ? 0x80 : 0) | (a.OwnerCanExecute ? 0x40 : 0) |
        (a.GroupCanRead ? 0x20 : 0) | (a.GroupCanWrite ? 0x10 : 0) | (a.GroupCanExecute ? 0x8 : 0) |
        (a.OthersCanRead ? 0x4 : 0) | (a.OthersCanWrite ? 0x2 : 0) | (a.OthersCanExecute ? 0x1 : 0);

    private static string Mode(SftpFileAttributes a)
    {
        static string Bits(bool r, bool w, bool x) => (r ? "r" : "-") + (w ? "w" : "-") + (x ? "x" : "-");
        return (a.IsDirectory ? "d" : "-") +
               Bits(a.OwnerCanRead, a.OwnerCanWrite, a.OwnerCanExecute) +
               Bits(a.GroupCanRead, a.GroupCanWrite, a.GroupCanExecute) +
               Bits(a.OthersCanRead, a.OthersCanWrite, a.OthersCanExecute);
    }

    public void Dispose()
    {
        try { _client?.Dispose(); } catch { /* does not matter during cleanup */ }
        try { _tunnel?.Dispose(); } catch { /* does not matter during cleanup */ }
        _client = null;
        _tunnel = null;
    }
}
