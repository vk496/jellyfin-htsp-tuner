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

        // Fills in the blank programme tiles on the Live TV home page from the broadcast itself. A plain
        // hosted service rather than an IScheduledTask: it runs every 61s, and a scheduled task logs a line
        // per run, which would bury the log in an hour.
        serviceCollection.AddHostedService<ProgramImageService>();

        // NOTE: no ILiveTvService is registered. There was one (HtspLiveTvService): a second, config-only way
        // to use the plugin that listed channels without a tuner and duplicated the tuner path. It has since
        // been DELETED, not kept -- so its Tvheadend-native DVR logic is gone with it, and DVR today means
        // Jellyfin core recording our tuner stream itself rather than asking Tvheadend to record.
    }
}
