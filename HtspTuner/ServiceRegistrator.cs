using HtspTuner.LiveTv;
using MediaBrowser.Controller;

using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace HtspTuner;

/// <summary>
/// Registers the plugin's services. Jellyfin has no auto-discovery for <see cref="ILiveTvService"/>;
/// a plugin must register it here, which Jellyfin invokes before building the container.
/// </summary>
public class ServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc/>
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<HtspLiveTvService>();
        serviceCollection.AddSingleton<ILiveTvService>(sp => sp.GetRequiredService<HtspLiveTvService>());

        // Also expose HTSP as a native tuner (shows under "Add Tuner Device", multiple instances) plus a
        // matching guide provider (shows under "TV Guide Data Providers"). They share one instance so the
        // guide and tuner reuse connections and channel ids. Users pick one model: the integrated service
        // (plugin config) or tuner devices + the HTSP guide.
        serviceCollection.AddSingleton<HtspTunerHost>();
        serviceCollection.AddSingleton<ITunerHost>(sp => sp.GetRequiredService<HtspTunerHost>());
        serviceCollection.AddSingleton<IListingsProvider, HtspListingsProvider>();

        // Recordings playback (IChannel/RecordingsChannel) lands after live TV is verified end to end.
    }
}
