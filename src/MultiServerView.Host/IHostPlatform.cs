using System.Drawing;
using MultiServerView.Core;
using MultiServerView.Core.Import;
using MultiServerView.Core.Ssh;
using MultiServerView.Core.Sync;

namespace MultiServerView.Host;

/// <summary>What the bridge needs from the RDP hosting (WinForms, UI thread). Not available on every platform.</summary>
public interface IRdpController
{
    void Open(string sessionId, ServerEntry server, Rectangle bounds);
    void Layout(string sessionId, Rectangle bounds, bool visible);
    void Close(string sessionId);

    /// <summary>true = hide all RDP windows (a UI overlay is on top), false = show them again.</summary>
    void SetSuspended(bool suspended);
}

/// <summary>What the integrated browser needs to know about a "Web" host.</summary>
/// <param name="Url">The first page.</param>
/// <param name="Origin">scheme://host[:port] of the device; only there the saved login and certificate are used.</param>
/// <param name="CheckCertificate">Checks the fingerprint of a certificate the system does not trust (like an SSH host key).</param>
public sealed record BrowserPage(string Url, string Origin, string ProfileKey, string Username, string Password,
    Func<string, HostKeyTrust> CheckCertificate, string MismatchMessage);

/// <summary>A real browser inside a tab (Windows: WebView2). Platforms without one use the local web proxy.</summary>
public interface IBrowserController
{
    void Open(string sessionId, BrowserPage page, Rectangle bounds);
    void Layout(string sessionId, Rectangle bounds, bool visible);

    /// <summary>"back", "forward", "reload", "home", "go" (with an address) or "external".</summary>
    void Command(string sessionId, string command, string url);
    void Close(string sessionId);
    void CloseAll();
    void SetSuspended(bool suspended);
}

/// <summary>Everything that differs between Windows and Linux. The bridge itself is the same on both.</summary>
public interface IHostPlatform
{
    /// <summary>"windows" or "linux" (the UI adapts a few texts and the title bar).</summary>
    string Name { get; }

    /// <summary>The app draws its own title bar and window buttons (Windows). Otherwise the system title bar is used.</summary>
    bool CustomTitleBar { get; }

    /// <summary>Protects the tokens and passwords of the vault source on disk.</summary>
    ISecretProtector Secrets { get; }

    /// <summary>The installed app can replace itself with a new version (Windows installer).</summary>
    bool CanSelfUpdate { get; }

    Task<string?> PickSavePathAsync(string suggestedName);
    Task<string?> PickFolderAsync(string description);

    /// <summary>"state", "minimize", "maximize" or "close". Returns whether the window is maximized.</summary>
    bool WindowCommand(string command);

    /// <summary>Opens a file with the program the system associates with it.</summary>
    void OpenFile(string path);

    void RevealInFileManager(string path);

    /// <summary>Moves files and folders to the trash / recycle bin.</summary>
    void MoveToTrash(IReadOnlyList<string> paths);

    /// <summary>Opens the RDP client of the system (mstsc, xfreerdp, ...) for a host.</summary>
    void OpenExternalRdp(ServerEntry server);

    /// <summary>Starts the installed TeamViewer client for a partner ID. Throws if it is not installed.</summary>
    void StartTeamViewer(string id, string password);

    /// <summary>Saved PuTTY sessions of this PC, or null if the platform has none (Linux).</summary>
    IReadOnlyList<PuttySession>? ReadPuttySessions();

    /// <summary>The integrated browser of the platform, or null.</summary>
    IBrowserController? Browser => null;

    /// <summary>Downloads and starts the installer of a new version and closes the app.</summary>
    Task InstallUpdateAsync(string setupUrl, string version, string? expectedSha256 = null);
}
