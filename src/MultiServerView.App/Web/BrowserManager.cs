using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using MultiServerView.Core;
using MultiServerView.Core.Web;
using MultiServerView.Host;

namespace MultiServerView.App.Web;

/// <summary>
/// The integrated browser on Windows: a real WebView2 per "Web" tab, placed over the tab area (like the RDP control).
/// The pages are shown unchanged. It is separate from the WebView2 of the app itself: no access to the app, an own private
/// profile (nothing is written to disk) and no messages from the page to the host.
/// </summary>
internal sealed class BrowserManager : IBrowserController, IDisposable
{
    private readonly Control _owner;
    private readonly Action<object> _emit;
    private readonly string _userDataFolder;
    private readonly Dictionary<string, Session> _sessions = new();
    private bool _suspended;

    private sealed class Session
    {
        public required string Id { get; init; }
        public required BrowserPage Page { get; init; }
        public required WebView2 View { get; init; }
        public bool WantVisible { get; set; } = true;
        public bool BasicTried { get; set; }
        public string CertificateProblem { get; set; } = "";
    }

    public BrowserManager(Control owner, Action<object> emit, string userDataFolder)
    {
        _owner = owner;
        _emit = emit;
        _userDataFolder = userDataFolder;
    }

    // ------------------------------------------------------------------ IBrowserController (thread-safe)

    public void Open(string sessionId, BrowserPage page, Rectangle bounds) => OnUi(() => OpenCore(sessionId, page, bounds));

    public void Layout(string sessionId, Rectangle bounds, bool visible) => OnUi(() =>
    {
        if (!_sessions.TryGetValue(sessionId, out var s)) return;
        s.WantVisible = visible;
        if (visible) s.View.Bounds = bounds;
        var show = visible && !_suspended;
        var appeared = show && !s.View.Visible;
        s.View.Visible = show;
        if (show) s.View.BringToFront();
        if (appeared) s.View.Focus();
    });

    public void Command(string sessionId, string command, string url) => OnUi(() =>
    {
        if (!_sessions.TryGetValue(sessionId, out var s) || s.View.CoreWebView2 is not { } core) return;
        switch (command)
        {
            case "back": if (core.CanGoBack) core.GoBack(); break;
            case "forward": if (core.CanGoForward) core.GoForward(); break;
            case "reload": s.CertificateProblem = ""; core.Reload(); break;
            case "home": s.CertificateProblem = ""; core.Navigate(s.Page.Url); break;
            case "go":
                if (WebAddress.TryNormalize(url, out var address)) core.Navigate(address);
                break;
            case "external":
                if (Uri.TryCreate(core.Source, UriKind.Absolute, out var current) && current.Scheme is "http" or "https")
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(current.AbsoluteUri) { UseShellExecute = true })?.Dispose();
                break;
        }
    });

    public void Close(string sessionId) => OnUi(() => CloseCore(sessionId));

    public void CloseAll() => OnUi(() =>
    {
        foreach (var id in _sessions.Keys.ToList()) CloseCore(id);
    });

    public void SetSuspended(bool suspended) => OnUi(() =>
    {
        _suspended = suspended;
        foreach (var s in _sessions.Values) s.View.Visible = s.WantVisible && !_suspended;
    });

    public void Dispose()
    {
        foreach (var id in _sessions.Keys.ToList()) CloseCore(id);
    }

    // ------------------------------------------------------------------ UI thread

    private void OnUi(Action action)
    {
        if (_owner.IsDisposed) return;
        if (_owner.InvokeRequired) _owner.Invoke(action);
        else action();
    }

    private void OpenCore(string id, BrowserPage page, Rectangle bounds)
    {
        CloseCore(id); // reconnect with the same id
        var view = new WebView2
        {
            Bounds = bounds,
            DefaultBackgroundColor = Color.White,
            CreationProperties = new CoreWebView2CreationProperties
            {
                UserDataFolder = _userDataFolder,
                ProfileName = "web-" + new string(page.ProfileKey.Where(char.IsAsciiLetterOrDigit).Take(40).ToArray()),
                IsInPrivateModeEnabled = true
            }
        };
        var session = new Session { Id = id, Page = page, View = view };
        _sessions[id] = session;
        _owner.Controls.Add(view);
        view.Visible = !_suspended;
        view.BringToFront();
        _ = StartAsync(session);
    }

    private async Task StartAsync(Session s)
    {
        try
        {
            await s.View.EnsureCoreWebView2Async();
            if (!IsCurrent(s)) return;
            var core = s.View.CoreWebView2;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.AreDevToolsEnabled = false;

            core.ServerCertificateErrorDetected += (_, e) => OnCertificateError(s, e);
            core.BasicAuthenticationRequested += (_, e) =>
            {
                // The saved login is only ever given to the device itself, and only once (a wrong one then shows the dialog).
                if (s.BasicTried || s.Page.Password.Length == 0 || !IsDevice(s, e.Uri)) return;
                s.BasicTried = true;
                e.Response.UserName = s.Page.Username;
                e.Response.Password = s.Page.Password;
            };
            core.DOMContentLoaded += async (_, _) =>
            {
                // Only on the device's own pages: following a link to another site never fills in the password there.
                if (!IsCurrent(s) || !IsDevice(s, core.Source)) return;
                var script = WebRewriter.BuildAutoLoginJs(s.Page.Username, s.Page.Password, s.Page.Origin);
                if (script.Length == 0) return;
                try { await core.ExecuteScriptAsync(script); }
                catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { /* page already gone */ }
            };
            core.NavigationStarting += (_, _) => Report(s, "connecting");
            core.NavigationCompleted += (_, _) => Report(s, s.CertificateProblem.Length > 0 ? "error" : "connected");
            core.SourceChanged += (_, _) => Report(s, null);
            core.Navigate(s.Page.Url);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!IsCurrent(s)) return;
            _emit(new { @event = "web.status", sessionId = s.Id, state = "error", message = "The integrated browser could not be started: " + ex.Message, url = "", canGoBack = false, canGoForward = false });
        }
    }

    private bool IsCurrent(Session s) => _sessions.TryGetValue(s.Id, out var now) && ReferenceEquals(now, s);

    private static bool IsDevice(Session s, string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && string.Equals(uri.GetLeftPart(UriPartial.Authority), s.Page.Origin, StringComparison.OrdinalIgnoreCase);

    private void Report(Session s, string? state)
    {
        if (!IsCurrent(s) || s.View.CoreWebView2 is not { } core) return;
        _emit(new
        {
            @event = "web.status",
            sessionId = s.Id,
            state = state ?? "",
            message = s.CertificateProblem,
            url = core.Source,
            canGoBack = core.CanGoBack,
            canGoForward = core.CanGoForward
        });
    }

    /// <summary>
    /// Devices usually have self-signed certificates. The device's own certificate is treated like an SSH host key: accepted and
    /// saved the first time, accepted later only if unchanged. Certificate errors of any other address stay errors.
    /// </summary>
    private void OnCertificateError(Session s, CoreWebView2ServerCertificateErrorDetectedEventArgs e)
    {
        e.Action = CoreWebView2ServerCertificateErrorAction.Default;
        if (!IsDevice(s, e.RequestUri)) return;
        using var certificate = X509Certificate2.CreateFromPem(e.ServerCertificate.ToPemEncoding());
        var fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(certificate.RawData)).TrimEnd('=');
        if (s.Page.CheckCertificate(fingerprint) == HostKeyTrust.Mismatch)
        {
            s.CertificateProblem = s.Page.MismatchMessage;
            e.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
        }
        else e.Action = CoreWebView2ServerCertificateErrorAction.AlwaysAllow;
    }

    private void CloseCore(string id)
    {
        if (!_sessions.Remove(id, out var s)) return;
        _owner.Controls.Remove(s.View);
        s.View.Dispose();
    }
}
