using HtspTuner.LiveTv;
using MediaBrowser.Controller;

using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace HtspTuner;

/// <summary>
/// Registers the plugin's services. Jellyfin has no auto-discovery for these; a plugin must register
/// them here, which Jellyfin invokes before building the container.
/// </summary>
public class ServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc/>
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // HTSP is a native tuner: it shows under "Add Tuner Device" (multiple instances, one per Tvheadend
        // server) and, on validation, auto-registers a matching guide under "TV Guide Data Providers". The
        // tuner and guide share one instance so they reuse connections and channel ids. The plugin's own
        // settings page is only the fallback connection details for a tuner (until Jellyfin's UI can show
        // per-tuner fields) -- so the plugin does nothing until the user actually adds a tuner.
        serviceCollection.AddSingleton<HtspTunerHost>();
        serviceCollection.AddSingleton<ITunerHost>(sp => sp.GetRequiredService<HtspTunerHost>());
        serviceCollection.AddSingleton<IListingsProvider, HtspListingsProvider>();

        // NOTE: HtspLiveTvService (the integrated ILiveTvService) is intentionally NOT registered. It was a
        // second, config-only way to use the plugin that listed channels without a tuner and duplicated the
        // tuner path; the class is kept for its Tvheadend-native DVR logic, to be reattached to the tuner
        // later. With only the tuner host, DVR is handled by Jellyfin core recording the tuner stream.
    }
}
