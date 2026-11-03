using System.ComponentModel;
using System.Diagnostics;
using MultiServerView.Core;

namespace MultiServerView.App.Rdp;

/// <summary>
/// Hostet das native Microsoft-RDP-Control (mstscax, derselbe ActiveX wie bei mstsc und mRemoteNG)
/// als Kind-Control im Hauptfenster - genau über dem Bereich, den das UI für die Session reserviert.
/// Alles hier läuft auf dem UI-Thread; die Bridge ruft von Hintergrund-Threads auf, deshalb Invoke.
///
/// Das Control wird per Late-Binding (dynamic) angesprochen. So braucht das Projekt keine
/// generierten Interop-Assemblies (aximp), und ein fehlendes Feature (z. B. ältere Windows-Version)
/// führt nicht zum Absturz, sondern wird übersprungen.
/// </summary>
internal sealed class RdpManager : IRdpController, IDisposable
{
    private readonly Control _owner;
    private readonly Action<object> _emit;
    private readonly Func<AppSettings> _settings;
    private readonly Dictionary<string, RdpSession> _sessions = new();
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 500 };
    private bool _suspended;

    public RdpManager(Control owner, Action<object> emit, Func<AppSettings> settings)
    {
        _owner = owner;
        _emit = emit;
        _settings = settings;
        _poll.Tick += (_, _) => Poll();
        _poll.Start();
    }

    private sealed class RdpSession
    {
        public required string Id { get; init; }
        public required RdpAxHost Host { get; init; }
        public required dynamic Ocx { get; init; }
        public bool WantVisible { get; set; }
        public int LastState { get; set; } = -1;
        public bool SawActive { get; set; }
        public DateTime Started { get; } = DateTime.UtcNow;
        public Size LastRequestedSize { get; set; }
        public System.Windows.Forms.Timer? ResizeTimer { get; set; }
    }

    // ------------------------------------------------------------------ IRdpController (Thread-sicher)

    public void Open(string sessionId, ServerEntry server, Rectangle bounds) =>
        _owner.Invoke(new Action(() => OpenCore(sessionId, server, bounds)));

    public void Layout(string sessionId, Rectangle bounds, bool visible) =>
        _owner.Invoke(new Action(() => LayoutCore(sessionId, bounds, visible)));

    public void Close(string sessionId) =>
        _owner.Invoke(new Action(() => CloseCore(sessionId)));

    public void SetSuspended(bool suspended) =>
        _owner.Invoke(new Action(() =>
        {
            _suspended = suspended;
            foreach (var s in _sessions.Values) s.Host.Visible = s.WantVisible && !_suspended;
        }));

    // ------------------------------------------------------------------ UI-Thread

    private void OpenCore(string id, ServerEntry server, Rectangle bounds)
    {
        CloseCore(id); // Reconnect mit gleicher Id

        RdpAxHost host;
        dynamic ocx;
        try
        {
            host = new RdpAxHost();
            ((ISupportInitialize)host).BeginInit();
            host.Bounds = bounds;
            _owner.Controls.Add(host);
            ((ISupportInitialize)host).EndInit();
            host.CreateControl();
            ocx = host.GetOcx() ?? throw new InvalidOperationException("RDP-Control konnte nicht erzeugt werden.");
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException
                                       or System.ComponentModel.InvalidAsynchronousStateException
                                       or System.Windows.Forms.AxHost.InvalidActiveXStateException)
        {
            throw new InvalidOperationException(
                "Das Windows-RDP-Control (mstscax) ist nicht verfügbar. " +
                "Du kannst stattdessen «In Windows-RDP öffnen» (mstsc) benutzen.", ex);
        }

        var session = new RdpSession { Id = id, Host = host, Ocx = ocx, WantVisible = true };
        _sessions[id] = session;

        Configure(ocx, server, bounds.Size, _settings());
        host.Visible = !_suspended;
        host.BringToFront();

        _emit(new { @event = "rdp.status", sessionId = id, state = "connecting", message = "" });
        try
        {
            ocx.Connect();
        }
        catch (Exception ex)
        {
            CloseCore(id);
            throw new InvalidOperationException("RDP-Verbindung konnte nicht gestartet werden: " + ex.Message, ex);
        }
        host.Focus();
    }

    private static void Configure(dynamic ocx, ServerEntry server, Size size, AppSettings settings)
    {
        Try(() => ocx.Server = server.Host);

        // "DOMAIN\user" oder "user@domain" -> Domain getrennt setzen
        var user = server.Username ?? "";
        var domain = "";
        var slash = user.IndexOf('\\');
        if (slash > 0)
        {
            domain = user[..slash];
            user = user[(slash + 1)..];
        }
        Try(() => ocx.UserName = user);
        if (domain.Length > 0) Try(() => ocx.Domain = domain);

        Try(() => ocx.DesktopWidth = size.Width);
        Try(() => ocx.DesktopHeight = size.Height);
        Try(() => ocx.ColorDepth = settings.RdpColorDepth);
        Try(() => ocx.AdvancedSettings9.RDPPort = server.Port > 0 ? server.Port : 3389);
        Try(() => ocx.AdvancedSettings9.SmartSizing = true);
        Try(() => ocx.AdvancedSettings9.EnableCredSspSupport = true);
        Try(() => ocx.AdvancedSettings9.RedirectClipboard = settings.RdpClipboard);
        Try(() => ocx.AdvancedSettings9.RedirectDrives = settings.RdpDrives);
        Try(() => ocx.AdvancedSettings9.AuthenticationLevel = settings.RdpWarnOnCertificate ? 2u : 0u);
        Try(() => ocx.AdvancedSettings9.PerformanceFlags = 0x0); // volle Qualität (Hintergrund, Animationen, ...)
        Try(() => ocx.AdvancedSettings9.BitmapPeristence = 1);

        // Passwort: nur über das Non-Scriptable-Interface setzbar (nie als Klartext-Property).
        if (!string.IsNullOrEmpty(server.Password))
        {
            Try(() =>
            {
                var ns = (IMsTscNonScriptable)ocx;
                ns.ClearTextPassword = server.Password;
            });
        }
    }

    private void LayoutCore(string id, Rectangle bounds, bool visible)
    {
        if (!_sessions.TryGetValue(id, out var s)) return;

        s.WantVisible = visible;
        if (visible)
        {
            s.Host.Bounds = bounds;
            s.Host.Visible = !_suspended;
            s.Host.BringToFront();
            if (_settings().RdpDynamicResize) QueueResolutionUpdate(s, bounds.Size);
        }
        else
        {
            s.Host.Visible = false;
        }
    }

    /// <summary>Passt die Auflösung der Remote-Sitzung an die Fenstergrösse an (erst wenn das Ziehen aufgehört hat).</summary>
    private void QueueResolutionUpdate(RdpSession s, Size size)
    {
        if (size == s.LastRequestedSize || size.Width < 200 || size.Height < 150) return;
        s.LastRequestedSize = size;

        s.ResizeTimer?.Stop();
        s.ResizeTimer?.Dispose();
        var timer = new System.Windows.Forms.Timer { Interval = 450 };
        s.ResizeTimer = timer;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            if (!_sessions.ContainsKey(s.Id)) return;
            // Klappt nur bei verbundener Session und Servern ab Windows 8.1; sonst bleibt es bei SmartSizing (skaliert).
            Try(() =>
            {
                if (Convert.ToInt32(s.Ocx.Connected) == 1)
                    s.Ocx.UpdateSessionDisplaySettings((uint)size.Width, (uint)size.Height, 0u, 0u, 0u, 100u, 100u);
            });
        };
        timer.Start();
    }

    private void CloseCore(string id)
    {
        if (!_sessions.Remove(id, out var s)) return;
        s.ResizeTimer?.Stop();
        s.ResizeTimer?.Dispose();
        Try(() => { if (Convert.ToInt32(s.Ocx.Connected) != 0) s.Ocx.Disconnect(); });
        _owner.Controls.Remove(s.Host);
        s.Host.Dispose();
    }

    private void Poll()
    {
        foreach (var s in _sessions.Values.ToList())
        {
            int state;
            try { state = Convert.ToInt32(s.Ocx.Connected); }
            catch { state = 0; }

            if (state == s.LastState) continue;
            if (state != 0) s.SawActive = true;

            // Vor dem ersten Verbindungsaufbau steht Connected kurz auf 0 - das ist noch kein Trennen.
            if (state == 0 && !s.SawActive && (DateTime.UtcNow - s.Started).TotalSeconds < 4) continue;

            s.LastState = state;
            _emit(new
            {
                @event = "rdp.status",
                sessionId = s.Id,
                state = state switch { 1 => "connected", 2 => "connecting", _ => "disconnected" },
                message = state == 0 ? "Verbindung getrennt oder fehlgeschlagen." : ""
            });
        }
    }

    private static void Try(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException
                                       or InvalidCastException or NotSupportedException or ArgumentException)
        {
            Debug.WriteLine("RDP: Einstellung übersprungen: " + ex.Message);
        }
    }

    public void Dispose()
    {
        _poll.Stop();
        _poll.Dispose();
        foreach (var id in _sessions.Keys.ToList()) CloseCore(id);
    }
}
