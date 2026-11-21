using System.Diagnostics;
using MultiServerView.Core;
using MultiServerView.Core.Import;
using MultiServerView.Core.Sync;
using MultiServerView.Host;

namespace MultiServerView.App;

/// <summary>Windows: WebView2 window, DPAPI, mstsc, Explorer, recycle bin, PuTTY registry, setup installer.</summary>
internal sealed class WindowsPlatform : IHostPlatform
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    private readonly Func<string, Task<string?>> _pickSavePath;
    private readonly Func<string, Task<string?>> _pickFolder;
    private readonly Func<string, bool> _window;

    public WindowsPlatform(Func<string, Task<string?>> pickSavePath, Func<string, Task<string?>> pickFolder, Func<string, bool> window)
    {
        _pickSavePath = pickSavePath;
        _pickFolder = pickFolder;
        _window = window;
    }

    public string Name => "windows";
    public bool CustomTitleBar => true;
    public ISecretProtector Secrets { get; } = new DpapiProtector();

    /// <summary>Installed by the setup wizard (an uninstaller sits next to the exe) - otherwise it is the portable ZIP.</summary>
    public bool CanSelfUpdate => Directory.EnumerateFiles(AppContext.BaseDirectory, "unins*.exe").Any();

    public Task<string?> PickSavePathAsync(string suggestedName) => _pickSavePath(suggestedName);
    public Task<string?> PickFolderAsync(string description) => _pickFolder(description);
    public bool WindowCommand(string command) => _window(command);

    public void OpenFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // no program is associated with this file type: ask "Open with..."
            Process.Start(new ProcessStartInfo("rundll32.exe") { ArgumentList = { "shell32.dll,OpenAs_RunDLL", path }, UseShellExecute = false });
        }
    }

    public void RevealInFileManager(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", path }, UseShellExecute = false });

    public void MoveToTrash(IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            else
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
    }

    public void OpenExternalRdp(ServerEntry server)
    {
        var host = (server.Host ?? "").Trim();
        if (host.Length == 0 || host.IndexOfAny(new[] { ' ', '\t', '\r', '\n', '"', '\'', '/', '\\' }) >= 0)
            throw new InvalidOperationException("Invalid RDP host name.");
        var port = server.Port is > 0 and <= 65535 ? server.Port : 3389;
        var info = new ProcessStartInfo("mstsc.exe") { UseShellExecute = false };
        info.ArgumentList.Add($"/v:{host}:{port}");
        Process.Start(info);
    }

    public void StartTeamViewer(string id, string password)
    {
        string? exe = null;
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
        {
            if (string.IsNullOrEmpty(root)) continue;
            var candidate = Path.Combine(root, "TeamViewer", "TeamViewer.exe");
            if (File.Exists(candidate)) { exe = candidate; break; }
        }
        if (exe == null) throw new InvalidOperationException("TeamViewer is not installed on this PC (https://www.teamviewer.com).");

        var info = new ProcessStartInfo(exe) { UseShellExecute = false };
        info.ArgumentList.Add("-i");
        info.ArgumentList.Add(id);
        if (password.Length > 0)
        {
            info.ArgumentList.Add("--Password");
            info.ArgumentList.Add(password);
        }
        Process.Start(info);
    }

    public IReadOnlyList<PuttySession>? ReadPuttySessions()
    {
        using var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\SimonTatham\PuTTY\Sessions");
        if (root == null) return Array.Empty<PuttySession>();
        var list = new List<PuttySession>();
        foreach (var name in root.GetSubKeyNames())
        {
            using var k = root.OpenSubKey(name);
            if (k == null) continue;
            list.Add(new PuttySession(
                Uri.UnescapeDataString(name),
                k.GetValue("HostName") as string ?? "",
                k.GetValue("PortNumber") is int port ? port : 22,
                k.GetValue("Protocol") as string ?? "ssh",
                k.GetValue("UserName") as string ?? ""));
        }
        return list;
    }

    private int _installing;

    public async Task InstallUpdateAsync(string setupUrl, string version, string? expectedSha256 = null)
    {
        if (!Core.Update.UpdateChecker.IsGitHubUrl(setupUrl, Core.Update.UpdateChecker.Repository))
            throw new InvalidOperationException("Refusing to download update from an untrusted URL.");
        if (Interlocked.Exchange(ref _installing, 1) != 0)
            throw new InvalidOperationException("An update download is already in progress.");

        var safeVersion = Core.Update.UpdateChecker.SanitizeVersionForFileName(version);
        var file = Path.Combine(Path.GetTempPath(), "MultiServerView-Setup-" + safeVersion + ".exe");
        var part = file + "." + Guid.NewGuid().ToString("N")[..8] + ".part";
        try
        {
            using (var response = await Http.GetAsync(setupUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? "";
                if (!Core.Update.UpdateChecker.IsGitHubUrl(finalUrl, Core.Update.UpdateChecker.Repository))
                    throw new InvalidOperationException("Update download redirected to an untrusted host.");
                await using (var fs = File.Create(part))
                {
                    await response.Content.CopyToAsync(fs);
                }
                if (response.Content.Headers.ContentLength is { } expected && new FileInfo(part).Length != expected)
                    throw new IOException("The update download was incomplete. Please try again.");
            }
            if (!string.IsNullOrEmpty(expectedSha256))
            {
                await using var checkStream = File.OpenRead(part);
                var actual = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(checkStream));
                if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The downloaded update failed SHA-256 verification.");
            }
            File.Move(part, file, overwrite: true);
            // Inno Setup: silent, closes this app, starts the new version when done (/relaunch=1 triggers the silent [Run] entry).
            Process.Start(new ProcessStartInfo(file, "/SILENT /CLOSEAPPLICATIONS /NOCANCEL /relaunch=1") { UseShellExecute = true });
            _window("close");
        }
        catch
        {
            try { if (File.Exists(part)) File.Delete(part); } catch { /* ignore cleanup errors */ }
            throw;
        }
        finally
        {
            Interlocked.Exchange(ref _installing, 0);
        }
    }
}
