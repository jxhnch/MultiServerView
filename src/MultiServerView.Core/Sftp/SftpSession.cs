using Renci.SshNet;
using Renci.SshNet.Sftp;
using MultiServerView.Core.Ssh;

namespace MultiServerView.Core.Sftp;

/// <summary>Eine SFTP-Verbindung zu einem Host: Dateien auflisten, hoch-/herunterladen, anlegen, umbenennen, löschen.</summary>
public sealed class SftpSession : IDisposable
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

    private SftpClient Client => _client ?? throw new InvalidOperationException("Die SFTP-Verbindung ist nicht offen.");

    public async Task<string> ConnectAsync()
    {
        var tunnel = await JumpTunnel.OpenAsync(_server, _jumps, _check);
        var client = new SftpClient(SshConnector.BuildConnectionInfo(_server, _key, tunnel?.Host, tunnel?.Port)) { KeepAliveInterval = SshConnector.KeepAlive };
        var hostKeyError = SshConnector.HookHostKey(client, _server, _check, null);
        try
        {
            await Task.Run(() => client.Connect());
        }
        catch (Exception ex)
        {
            client.Dispose();
            tunnel?.Dispose();
            throw new SshConnectException(hostKeyError() ?? SshConnector.Describe(ex), ex);
        }
        _client = client;
        _tunnel = tunnel;
        return client.WorkingDirectory;
    }

    public Task<BrowseResult> ListAsync(string path) => Task.Run(() =>
    {
        var client = Client;
        var full = Normalize(client.WorkingDirectory, path);
        var entries = client.ListDirectory(full)
            .Where(f => f.Name != "." && f.Name != "..")
            .Select(f => new FileEntry(f.Name, f.FullName, f.IsDirectory, f.IsDirectory ? 0 : f.Length, new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero), Mode(f.Attributes)))
            .OrderByDescending(e => e.IsDir)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new BrowseResult(full, ParentOf(full), entries);
    });

    public Task MakeDirAsync(string path) => Task.Run(() => Client.CreateDirectory(path));

    public Task RenameAsync(string from, string to) => Task.Run(() => Client.RenameFile(from, to));

    public Task DeleteAsync(string path) => Task.Run(() => DeleteRecursive(Client, path));

    /// <summary>Lädt eine Datei oder einen ganzen Ordner (rekursiv) in <paramref name="remoteDir"/> hoch.</summary>
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
            else throw new FileNotFoundException("Datei nicht gefunden: " + localPath);

            foreach (var dir in d.OrderBy(x => x.Length))
                if (!client.Exists(dir)) client.CreateDirectory(dir);
            return (f, d);
        }, ct);

        var total = files.Sum(f => f.size);
        long done = 0;
        progress(new TransferProgress(name, 0, total, false));
        foreach (var (local, remote, size) in files)
        {
            ct.ThrowIfCancellationRequested();
            var baseDone = done;
            await using var stream = File.OpenRead(local);
            // Async-API: Abbruch über das Token. In Fortschritts-Callbacks nie werfen - die laufen auf dem Threadpool.
            await client.UploadFileAsync(stream, remote, true,
                new Progress<UploadFileProgressReport>(r => progress(new TransferProgress(name, baseDone + (long)r.TotalBytesUploaded, total, false))), ct);
            done += size;
        }
        progress(new TransferProgress(name, total, total, true));
    }

    /// <summary>Lädt eine Datei oder einen ganzen Ordner (rekursiv) in den lokalen Ordner <paramref name="localDir"/> herunter.</summary>
    public async Task DownloadAsync(string remotePath, string localDir, Action<TransferProgress> progress, CancellationToken ct)
    {
        var client = Client;
        var name = remotePath.TrimEnd('/').Split('/').Last();

        var (files, dirs) = await Task.Run(() =>
        {
            var attrs = client.GetAttributes(remotePath);
            var f = new List<(string remote, string local, long size)>();
            var d = new List<string>();
            if (attrs.IsDirectory)
            {
                var rootLocal = Path.Combine(localDir, name);
                d.Add(rootLocal);
                CollectRemote(client, remotePath, rootLocal, f, d);
            }
            else f.Add((remotePath, Path.Combine(localDir, name), attrs.Size));
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
            await using var stream = File.Create(local);
            await client.DownloadFileAsync(remote, stream,
                new Progress<DownloadFileProgressReport>(r => progress(new TransferProgress(name, baseDone + (long)r.TotalBytesDownloaded, total, false))), ct);
            done += size;
        }
        progress(new TransferProgress(name, total, total, true));
    }

    private static void CollectRemote(SftpClient client, string remoteDir, string localDir,
        List<(string, string, long)> files, List<string> dirs)
    {
        foreach (var f in client.ListDirectory(remoteDir))
        {
            if (f.Name is "." or "..") continue;
            var local = Path.Combine(localDir, f.Name);
            if (f.IsDirectory)
            {
                dirs.Add(local);
                CollectRemote(client, f.FullName, local, files, dirs);
            }
            else files.Add((f.FullName, local, f.Length));
        }
    }

    private static void DeleteRecursive(SftpClient client, string path)
    {
        var attrs = client.GetAttributes(path);
        if (!attrs.IsDirectory)
        {
            client.DeleteFile(path);
            return;
        }
        foreach (var f in client.ListDirectory(path))
        {
            if (f.Name is "." or "..") continue;
            if (f.IsDirectory) DeleteRecursive(client, f.FullName);
            else client.DeleteFile(f.FullName);
        }
        client.DeleteDirectory(path);
    }

    /// <summary>Macht aus relativen Pfaden absolute und löst "." und ".." auf.</summary>
    public static string Normalize(string workingDirectory, string path)
    {
        var raw = string.IsNullOrWhiteSpace(path) || path == "." ? workingDirectory : path.StartsWith('/') ? path : Combine(workingDirectory, path);
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
        try { _client?.Dispose(); } catch { /* egal beim Aufräumen */ }
        try { _tunnel?.Dispose(); } catch { /* egal beim Aufräumen */ }
        _client = null;
        _tunnel = null;
    }
}
