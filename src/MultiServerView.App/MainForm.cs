using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using MultiServerView.App.Rdp;
using MultiServerView.Core;
using MultiServerView.Core.Sync;

namespace MultiServerView.App;

public sealed partial class MainForm : Form
{
    private const string Host = "app.msv"; // virtual host name for the built UI
    private static readonly Color Background = Color.FromArgb(14, 16, 21);

    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Background };
    private readonly Bridge _bridge;
    private readonly RdpManager _rdp;

    private readonly string? _smokeDir;

    public MainForm(string? smokeDir = null)
    {
        _smokeDir = smokeDir;
        Text = "MultiServerView";
        Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? Icon;
        Width = 1360;
        Height = 860;
        MinimumSize = new Size(960, 600);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Background;

        // MSV_DATA_DIR: own data folder (portable mode or self-test)
        var dataOverride = Environment.GetEnvironmentVariable("MSV_DATA_DIR");
        var roaming = !string.IsNullOrWhiteSpace(dataOverride)
            ? dataOverride
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MultiServerView");
        var vault = new VaultStore(Path.Combine(roaming, "vault.msv"));
        var settings = new SyncSettingsStore(Path.Combine(roaming, "sync.json"), new DpapiProtector());
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var sync = new SyncService(vault, settings, cfg => new SupabaseClient(http, cfg.Url, cfg.AnonKey));

        var appSettings = new AppSettingsStore(Path.Combine(roaming, "settings.json"));

        Controls.Add(_web);
        _rdp = new RdpManager(this, Emit, () => appSettings.Current);
        var history = new HistoryStore(Path.Combine(roaming, "history.json"));
        _bridge = new Bridge(vault, sync, history, appSettings, _rdp, Emit, PickSavePath, PickFolder, WindowCommand);
    }

    /// <summary>Shows the "Save as" dialog (runs on the UI thread). null = cancelled.</summary>
    private Task<string?> PickSavePath(string suggestedName)
    {
        var tcs = new TaskCompletionSource<string?>();
        BeginInvoke(() =>
        {
            using var dialog = new SaveFileDialog
            {
                FileName = suggestedName,
                Filter = "MultiServerView backup (*.msv)|*.msv|All files (*.*)|*.*",
                Title = "Save vault backup"
            };
            tcs.SetResult(dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null);
        });
        return tcs.Task;
    }

    /// <summary>Shows the folder dialog (runs on the UI thread). null = cancelled.</summary>
    private Task<string?> PickFolder(string description)
    {
        var tcs = new TaskCompletionSource<string?>();
        BeginInvoke(() =>
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = description,
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true
            };
            tcs.SetResult(dialog.ShowDialog(this) == DialogResult.OK ? dialog.SelectedPath : null);
        });
        return tcs.Task;
    }

    /// <summary>Sends an event to the UI (thread-safe).</summary>
    private void Emit(object payload)
    {
        var json = JsonSerializer.Serialize(payload, Json.Options);
        if (_smokeDir != null) lock (_tap) _tap.Add(json);
        void Post()
        {
            if (!IsDisposed && _web.CoreWebView2 != null) _web.CoreWebView2.PostWebMessageAsJson(json);
        }

        if (InvokeRequired)
        {
            try { BeginInvoke(Post); }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { /* window is closing */ }
        }
        else Post();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Dark window (Windows 10 20H1+ / 11); on older systems nothing happens.
        var dark = 1;
        _ = DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
        _ = DwmSetWindowAttribute(Handle, 19, ref dark, sizeof(int));

        // Windows 11: border and the thin strip at the top in the color of the tab bar so it blends seamlessly into the title bar.
        var bar = BarColorRef;
        _ = DwmSetWindowAttribute(Handle, DwmwaBorderColor, ref bar, sizeof(int));
        _ = DwmSetWindowAttribute(Handle, DwmwaCaptionColor, ref bar, sizeof(int));

        // Recalculate once after the window is created so the custom title bar applies immediately.
        SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpFrameChanged);
    }

    // ---------------------------------------------------------------- Custom title bar
    // The window keeps its frame (move, resize, snap, shadow, rounded corners), but the Windows
    // title bar is gone: the tab bar of the UI is the title bar and has its own window buttons.

    private const int WmNcCalcSize = 0x0083;
    private const int ResizeStrip = 4; // thin strip at the top where the window can be resized
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const uint SwpNoSize = 0x1, SwpNoMove = 0x2, SwpNoZOrder = 0x4, SwpFrameChanged = 0x20;
    private const int SmCySizeFrame = 33, SmCxPaddedBorder = 92;

    /// <summary>Color of the tab bar (#182033) as COLORREF (0x00BBGGRR).</summary>
    private static int BarColorRef => 0x33 << 16 | 0x20 << 8 | 0x18;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmNcCalcSize && m.WParam != IntPtr.Zero)
        {
            // Let the default calculate (border left, right, bottom) and then only remove the title bar at the top.
            var windowTop = Marshal.ReadInt32(m.LParam, 4);
            base.WndProc(ref m);
            // IsZoomed instead of WindowState: WinForms only switches WindowState after this message.
            var top = IsZoomed(m.HWnd)
                ? windowTop + GetSystemMetricsForDpi(SmCySizeFrame, (uint)DeviceDpi) + GetSystemMetricsForDpi(SmCxPaddedBorder, (uint)DeviceDpi)
                : windowTop + ResizeStrip;
            Marshal.WriteInt32(m.LParam, 4, top);
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        EmitWindowState();
    }

    private bool _lastMaximized;

    private void EmitWindowState()
    {
        var maximized = WindowState == FormWindowState.Maximized;
        if (maximized == _lastMaximized) return;
        _lastMaximized = maximized;
        Emit(new { @event = "window.state", maximized });
    }

    /// <summary>Window commands of the custom title bar. "state" only returns whether the window is maximized.</summary>
    private bool WindowCommand(string command)
    {
        if (IsDisposed) return false;
        if (command == "state") return WindowState == FormWindowState.Maximized;
        BeginInvoke(() =>
        {
            switch (command)
            {
                case "minimize": WindowState = FormWindowState.Minimized; break;
                case "maximize": WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized; break;
                case "close": Close(); break;
            }
        });
        return false;
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        try
        {
            var userData = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MSV_DATA_DIR"))
                ? Path.Combine(Environment.GetEnvironmentVariable("MSV_DATA_DIR")!, "webview")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MultiServerView", "webview");
            var env = await CoreWebView2Environment.CreateAsync(null, userData);
            await _web.EnsureCoreWebView2Async(env);

            var core = _web.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = Environment.GetEnvironmentVariable("MSV_DEV_URL") != null;
            // Ctrl+Tab, Ctrl+W, Ctrl+1..9 etc. belong to the UI and not to the browser.
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            // CSS "app-region: drag" on the tab bar makes it the title bar (drag, double-click, snap).
            core.Settings.IsNonClientRegionSupportEnabled = true;
            core.WebMessageReceived += OnWebMessage;
            if (_smokeDir != null)
                core.NavigationCompleted += (_, _) =>
                {
                    if (_smokeStarted) return;
                    _smokeStarted = true;
                    _ = RunSmokeAsync();
                };

            // Allow the clipboard (terminal: paste via right click / Ctrl+Shift+V) without asking.
            core.PermissionRequested += (_, args) =>
            {
                if (args.PermissionKind is CoreWebView2PermissionKind.ClipboardRead)
                    args.State = CoreWebView2PermissionState.Allow;
            };

            // Open external links in the default browser, never in the app window.
            core.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) && (uri.Scheme is "http" or "https"))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
            };

            // Dev mode: `set MSV_DEV_URL=http://localhost:5173` -> Vite dev server with hot reload.
            var devUrl = Environment.GetEnvironmentVariable("MSV_DEV_URL");
            if (!string.IsNullOrWhiteSpace(devUrl))
            {
                core.Navigate(devUrl);
            }
            else
            {
                var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
                core.SetVirtualHostNameToFolderMapping(Host, wwwroot, CoreWebView2HostResourceAccessKind.Allow);
                core.Navigate($"https://{Host}/index.html");
            }
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(
                "The WebView2 runtime is missing. Please install it: https://developer.microsoft.com/microsoft-edge/webview2/",
                "MultiServerView", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var request = e.WebMessageAsJson;
        // Everything except receiving the message runs in the background (PBKDF2, SSH connect, HTTP, ...).
        var reply = await Task.Run(() => _bridge.HandleAsync(request));
        if (!IsDisposed && _web.CoreWebView2 != null) _web.CoreWebView2.PostWebMessageAsJson(reply);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Log("Window closed");

        // Watchdog in the background: no matter what hangs during cleanup (RDP control, open SSH channels) - after 3 seconds the process is gone.
        var exitCode = Environment.ExitCode;
        _ = Task.Run(async () =>
        {
            await Task.Delay(3000);
            Log("Cleanup took too long - exiting hard");
            Environment.Exit(exitCode);
        });

        try { _rdp.Dispose(); Log("RDP cleaned up"); } catch (Exception ex) { Log("RDP error: " + ex.Message); }

        // Cleaning up the network sessions can block on a hanging connection (closing SSH channels).
        // Therefore in the background and for at most 3 seconds - after that the process exits in any case.
        var cleanup = Task.Run(() =>
        {
            try { _bridge.Dispose(); Log("Bridge cleaned up"); } catch (Exception ex) { Log("Bridge error: " + ex.Message); }
        });
        cleanup.Wait(TimeSpan.FromSeconds(2));
        base.OnFormClosed(e);

        // Everything is saved and cleaned up: exit immediately. This way no invisible process with an unlocked
        // vault in memory is left behind after the window is closed (WebView2 would otherwise keep the process open for seconds).
        Log("exiting process");
        Environment.Exit(Environment.ExitCode);
    }

    private void Log(string message)
    {
        try
        {
            if (_smokeDir != null) File.AppendAllText(Path.Combine(_smokeDir, "exit.log"), $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch { /* the log is not critical */ }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
