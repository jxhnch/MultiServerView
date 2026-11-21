using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using MultiServerView.Core;
using MultiServerView.Core.Import;
using MultiServerView.Core.Sync;
using MultiServerView.Host;

namespace MultiServerView.Linux;

/// <summary>Linux: GTK/WebKit window (Photino), key file instead of DPAPI, xdg-open, trash, xfreerdp/Remmina.</summary>
internal sealed class LinuxPlatform : IHostPlatform
{
    private readonly Func<string, Task<string?>> _pickSavePath;
    private readonly Func<string, Task<string?>> _pickFolder;
    private readonly Func<string, bool> _window;

    public LinuxPlatform(string dataDir, Func<string, Task<string?>> pickSavePath, Func<string, Task<string?>> pickFolder, Func<string, bool> window)
    {
        _pickSavePath = pickSavePath;
        _pickFolder = pickFolder;
        _window = window;
        Secrets = new KeyFileProtector(Path.Combine(dataDir, "secret.key"));
    }

    public string Name => "linux";
    public bool CustomTitleBar => false;
    public ISecretProtector Secrets { get; }

    /// <summary>Updates come with the package or the AppImage, the app does not replace itself.</summary>
    public bool CanSelfUpdate => false;

    public Task<string?> PickSavePathAsync(string suggestedName) => _pickSavePath(suggestedName);
    public Task<string?> PickFolderAsync(string description) => _pickFolder(description);
    public bool WindowCommand(string command) => _window(command);

    public void OpenFile(string path) => Start("xdg-open", path);

    public void RevealInFileManager(string path)
    {
        // most file managers cannot select a file from the command line: open its folder
        var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path;
        Start("xdg-open", dir);
    }

    public void MoveToTrash(IReadOnlyList<string> paths)
    {
        foreach (var tool in new[] { ("gio", "trash"), ("trash-put", "") })
        {
            if (Which(tool.Item1) == null) continue;
            var info = new ProcessStartInfo(tool.Item1) { UseShellExecute = false, RedirectStandardError = true };
            if (tool.Item2.Length > 0) info.ArgumentList.Add(tool.Item2);
            info.ArgumentList.Add("--");
            foreach (var p in paths) info.ArgumentList.Add(p);
            using var process = Process.Start(info)!;
            process.WaitForExit();
            if (process.ExitCode != 0) throw new IOException("Could not move to the trash: " + process.StandardError.ReadToEnd().Trim());
            return;
        }
        throw new InvalidOperationException("No trash tool found (install \"gio\" from glib or \"trash-cli\"). Nothing was deleted.");
    }

    public void OpenExternalRdp(ServerEntry server)
    {
        var host = (server.Host ?? "").Trim();
        if (host.Length == 0 || host.IndexOfAny(new[] { ' ', '\t', '\r', '\n', '"', '\'', '/', '\\' }) >= 0)
            throw new InvalidOperationException("Invalid RDP host name.");
        var port = server.Port is > 0 and <= 65535 ? server.Port : 3389;
        var target = $"{host}:{port}";
        foreach (var exe in new[] { "xfreerdp3", "xfreerdp", "wlfreerdp" })
            if (Which(exe) != null)
            {
                var info = new ProcessStartInfo(exe) { UseShellExecute = false };
                info.ArgumentList.Add("/v:" + target);
                if (server.Username.Length > 0) info.ArgumentList.Add("/u:" + server.Username);
                info.ArgumentList.Add("/cert:tofu");
                info.ArgumentList.Add("/dynamic-resolution");
                Process.Start(info);
                return;
            }
        if (Which("remmina") != null)
        {
            Start("remmina", "-c", "rdp://" + target);
            return;
        }
        throw new InvalidOperationException("No RDP client found. Install FreeRDP (xfreerdp) or Remmina.");
    }

    public void StartTeamViewer(string id, string password)
    {
        var exe = Which("teamviewer") ?? throw new InvalidOperationException("TeamViewer is not installed (https://www.teamviewer.com).");
        var info = new ProcessStartInfo(exe) { UseShellExecute = false };
        info.ArgumentList.Add("--id");
        info.ArgumentList.Add(id);
        if (password.Length > 0)
        {
            info.ArgumentList.Add("--password");
            info.ArgumentList.Add(password);
        }
        Process.Start(info);
    }

    public IReadOnlyList<PuttySession>? ReadPuttySessions() => null;

    public Task InstallUpdateAsync(string setupUrl, string version, string? expectedSha256 = null) =>
        throw new InvalidOperationException("Linux updates come with the package or the AppImage. Download the new version from the releases page.");

    private static void Start(string exe, params string[] args)
    {
        var info = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var a in args) info.ArgumentList.Add(a);
        try
        {
            Process.Start(info);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException($"\"{exe}\" was not found. Please install it.");
        }
    }

    private static string? Which(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, exe);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}

/// <summary>
/// Protects secrets (GitHub token, Supabase password) with AES-GCM and a random key in a file that only the user can read (mode 600).
/// This keeps the secrets out of plain sight and out of backups of sync.json, but it is not as strong as the Windows DPAPI:
/// anyone who can read the user's files can read the key too. The vault itself is protected by the master password.
/// </summary>
internal sealed class KeyFileProtector : ISecretProtector
{
    private readonly string _keyFile;
    private readonly object _gate = new();
    private byte[]? _key;

    public KeyFileProtector(string keyFile) => _keyFile = keyFile;

    private byte[] Key()
    {
        if (_key != null) return _key;
        lock (_gate)
        {
            if (_key != null) return _key;
            if (File.Exists(_keyFile)) return _key = Convert.FromBase64String(File.ReadAllText(_keyFile).Trim());

            Directory.CreateDirectory(Path.GetDirectoryName(_keyFile)!);
            var key = RandomNumberGenerator.GetBytes(32);
            var bytes = Encoding.UTF8.GetBytes(Convert.ToBase64String(key));
            var opts = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) opts.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var fs = new FileStream(_keyFile, opts)) fs.Write(bytes);
            return _key = key;
        }
    }

    public string Protect(string plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var data = Encoding.UTF8.GetBytes(plain);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using (var gcm = new AesGcm(Key(), 16)) gcm.Encrypt(nonce, data, cipher, tag);
        return Convert.ToBase64String(nonce.Concat(tag).Concat(cipher).ToArray());
    }

    public string Unprotect(string protectedText)
    {
        var all = Convert.FromBase64String(protectedText);
        var nonce = all[..12];
        var tag = all[12..28];
        var cipher = all[28..];
        var plain = new byte[cipher.Length];
        using (var gcm = new AesGcm(Key(), 16)) gcm.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }
}
