using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;

namespace HtspTuner.LiveTv;

/// <summary>
/// Serves Tvheadend's EPG as a Jellyfin guide source, so "HTSP" appears under "TV Guide Data Providers"
/// next to Schedules Direct and XMLTV and can be mapped to an HTSP tuner's channels.
/// </summary>
/// <remarks>
/// It delegates to <see cref="HtspTunerHost"/> so the guide and the tuner share the same connections and
/// channel ids — a guide channel id equals its tuner channel id, giving an identity mapping with no
/// manual channel matching.
/// </remarks>
public sealed class HtspListingsProvider : IListingsProvider
{
    private readonly HtspTunerHost _tunerHost;

    /// <summary>Initializes a new instance of the <see cref="HtspListingsProvider"/> class.</summary>
    /// <param name="tunerHost">The HTSP tuner host to source channels and EPG from.</param>
    public HtspListingsProvider(HtspTunerHost tunerHost)
    {
        _tunerHost = tunerHost;
    }

    /// <inheritdoc/>
    public string Name => "HTSP";

    /// <inheritdoc/>
    public string Type => "htsp";

    /// <inheritdoc/>
    public Task<IEnumerable<ProgramInfo>> GetProgramsAsync(
        ListingsProviderInfo info, string channelId, DateTime startDateUtc, DateTime endDateUtc, CancellationToken cancellationToken)
        => _tunerHost.GetProgramsAsync(channelId, startDateUtc, endDateUtc, cancellationToken);

    /// <inheritdoc/>
    public Task<List<ChannelInfo>> GetChannels(ListingsProviderInfo info, CancellationToken cancellationToken)
        => _tunerHost.GetChannels(true, cancellationToken);

    /// <inheritdoc/>
    public Task Validate(ListingsProviderInfo info, bool validateLogin, bool validateListings)
        => Task.CompletedTask; // the tuner host already validates the connection

    /// <inheritdoc/>
    public Task<List<NameIdPair>> GetLineups(ListingsProviderInfo info, string country, string location)
        => Task.FromResult(new List<NameIdPair> { new() { Name = "Tvheadend HTSP", Id = "htsp" } });
}
