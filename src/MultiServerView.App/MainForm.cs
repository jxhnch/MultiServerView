using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using MultiServerView.Core;

namespace MultiServerView.App;

public sealed class MainForm : Form
{
    private const string Host = "app.msv"; // virtueller Hostname fuer das gebaute UI

    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(15, 17, 21) };
    private readonly Bridge _bridge;

    public MainForm()
    {
        Text = "MultiServerView";
        Width = 1280;
        Height = 800;
        MinimumSize = new Size(900, 560);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(15, 17, 21);

        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MultiServerView");
        _bridge = new Bridge(new VaultStore(Path.Combine(dataDir, "vault.msv")));

        Controls.Add(_web);
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        try
        {
            var userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MultiServerView", "webview");
            var env = await CoreWebView2Environment.CreateAsync(null, userData);
            await _web.EnsureCoreWebView2Async(env);

            var core = _web.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            // Ctrl+Tab, Ctrl+W, Ctrl+1..9 usw. gehoeren dem UI und nicht dem Browser.
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.WebMessageReceived += OnWebMessage;

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
        // Vault-Operationen (PBKDF2) nicht auf dem UI-Thread rechnen.
        var reply = await Task.Run(() => _bridge.Handle(request));
        _web.CoreWebView2.PostWebMessageAsJson(reply);
    }
}
