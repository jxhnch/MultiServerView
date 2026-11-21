using System.Drawing;
using MultiServerView.Core;
using MultiServerView.Core.Import;
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

    /// <summary>Downloads and starts the installer of a new version and closes the app.</summary>
    Task InstallUpdateAsync(string setupUrl, string version, string? expectedSha256 = null);
}
