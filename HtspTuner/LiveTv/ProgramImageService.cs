using System.Collections.Concurrent;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HtspTuner.LiveTv;

/// <summary>
/// Gives the programmes on the Live TV home page a picture by grabbing a frame off the channel itself.
/// </summary>
/// <remarks>
/// Broadcasters supply artwork for a small minority of programmes, so most of that page is the same blank
/// placeholder tile. Tvheadend cannot fill the gap either — its own EPG has no image for these — and asking
/// Jellyfin to refresh the guide more often would not help, because there is nothing new to fetch and a
/// refresh costs minutes. A still from the live broadcast is the only picture that exists, so take one.
/// </remarks>
internal sealed class ProgramImageService : BackgroundService
{
    // Long enough that a channel which cannot produce a frame (scrambled, no signal, an odd codec) is not
    // retried on every scan, short enough that a temporary failure heals within an hour.
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromMinutes(30);

    // ponytail: fixed budget, no config knob. Each capture is a tune plus a few seconds of TS, so this is
    // the difference between a background trickle and hammering the tuners. Promote if anyone needs it.
    private const int MaxCapturesPerScan = 4;

    private readonly HtspTunerHost _host;
    private readonly ILibraryManager _library;
    private readonly IProviderManager _providerManager;
    private readonly ILogger<ProgramImageService> _logger;
    private readonly ConcurrentDictionary<string, DateTime> _cooldown = new();

    /// <summary>Initializes a new instance of the <see cref="ProgramImageService"/> class.</summary>
    /// <param name="host">The tuner host, which owns the channels and does the capturing.</param>
    /// <param name="library">The library, used to find what the home page is showing.</param>
    /// <param name="providerManager">Used to store the captured image against the programme.</param>
    /// <param name="logger">The logger.</param>
    public ProgramImageService(
        HtspTunerHost host,
        ILibraryManager library,
        IProviderManager providerManager,
        ILogger<ProgramImageService> logger)
    {
        _host = host;
        _library = library;
        _providerManager = providerManager;
        _logger = logger;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(ScanInterval());
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            // Re-read every tick so changing the setting takes effect without restarting the server.
            timer.Period = ScanInterval();

            try
            {
                await ScanAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // A background nicety must never be the thing that takes the plugin down.
                _logger.LogWarning(ex, "Programme thumbnail scan failed");
            }
        }
    }

    private static TimeSpan ScanInterval()
        => TimeSpan.FromSeconds(Math.Clamp(Plugin.Instance?.Configuration.ProgramImageScanSeconds ?? 61, 15, 3600));

    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.Configuration.CaptureProgramImages != true || !_host.HasTuners)
        {
            return;
        }

        // Exactly the pool the Live TV home page draws its "On Now" row from: LiveTvManager asks for the
        // airing programmes with the earliest start date, capped, and only then reorders that shortlist by
        // its recommendation score and shows the top of it. Matching the query means we cover what is on
        // screen without guessing at the score — and the cap is what keeps this sane on a Tvheadend
        // carrying thousands of channels, where "every airing programme" would be thousands of rows a minute.
        var limit = Plugin.Instance.Configuration.ProgramImageCandidates;
        var missing = _library
            .GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.LiveTvProgram],
                IsAiring = true,
                OrderBy = [(ItemSortBy.StartDate, SortOrder.Ascending)],
                Limit = limit > 0 ? limit : null,
            })
            .Where(p => !p.HasImage(ImageType.Primary, 0))
            .ToList();
        if (missing.Count == 0)
        {
            return;
        }

        // Only the channels those programmes are actually on, so this never walks the whole channel list.
        // The library may also hold an M3U or HDHomeRun tuner's channels, which we cannot tune.
        var channels = _library
            .GetItemList(new InternalItemsQuery { ItemIds = missing.Select(p => p.ChannelId).Distinct().ToArray() })
            .Where(c => c.ExternalId?.StartsWith(HtspTunerHost.ChannelIdPrefix, StringComparison.Ordinal) == true)
            .ToDictionary(c => c.Id, c => c.ExternalId!);

        var now = DateTime.UtcNow;
        var candidates = missing
            .Where(p => channels.ContainsKey(p.ChannelId))
            .Select(p => (Program: p, Channel: channels[p.ChannelId]))
            .Where(x => _cooldown.GetValueOrDefault(x.Channel) <= now)
            .DistinctBy(x => x.Channel, StringComparer.Ordinal);

        // Shuffle before taking the scan's few: the candidate list comes back in a stable order, so always
        // working from the front means the same handful of channels get every attempt and the tail is only
        // ever reached once those succeed. Random picks spread the coverage instead. The chosen few are then
        // ordered by mux, which is about how they are captured rather than which ones are captured.
        var work = GroupByMux(
                Shuffle(candidates.ToList()).Take(MaxCapturesPerScan),
                x => _host.MuxOf(x.Channel))
            .ToList();

        foreach (var (program, channelId) in work)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await TryCaptureAsync(program, channelId, cancellationToken).ConfigureAwait(false))
            {
                _cooldown[channelId] = now + FailureCooldown;
            }
        }
    }

    // Fisher-Yates over a copy. Random.Shared is fine here: this only decides which thumbnails get taken
    // first, so it wants to be cheap and thread-safe, not unpredictable.
    private static List<T> Shuffle<T>(List<T> items)
    {
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }

        return items;
    }

    /// <summary>Orders items so that ones sharing a mux end up next to each other.</summary>
    /// <remarks>
    /// A scan captures a handful of channels back to back. Tvheadend can serve a second channel off a mux it
    /// is already tuned to without touching the tuner, so keeping same-mux channels adjacent turns most of a
    /// scan into near-free work. Channels we have never tuned have no known mux and go last, since they are
    /// the ones that will definitely cost a tune.
    /// </remarks>
    /// <typeparam name="T">The item type.</typeparam>
    /// <param name="items">The items to order.</param>
    /// <param name="muxOf">Returns an item's mux, or null if it is not known.</param>
    /// <returns>The ordered items.</returns>
    internal static IEnumerable<T> GroupByMux<T>(IEnumerable<T> items, Func<T, string?> muxOf)
        => items
            .OrderBy(x => muxOf(x) is null ? 1 : 0)
            .ThenBy(muxOf, StringComparer.Ordinal);

    private async Task<bool> TryCaptureAsync(BaseItem program, string channelId, CancellationToken cancellationToken)
    {
        var image = await _host.CaptureFrameAsync(channelId, cancellationToken).ConfigureAwait(false);
        if (image is null)
        {
            return false;
        }

        try
        {
            var file = File.OpenRead(image);
            await using (file.ConfigureAwait(false))
            {
                // ExtractVideoImage always writes a JPEG.
                await _providerManager.SaveImage(program, file, "image/jpeg", ImageType.Primary, null, cancellationToken)
                    .ConfigureAwait(false);
            }

            // SaveImage only puts the file on disk and points the item at it; without this the path is lost
            // on the next read and the programme looks blank again.
            await program.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);

            // A guide refresh drops any image whose programme has no ImageUrl in the listings — which is
            // every programme we capture for — so this can be undone by a refresh. That is what makes the
            // scan a loop rather than a one-off: it simply takes the picture again on the next pass.
            _logger.LogDebug("Captured a thumbnail for \"{Program}\" from {Channel}", program.Name, channelId);
            return true;
        }
        finally
        {
            HtspTunerHost.TryDelete(image);
        }
    }
}
