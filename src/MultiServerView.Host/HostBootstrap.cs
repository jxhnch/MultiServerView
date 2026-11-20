using MultiServerView.Core;
using MultiServerView.Core.Sync;

namespace MultiServerView.Host;

/// <summary>Creates the bridge with all its stores for a data folder. Used by every platform app.</summary>
public static class HostBootstrap
{
    /// <param name="rdpFactory">Creates the RDP hosting of the platform; gets a function that returns the current settings.</param>
    public static Bridge Create(string dataDir, IHostPlatform platform, Action<object> emit, Func<Func<AppSettings>, IRdpController> rdpFactory)
    {
        var vault = new VaultStore(Path.Combine(dataDir, "vault.msv"));
        var sourceSettings = new SyncSettingsStore(Path.Combine(dataDir, "sync.json"), platform.Secrets);
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var source = new SourceService(vault, sourceSettings, cfg => new SupabaseClient(http, cfg.Url, cfg.AnonKey));
        var appSettings = new AppSettingsStore(Path.Combine(dataDir, "settings.json"), platform.Secrets);
        var rdp = rdpFactory(() => appSettings.Current);
        var history = new HistoryStore(Path.Combine(dataDir, "history.json"));
        return new Bridge(vault, source, history, appSettings, rdp, emit, platform);
    }
}
