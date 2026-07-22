using System.Collections.Concurrent;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;
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
    // Jellyfin never deletes a programme's artwork folder. CleanDatabase drops the item with
    // DeleteFileLocation=false, and LibraryManager only clears an item's metadata folder when the item is
    // file-protocol -- a programme has no path, so it is not. Every programme that ages out of the guide
    // therefore leaves its picture behind for good, and capturing frames adds one per programme per airing.
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(1);

    // A folder is only an orphan if its item is gone, and an item is written a moment after its folder.
    // This grace makes that race impossible rather than unlikely.
    private static readonly TimeSpan OrphanGrace = TimeSpan.FromHours(6);

    // Bounds one pass: the first run on a server that has been collecting these for months has thousands to
    // get through, and there is no hurry.
    private const int MaxDeletesPerPass = 2000;

    private readonly HtspTunerHost _host;
    private readonly IServerApplicationPaths _appPaths;
    private readonly ILibraryManager _library;
    private readonly IProviderManager _providerManager;
    private readonly ILogger<ProgramImageService> _logger;
    private readonly ConcurrentDictionary<string, DateTime> _cooldown = new();

    // Starts "now" so a server restart does not trigger a sweep of the whole metadata tree on the first tick.
    private DateTime _lastCleanupUtc = DateTime.UtcNow;

    /// <summary>Initializes a new instance of the <see cref="ProgramImageService"/> class.</summary>
    /// <param name="host">The tuner host, which owns the channels and does the capturing.</param>
    /// <param name="appPaths">Application paths, for finding the artwork Jellyfin leaves behind.</param>
    /// <param name="library">The library, used to find what the home page is showing.</param>
    /// <param name="providerManager">Used to store the captured image against the programme.</param>
    /// <param name="logger">The logger.</param>
    public ProgramImageService(
        HtspTunerHost host,
        IServerApplicationPaths appPaths,
        ILibraryManager library,
        IProviderManager providerManager,
        ILogger<ProgramImageService> logger)
    {
        _host = host;
        _appPaths = appPaths;
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
                CleanOrphanedImages(stoppingToken);
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
            .OfType<LiveTvChannel>()
            .Where(c => c.ExternalId?.StartsWith(HtspTunerHost.ChannelIdPrefix, StringComparison.Ordinal) == true)
            // A radio service has no video, so there is no frame to grab and never will be. Jellyfin already
            // knows which these are, so drop them here instead of spending a slot per sweep finding out.
            .Where(c => c.ChannelType != ChannelType.Radio)
            // The logo is whatever Jellyfin already stored for the channel -- the same picture it draws on
            // the channel tile. Taking it from here rather than over HTSP also covers the channels whose
            // Tvheadend icon is an external URL, which HTSP cannot hand us but Jellyfin has downloaded.
            .ToDictionary(c => c.Id, c => (Id: c.ExternalId!, Logo: c.GetImageInfo(ImageType.Primary, 0)?.Path));

        var now = DateTime.UtcNow;
        var candidates = missing
            .Where(p => channels.ContainsKey(p.ChannelId))
            .Select(p => (Program: p, Channel: channels[p.ChannelId]))
            .Where(x => _cooldown.GetValueOrDefault(x.Channel.Id) <= now)
            .DistinctBy(x => x.Channel.Id, StringComparer.Ordinal)
            .ToList();

        // A scan works the whole backlog, not a fixed slice of it: the point is to fill the page in minutes,
        // and a per-scan cap meant the page filled at the cap's rate no matter how cheap each capture got.
        // Ticks that fire while this is still running simply find nothing left to do -- WaitForNextTickAsync
        // coalesces them, so scans never overlap and never queue up.
        //
        // Shuffled first, because the query order is stable and always working from the front would give the
        // same channels every attempt if a scan is cut short. Then grouped by mux, which decides how they are
        // captured rather than which: Tvheadend serves a second channel off a mux it is already tuned to
        // without touching the tuner.
        var work = GroupByMux(Shuffle(candidates), x => _host.MuxOf(x.Channel.Id)).ToList();

        var started = DateTime.UtcNow;
        var captured = 0;
        var reasons = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (program, channel) in work)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var error = await TryCaptureAsync(program, channel.Id, channel.Logo, cancellationToken)
                .ConfigureAwait(false);
            if (error is null)
            {
                captured++;
            }
            else
            {
                // Parking a channel that just failed stops a sweep spending its whole budget on the same
                // dead channels, but it also means a channel that was only briefly unavailable -- a guide
                // refresh holding the tuners, a transient loss of signal -- stays blank for the whole park.
                // Off by default for that reason: a retry costs one short tune.
                var park = Plugin.Instance?.Configuration.ProgramImageParkMinutes ?? 0;
                if (park > 0)
                {
                    _cooldown[channel.Id] = DateTime.UtcNow + TimeSpan.FromMinutes(park);
                }
                reasons[error] = reasons.GetValueOrDefault(error) + 1;
            }
        }

        // One line at Information per sweep, with why the failures failed. This is background work on the
        // tuners and the only other evidence it ran is thumbnails quietly appearing -- and when most of a
        // lineup fails it is usually for one shared reason (a login without access to those channels, a dish
        // pointed elsewhere), which "nothing appeared" does not tell anyone.
        var why = reasons.Count == 0
            ? string.Empty
            : "; failures: " + string.Join(", ", reasons.OrderByDescending(r => r.Value).Select(r => $"{r.Value}x {r.Key}"));
        _logger.LogInformation(
            "Captured {Captured} of {Total} missing programme images in {Seconds}s{Why}",
            captured, work.Count, (int)(DateTime.UtcNow - started).TotalSeconds, why);
    }

    // A captured frame is worthless the moment its programme is over, and Jellyfin will not tidy up after
    // itself here (see CleanupInterval). So delete the artwork folders whose programme no longer exists.
    // Deliberately not limited to our own captures: an orphaned folder is rubbish whoever wrote it, and
    // there is nothing in the file to tell a captured frame from a downloaded EPG image anyway.
    private void CleanOrphanedImages(CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.Configuration.CleanOrphanedProgramImages != true
            || DateTime.UtcNow - _lastCleanupUtc < CleanupInterval)
        {
            return;
        }

        _lastCleanupUtc = DateTime.UtcNow;
        var root = Path.Combine(_appPaths.InternalMetadataPath, "livetv");
        if (!Directory.Exists(root))
        {
            return;
        }

        var cutoff = DateTime.UtcNow - OrphanGrace;
        var deleted = 0;
        long freed = 0;

        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            if (cancellationToken.IsCancellationRequested || deleted >= MaxDeletesPerPass)
            {
                break;
            }

            // Only ever a folder named exactly as an item id, directly under livetv. Anything else is not
            // ours to reason about, so it is left alone.
            var name = Path.GetFileName(dir);
            if (name.Length != 32 || !Guid.TryParseExact(name, "N", out var id))
            {
                continue;
            }

            try
            {
                if (Directory.GetLastWriteTimeUtc(dir) > cutoff || _library.GetItemById(id) is not null)
                {
                    continue;
                }

                var size = new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                Directory.Delete(dir, true);
                deleted++;
                freed += size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Someone else's file, or in use. Next pass will find it again.
            }
        }

        if (deleted > 0)
        {
            _logger.LogInformation(
                "Deleted {Count} Live TV artwork folders whose programme no longer exists, freeing {Mb} MB",
                deleted, freed / (1024 * 1024));
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

    // Returns null on success, or why no image was stored.
    private async Task<string?> TryCaptureAsync(
        BaseItem program, string channelId, string? logoPath, CancellationToken cancellationToken)
    {
        // A movie is shown on a portrait card, which crops the sides away; the logo has to sit in the middle
        // to survive that.
        var isMovie = program is LiveTvProgram { IsMovie: true };
        var result = await _host.CaptureFrameAsync(channelId, logoPath, isMovie, cancellationToken)
            .ConfigureAwait(false);
        if (result.Path is not { } image)
        {
            return result.Error ?? "unknown";
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
            return null;
        }
        finally
        {
            HtspTunerHost.TryDelete(image);
        }
    }
}
