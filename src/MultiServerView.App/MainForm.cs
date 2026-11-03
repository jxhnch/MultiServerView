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
    private const string Host = "app.msv"; // virtueller Hostname für das gebaute UI
    private static readonly Color Background = Color.FromArgb(14, 16, 21);

    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Background };
    private readonly Bridge _bridge;
    private readonly RdpManager _rdp;

    private readonly string? _smokeDir;

    public MainForm(string? smokeDir = null)
    {
        _smokeDir = smokeDir;
        Text = "MultiServerView";
        Width = 1360;
        Height = 860;
        MinimumSize = new Size(960, 600);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Background;

        // MSV_DATA_DIR: eigener Datenordner (portabler Betrieb oder Selbsttest)
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
        _bridge = new Bridge(vault, sync, history, appSettings, _rdp, Emit, PickSavePath);
    }

    /// <summary>Zeigt den «Speichern unter»-Dialog (läuft auf dem UI-Thread). null = abgebrochen.</summary>
    private Task<string?> PickSavePath(string suggestedName)
    {
        var tcs = new TaskCompletionSource<string?>();
        BeginInvoke(() =>
        {
            using var dialog = new SaveFileDialog
            {
                FileName = suggestedName,
                Filter = "MultiServerView-Backup (*.msv)|*.msv|Alle Dateien (*.*)|*.*",
                Title = "Vault-Backup speichern"
            };
            tcs.SetResult(dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null);
        });
        return tcs.Task;
    }

    /// <summary>Schickt ein Event ans UI (thread-sicher).</summary>
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
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { /* Fenster schliesst gerade */ }
        }
        else Post();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Dunkle Titelleiste (Windows 10 20H1+ / 11); auf älteren Systemen passiert einfach nichts.
        var dark = 1;
        _ = DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
        _ = DwmSetWindowAttribute(Handle, 19, ref dark, sizeof(int));
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
            // Ctrl+Tab, Ctrl+W, Ctrl+1..9 usw. gehören dem UI und nicht dem Browser.
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.WebMessageReceived += OnWebMessage;
            if (_smokeDir != null)
                core.NavigationCompleted += (_, _) =>
                {
                    if (_smokeStarted) return;
                    _smokeStarted = true;
                    _ = RunSmokeAsync();
                };

            // Zwischenablage (Terminal: Einfügen per Rechtsklick / Ctrl+Shift+V) ohne Rückfrage erlauben.
            core.PermissionRequested += (_, args) =>
            {
                if (args.PermissionKind is CoreWebView2PermissionKind.ClipboardRead)
                    args.State = CoreWebView2PermissionState.Allow;
            };

            // Externe Links im Standardbrowser öffnen, nie im App-Fenster.
            core.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) && (uri.Scheme is "http" or "https"))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
            };

            // Dev-Modus: `set MSV_DEV_URL=http://localhost:5173` -> Vite-Devserver mit Hot Reload.
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
                "Die WebView2-Runtime fehlt. Bitte installieren: https://developer.microsoft.com/microsoft-edge/webview2/",
                "MultiServerView", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var request = e.WebMessageAsJson;
        // Alles ausser dem Aufnehmen der Nachricht läuft im Hintergrund (PBKDF2, SSH-Connect, HTTP, ...).
        var reply = await Task.Run(() => _bridge.HandleAsync(request));
        if (!IsDisposed && _web.CoreWebView2 != null) _web.CoreWebView2.PostWebMessageAsJson(reply);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Log("Fenster geschlossen");
        try { _rdp.Dispose(); Log("RDP aufgeräumt"); } catch (Exception ex) { Log("RDP-Fehler: " + ex.Message); }

        // Das Aufräumen der Netzwerk-Sitzungen kann bei einer hängenden Verbindung blockieren (SSH-Kanäle schliessen).
        // Deshalb im Hintergrund und höchstens 3 Sekunden lang - danach wird in jedem Fall beendet.
        var cleanup = Task.Run(() =>
        {
            try { _bridge.Dispose(); Log("Bridge aufgeräumt"); } catch (Exception ex) { Log("Bridge-Fehler: " + ex.Message); }
        });
        if (!cleanup.Wait(TimeSpan.FromSeconds(3))) Log("Aufräumen dauerte zu lange - beende trotzdem");
        base.OnFormClosed(e);

        // Alles ist gespeichert und aufgeräumt: sofort beenden. So bleibt nach dem Schliessen des Fensters nie ein
        // unsichtbarer Prozess mit entsperrtem Vault im Speicher (WebView2 hielt den Prozess sonst noch Sekunden offen).
        Log("beende Prozess");
        Environment.Exit(Environment.ExitCode);
    }

    private void Log(string message)
    {
        try
        {
            if (_smokeDir != null) File.AppendAllText(Path.Combine(_smokeDir, "exit.log"), $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch { /* Protokoll ist nicht kritisch */ }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
