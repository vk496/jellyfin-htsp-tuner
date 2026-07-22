using System.Collections.Concurrent;
using System.Globalization;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Dto;
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
public sealed class ProgramImageService : BackgroundService
{
    // Jellyfin never deletes a programme's artwork folder. CleanDatabase drops the item with
    // DeleteFileLocation=false, and LibraryManager only clears an item's metadata folder when the item is
    // file-protocol -- a programme has no path, so it is not. Every programme that ages out of the guide
    // therefore leaves its picture behind for good, and capturing frames adds one per programme per airing.
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(1);

    // A folder is only an orphan if its item is gone, and an item is written a moment after its folder.
    // This grace makes that race impossible rather than unlikely.
    private static readonly TimeSpan OrphanGrace = TimeSpan.FromHours(6);

    private static readonly TimeSpan ChannelIdCacheTtl = TimeSpan.FromMinutes(10);

    // How recently somebody must have used the server for their home page to steer the sweep.
    private static readonly TimeSpan ActiveUserWindow = TimeSpan.FromDays(30);

    // Ahead of everything on the page: a channel already being watched is the cheapest capture there is.
    private const long WatchedRank = -1;

    // Not on the page at all, so it has no claim on being done before anything that is.
    private const long Unranked = long.MaxValue;

    // Bounds one pass: the first run on a server that has been collecting these for months has thousands to
    // get through, and there is no hurry.
    private const int MaxDeletesPerPass = 2000;

    private readonly HtspTunerHost _host;
    private readonly IServerApplicationPaths _appPaths;
    private readonly ILibraryManager _library;
    private readonly ILiveTvManager _liveTv;
    private readonly IUserManager _userManager;
    private readonly IProviderManager _providerManager;
    private readonly ILogger<ProgramImageService> _logger;
    private readonly ConcurrentDictionary<string, DateTime> _cooldown = new();

    // One sweep at a time, whether it was the timer or somebody pressing the button.
    private readonly SemaphoreSlim _sweeping = new(1, 1);

    // Cancels whichever sweep is running. A sweep holds tuners for minutes at a time, so somebody who
    // wants them back should not have to wait for it or restart the server to get them.
    private CancellationTokenSource? _running;

    // Set so the first sweep happens a few minutes in, not on the first tick and not a full hour later:
    // walking the whole metadata tree is not something to do while the server is still starting up, but
    // anchoring it to boot would mean a server restarted more often than the interval never cleans at all.
    private DateTime _lastCleanupUtc = DateTime.UtcNow - CleanupInterval + TimeSpan.FromMinutes(5);

    // When each programme's picture was last taken, so a watched channel is re-shot on a schedule rather
    // than every sweep. In memory only: after a restart every programme is simply due once more, which
    // costs one free capture each.
    private readonly ConcurrentDictionary<Guid, DateTime> _refreshedAt = new();

    private Dictionary<string, Guid>? _channelIds;
    private DateTime _channelIdsAtUtc;

    /// <summary>Initializes a new instance of the <see cref="ProgramImageService"/> class.</summary>
    /// <param name="host">The tuner host, which owns the channels and does the capturing.</param>
    /// <param name="appPaths">Application paths, for finding the artwork Jellyfin leaves behind.</param>
    /// <param name="library">The library, used to resolve the channels those programmes are on.</param>
    /// <param name="liveTv">Live TV, asked directly what the home page is showing.</param>
    /// <param name="userManager">The users, because the home page is built per user.</param>
    /// <param name="providerManager">Used to store the captured image against the programme.</param>
    /// <param name="logger">The logger.</param>
    public ProgramImageService(
        HtspTunerHost host,
        IServerApplicationPaths appPaths,
        ILibraryManager library,
        ILiveTvManager liveTv,
        IUserManager userManager,
        IProviderManager providerManager,
        ILogger<ProgramImageService> logger)
    {
        _host = host;
        _appPaths = appPaths;
        _library = library;
        _liveTv = liveTv;
        _userManager = userManager;
        _providerManager = providerManager;
        _logger = logger;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _host.ChannelOpened += OnChannelOpened;
        try
        {
            await SweepLoopAsync(stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            _host.ChannelOpened -= OnChannelOpened;
        }
    }

    // A recording takes its artwork from the programme's own picture, once, as it starts. Waiting for the
    // next sweep means the recording keeps the blank it started with for good, so grab the frame now --
    // the channel is open, so there is no tuner to wait for and nothing to take from anybody.
    private void OnChannelOpened(string channelId)
    {
        if (Plugin.Instance?.Configuration.CaptureProgramImages != true)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await CaptureOpenChannelAsync(channelId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not capture on opening channel {Channel}", channelId);
            }
        });
    }

    private async Task CaptureOpenChannelAsync(string channelId, CancellationToken cancellationToken)
    {
        var channel = _library
            .GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.LiveTvChannel] })
            .OfType<LiveTvChannel>()
            .FirstOrDefault(c => string.Equals(c.ExternalId, channelId, StringComparison.Ordinal));
        if (channel is null || channel.ChannelType == ChannelType.Radio)
        {
            return;
        }

        var airing = _library
            .GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.LiveTvProgram],
                IsAiring = true,
                ChannelIds = [channel.Id],
            })
            .FirstOrDefault(p => !p.HasImage(ImageType.Primary, 0));
        if (airing is null)
        {
            return; // nothing on, or it already has a picture -- a recording will use that one
        }

        _logger.LogInformation(
            "Channel {Channel} opened and \"{Program}\" has no picture; capturing one now",
            channelId, airing.Name);

        var logo = channel.GetImageInfo(ImageType.Primary, 0)?.Path;
        await TryCaptureAsync(airing, channelId, logo, cancellationToken).ConfigureAwait(false);
    }

    private async Task SweepLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(ScanInterval());
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            // Re-read every tick so changing the setting takes effect without restarting the server.
            timer.Period = ScanInterval();

            try
            {
                if (!AutomaticSweepsEnabled)
                {
                    continue;
                }

                await _sweeping.WaitAsync(stoppingToken).ConfigureAwait(false);
                try
                {
                    using var sweep = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    _running = sweep;
                    await ScanAsync(sweep.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogInformation("Programme thumbnail sweep cancelled");
                }
                finally
                {
                    _running = null;
                    _sweeping.Release();
                }

                await CleanOrphanedImagesAsync(stoppingToken).ConfigureAwait(false);
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

    /// <summary>Starts a sweep now, unless one is already running.</summary>
    /// <remarks>
    /// For the button on the settings page. Captures are paced so as not to disturb anybody watching, which
    /// makes them slow by design -- so there has to be a way to say "do it now, I am not using the TV".
    /// It returns as soon as the sweep is under way rather than waiting for it: a full sweep runs for
    /// minutes, which no browser will hold a request open for. Progress goes to the log, as it does for a
    /// scheduled sweep.
    /// </remarks>
    /// <returns>True if a sweep was started, false if one was already in progress.</returns>
    public bool TryStartSweep()
    {
        if (!_sweeping.Wait(0))
        {
            return false;
        }

        // Detached from the caller's request, and from its cancellation token with it: the sweep should
        // outlive the HTTP call that asked for it.
        _ = Task.Run(async () =>
        {
            using var sweep = new CancellationTokenSource();
            _running = sweep;
            try
            {
                await ScanAsync(sweep.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Programme thumbnail sweep cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Requested programme thumbnail sweep failed");
            }
            finally
            {
                _running = null;
                _sweeping.Release();
            }
        });

        return true;
    }

    /// <summary>Stops the sweep that is running, if there is one.</summary>
    /// <remarks>
    /// A sweep can hold tuners for minutes. Being able to start one on demand without being able to stop it
    /// again leaves no way out short of restarting the server.
    /// </remarks>
    /// <returns>True if a sweep was running and has been asked to stop.</returns>
    public bool TryCancelSweep()
    {
        var running = _running;
        if (running is null)
        {
            return false;
        }

        try
        {
            running.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false; // it finished between the check and the cancel
        }
    }

    // Zero switches automatic sweeps off; the button on the settings page still works. The wait itself is
    // short in that case, because it is only there to notice the setting being turned back on.
    // Whose home page counts. A server accumulates accounts that stopped being used -- and a row belonging
    // to somebody who last logged in a year ago should not decide what the tuners spend their time on ahead
    // of the person watching today. Everybody counts if nobody has been active, since an idle server has no
    // better answer.
    private IReadOnlyList<User> ActiveUsers()
    {
        var all = _userManager.GetUsers().OrderByDescending(u => u.LastActivityDate ?? DateTime.MinValue).ToList();
        var since = DateTime.UtcNow - ActiveUserWindow;
        var active = all.Where(u => u.LastActivityDate >= since).ToList();
        return active.Count > 0 ? active : all;
    }

    private static TimeSpan ScanInterval()
    {
        var seconds = Plugin.Instance?.Configuration.ProgramImageScanSeconds ?? 181;
        return seconds <= 0 ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(Math.Clamp(seconds, 15, 3600));
    }

    private static bool AutomaticSweepsEnabled => (Plugin.Instance?.Configuration.ProgramImageScanSeconds ?? 181) > 0;

    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.Configuration.CaptureProgramImages != true || !_host.HasTuners)
        {
            return;
        }

        // Ask Jellyfin what the Live TV home page is actually showing, rather than reproducing the query
        // behind it. The row is not simply "the airing programmes": LiveTvManager fetches a shortlist and
        // reorders it by a recommendation score built from each user's favourites, likes and play counts,
        // so the same server shows different programmes to different people. Guessing at that put channels
        // in the candidate set that were nowhere near the page -- radio services among them.
        //
        // The union across users is the honest reading of "what is on the main page", since each user has
        // their own. It is also what keeps this proportional: the page shows tens of programmes, not the
        // thousands a large Tvheadend carries.
        var limit = Plugin.Instance.Configuration.ProgramImageCandidates;
        var missing = new List<BaseItem>();
        var seen = new HashSet<Guid>();

        // Where each programme sits on the page. Jellyfin returns the row already ordered, best first, so
        // position in that list is exactly how prominent a tile is -- and the whole point of capturing is
        // the tiles somebody is looking at.
        var rank = new Dictionary<Guid, long>();
        var users = ActiveUsers();
        for (var whose = 0; whose < users.Count; whose++)
        {
            var user = users[whose];
            cancellationToken.ThrowIfCancellationRequested();
            var page = _liveTv.GetRecommendedProgramsInternal(
                new InternalItemsQuery(user)
                {
                    IsAiring = true,
                    Limit = limit > 0 ? limit : null,
                    EnableTotalRecordCount = false,
                },
                new DtoOptions(false),
                cancellationToken);

            for (var position = 0; position < page.Items.Count; position++)
            {
                var item = page.Items[position];
                if (item.HasImage(ImageType.Primary, 0))
                {
                    continue;
                }

                if (seen.Add(item.Id))
                {
                    missing.Add(item);
                }

                // Position within this person's row, and the best one across everybody: a tile at the top
                // of one row deserves that treatment even if it is halfway down another. Ranking by
                // position in the accumulated list instead put every one of the first user's tiles ahead
                // of every one of the second's, however prominent the second's actually were.
                //
                // Account first, position second: one person's row is worked through before the next
                // person's begins. Ranking by position across everybody instead interleaved them, so the
                // top of a row belonging to somebody who has never watched anything -- and whose row is
                // therefore in no meaningful order at all -- outranked the second entry of a row driven by
                // real viewing history.
                //
                // Still the best across accounts, so a channel two people both have keeps the better claim
                // rather than the last one seen.
                var score = ((long)whose << 32) | (uint)position;
                rank[item.Id] = Math.Min(rank.GetValueOrDefault(item.Id, Unranked), score);
            }
        }

        // Whatever is on a channel somebody is watching, page or no page. Reading that costs Tvheadend
        // nothing -- the bytes are already buffered, no tuner is touched -- so there is no reason to let it
        // stay blank just because the home page happened not to rank it.
        AddWatchedChannelPrograms(missing, seen, rank, cancellationToken);

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

        // Weight keeps a capture from taking a tuner off a viewer, but it cannot make tuning free. On a
        // satellite install the tuners share an LNB, so putting one on another transponder means DiSEqC
        // traffic and a polarisation or band switch on the shared cable -- and the two ffmpeg runs per
        // capture compete with whatever is transcoding the stream somebody is watching. None of that shows
        // up as a lost subscription; it shows up as a viewer's picture stuttering. So while anyone is
        // watching, capture only from the streams already open, which touch no tuner and decode nothing new.
        var watching = _host.WatchedChannelIds();
        if (watching.Count > 0 && Plugin.Instance?.Configuration.PauseCapturesWhileWatching != false)
        {
            var free = watching.ToHashSet(StringComparer.Ordinal);
            var before = candidates.Count;
            candidates = candidates.Where(x => free.Contains(x.Channel.Id)).ToList();
            if (before != candidates.Count)
            {
                // Information, not debug: this is the plugin declining to do the thing it was asked to do,
                // and whether it engaged is exactly the question when somebody reports playback trouble.
                _logger.LogInformation(
                    "Watching {Channels}, so {Skipped} of {Total} captures needing a tuner are deferred",
                    string.Join(", ", watching), before - candidates.Count, before);
            }
        }

        // A scan works the whole backlog, not a fixed slice of it: the point is to fill the page in minutes,
        // and a per-scan cap meant the page filled at the cap's rate no matter how cheap each capture got.
        // Sweeps cannot overlap -- the interval is counted from the end of one to the start of the next.
        var work = CaptureOrder(
                candidates,
                x => _host.MuxOf(x.Channel.Id),
                x => rank.GetValueOrDefault(x.Program.Id, Unranked))
            .ToList();

        var started = DateTime.UtcNow;
        var captured = 0;
        var reasons = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (program, channel) in work)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (error, fatal) = await TryCaptureAsync(program, channel.Id, channel.Logo, cancellationToken)
                .ConfigureAwait(false);
            if (error is null)
            {
                captured++;
                continue;
            }

            reasons[error] = reasons.GetValueOrDefault(error) + 1;
            if (fatal)
            {
                // Tvheadend is unreachable, so the rest of the list would only produce this same answer
                // dozens more times -- and parking channels for a fault that is not theirs would keep them
                // blank well after it recovers.
                break;
            }

            // Parking a channel that just failed stops a sweep spending its whole budget on the same
            // dead channels, but it also means a channel that was only briefly unavailable -- a guide
            // refresh holding the tuners, a transient loss of signal -- stays blank for the whole park.
            // Off by default for that reason: a retry costs one short tune.
            var park = Plugin.Instance?.Configuration.ProgramImageParkMinutes ?? 0;
            if (park > 0)
            {
                _cooldown[channel.Id] = DateTime.UtcNow + TimeSpan.FromMinutes(park);
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

    private void AddWatchedChannelPrograms(
        List<BaseItem> missing, HashSet<Guid> seen, Dictionary<Guid, long> rank, CancellationToken cancellationToken)
    {
        var watched = _host.WatchedChannelIds();
        if (watched.Count == 0)
        {
            return;
        }

        // Going from our channel id back to Jellyfin's needs the channel list, which is the one query that
        // scales with the size of the lineup rather than the size of the page. It only runs while somebody
        // is actually watching, and the answer changes about as often as the guide does, so it is cached.
        if (_channelIds is null || DateTime.UtcNow - _channelIdsAtUtc > ChannelIdCacheTtl)
        {
            _channelIds = _library
                .GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.LiveTvChannel] })
                .Where(c => c.ExternalId?.StartsWith(HtspTunerHost.ChannelIdPrefix, StringComparison.Ordinal) == true)
                .GroupBy(c => c.ExternalId!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal);
            _channelIdsAtUtc = DateTime.UtcNow;
        }

        var guids = watched.Select(id => _channelIds.GetValueOrDefault(id)).Where(g => g != Guid.Empty).ToArray();
        if (guids.Length == 0)
        {
            return;
        }

        // A watched channel is also worth re-shooting now and then. The picture is a still from a live
        // broadcast, so half an hour into a programme the opening titles are a poor summary of it -- and
        // taking another costs nothing while the stream is already in memory.
        var refresh = Plugin.Instance?.Configuration.WatchedRefreshMinutes ?? 0;
        var now = DateTime.UtcNow;

        cancellationToken.ThrowIfCancellationRequested();
        foreach (var item in _library.GetItemList(new InternalItemsQuery
                 {
                     IncludeItemTypes = [BaseItemKind.LiveTvProgram],
                     IsAiring = true,
                     ChannelIds = guids,
                 }))
        {
            // Re-shoot only pictures we took ourselves. A programme that already has artwork got it from the
            // broadcaster's EPG, which is a better picture than any frame we could grab and is not ours to
            // replace. _refreshedAt is what says "this one is ours" -- and because it does not survive a
            // restart, an unknown programme is left alone rather than assumed to be a previous capture.
            var due = !item.HasImage(ImageType.Primary, 0)
                      || (refresh > 0
                          && _refreshedAt.TryGetValue(item.Id, out var taken)
                          && now - taken >= TimeSpan.FromMinutes(refresh));
            if (due && seen.Add(item.Id))
            {
                // Ahead of the page: this channel is already tuned, so it is both the cheapest capture
                // available and the one somebody is demonstrably looking at.
                rank[item.Id] = WatchedRank;
                missing.Add(item);
            }
        }
    }

    // A captured frame is worthless the moment its programme is over, and Jellyfin will not tidy up after
    // itself here (see CleanupInterval). So delete the artwork folders whose programme no longer exists.
    // Deliberately not limited to our own captures: an orphaned folder is rubbish whoever wrote it, and
    // there is nothing in the file to tell a captured frame from a downloaded EPG image anyway.
    private async Task CleanOrphanedImagesAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.Configuration.CleanOrphanedProgramImages != true
            || DateTime.UtcNow - _lastCleanupUtc < CleanupInterval)
        {
            return;
        }

        // Thousands of directory deletions in a row is a lot of disk for a server that is also feeding a
        // live stream. It can wait for a quiet moment; nothing here is urgent.
        if (_host.WatchedChannelIds().Count > 0)
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

                // Paced rather than flat out, for the same reason.
                if (deleted % 100 == 0)
                {
                    await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Someone else's file, or in use. Next pass will find it again.
            }

            if (_host.WatchedChannelIds().Count > 0)
            {
                break; // somebody started watching; stop and pick this up next time
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

    /// <summary>Orders a sweep's work: what is on screen first, then whatever is left, at random.</summary>
    /// <remarks>
    /// Two things decide this. Prominence, because a tile nobody can see is not worth a tuner ahead of one
    /// they are looking at — one account's row at a time, most recently used first, rather than interleaving
    /// everybody's. And the multiplex, because Tvheadend serves a second channel off one it is already tuned
    /// to without touching the tuner, so once a mux has been picked the rest of its channels are nearly free
    /// and should follow immediately.
    /// Whatever is left over goes last in random order, so a sweep cut short does not keep retrying the same
    /// few channels while the tail is never reached.
    /// </remarks>
    /// <typeparam name="T">The item type.</typeparam>
    /// <param name="items">The items to order.</param>
    /// <param name="muxOf">Returns an item's mux, or null if it has never been tuned.</param>
    /// <param name="rankOf">Returns how prominent an item is; lower is more visible, <see cref="Unranked"/> is not on the page.</param>
    /// <returns>The ordered items.</returns>
    internal static IEnumerable<T> CaptureOrder<T>(
        IReadOnlyList<T> items, Func<T, string?> muxOf, Func<T, long> rankOf)
    {
        var ranked = new List<(T Item, long Rank, string Key)>();
        var rest = new List<T>();

        for (var i = 0; i < items.Count; i++)
        {
            var r = rankOf(items[i]);
            if (r >= Unranked)
            {
                rest.Add(items[i]);
                continue;
            }

            // A mux we have never seen cannot be grouped with anything, so it stands alone rather than
            // pooling with every other unknown -- those are not one multiplex, they are simply unknown.
            ranked.Add((items[i], r, muxOf(items[i]) ?? "\u0000" + i.ToString(CultureInfo.InvariantCulture)));
        }

        var byMux = ranked
            .GroupBy(t => t.Key, StringComparer.Ordinal)
            .OrderBy(g => g.Min(t => t.Rank))
            .SelectMany(g => g.OrderBy(t => t.Rank))
            .Select(t => t.Item);

        return byMux.Concat(Shuffle(rest));
    }

    // Returns (null, false) on success, otherwise why no image was stored and whether every other channel
    // would hit the same thing.
    private async Task<(string? Error, bool Fatal)> TryCaptureAsync(
        BaseItem program, string channelId, string? logoPath, CancellationToken cancellationToken)
    {
        var result = await _host.CaptureFrameAsync(channelId, logoPath, cancellationToken).ConfigureAwait(false);
        if (result.Path is not { } image)
        {
            return (result.Error ?? "unknown", result.Fatal);
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
            _refreshedAt[program.Id] = DateTime.UtcNow;
            _logger.LogDebug("Captured a thumbnail for \"{Program}\" from {Channel}", program.Name, channelId);
            return (null, false);
        }
        finally
        {
            HtspTunerHost.TryDelete(image);
        }
    }
}
