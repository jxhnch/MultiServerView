using System.ComponentModel;
using System.Diagnostics;
using MultiServerView.Core;

namespace MultiServerView.App.Rdp;

/// <summary>
/// Hosts the native Microsoft RDP control (mstscax, the same ActiveX as in mstsc and mRemoteNG)
/// as a child control in the main window - exactly above the area the UI reserves for the session.
/// Everything here runs on the UI thread; the bridge calls from background threads, hence Invoke.
///
/// The control is addressed via late binding (dynamic). This way the project needs no
/// generated interop assemblies (aximp), and a missing feature (e.g. an older Windows version)
/// does not crash but is skipped.
/// </summary>
internal sealed class RdpManager : IRdpController, IDisposable
{
    private readonly Control _owner;
    private readonly Action<object> _emit;
    private readonly Func<AppSettings> _settings;
    private readonly Dictionary<string, RdpSession> _sessions = new();
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 500 };
    private bool _suspended;

    /// <summary>Measurements of the last close (for the self-test).</summary>
    public string LastCloseTimings { get; private set; } = "";

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

    // ------------------------------------------------------------------ IRdpController (thread-safe)

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
        CloseCore(id); // reconnect with the same id

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
            ocx = host.GetOcx() ?? throw new InvalidOperationException("The RDP control could not be created.");
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException
                                       or System.ComponentModel.InvalidAsynchronousStateException
                                       or System.Windows.Forms.AxHost.InvalidActiveXStateException)
        {
            throw new InvalidOperationException(
                "The Windows RDP control (mstscax) is not available. " +
                "You can use \"Open in Windows RDP\" (mstsc) instead.", ex);
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
            throw new InvalidOperationException("The RDP connection could not be started: " + ex.Message, ex);
        }
        host.Focus();
    }

    private static void Configure(dynamic ocx, ServerEntry server, Size size, AppSettings settings)
    {
        Try(() => ocx.Server = server.Host);

        // "DOMAIN\user" or "user@domain" -> set the domain separately
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
        Try(() => ocx.AdvancedSettings9.PerformanceFlags = 0x0); // full quality (background, animations, ...)
        Try(() => ocx.AdvancedSettings9.BitmapPeristence = 1);

        // Password: can only be set via the non-scriptable interface (never as a plain-text property).
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

    /// <summary>Adjusts the resolution of the remote session to the window size (only once dragging has stopped).</summary>
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
            // Only works with a connected session and servers from Windows 8.1; otherwise it stays with SmartSizing (scaled).
            Try(() =>
            {
                if (Convert.ToInt32(s.Ocx.Connected) == 1)
                    s.Ocx.UpdateSessionDisplaySettings((uint)size.Width, (uint)size.Height, 0u, 0u, 0u, 100u, 100u);
            });
        };
        timer.Start();
    }

    private void CloseCore(string id, bool disconnect = true)
    {
        if (!_sessions.Remove(id, out var s)) return;
        s.ResizeTimer?.Stop();
        s.ResizeTimer?.Dispose();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (disconnect) Try(() => { if (Convert.ToInt32(s.Ocx.Connected) != 0) s.Ocx.Disconnect(); });
        var tDisconnect = sw.ElapsedMilliseconds;
        s.Host.Visible = false;
        _owner.Controls.Remove(s.Host);
        s.Host.Dispose();
        LastCloseTimings = $"Disconnect {tDisconnect} ms, release {sw.ElapsedMilliseconds - tDisconnect} ms";
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

            // Before the first connection attempt Connected is briefly 0 - that is not a disconnect yet.
            if (state == 0 && !s.SawActive && (DateTime.UtcNow - s.Started).TotalSeconds < 4) continue;

            s.LastState = state;
            _emit(new
            {
                @event = "rdp.status",
                sessionId = s.Id,
                state = state switch { 1 => "connected", 2 => "connecting", _ => "disconnected" },
                message = state == 0 ? "Connection closed or failed." : ""
            });
        }
    }

    private static void Try(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException
                                       or InvalidCastException or NotSupportedException or ArgumentException)
        {
            Debug.WriteLine("RDP: setting skipped: " + ex.Message);
        }
    }

    public void Dispose()
    {
        _poll.Stop();
        _poll.Dispose();
        // Do not disconnect cleanly on exit: Disconnect() can block on a session that is still connecting.
        foreach (var id in _sessions.Keys.ToList()) CloseCore(id, disconnect: false);
    }
}
