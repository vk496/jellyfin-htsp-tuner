using System.Collections.Concurrent;
using System.Globalization;
using HtspTuner.Configuration;
using HtspTuner.Htsp;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace HtspTuner.LiveTv;

/// <summary>
/// Exposes Tvheadend as a native Jellyfin tuner, so "HTSP Tuner" appears under "Add Tuner Device" and
/// several instances (one per Tvheadend server) can run side by side.
/// </summary>
/// <remarks>
/// The concrete <c>BaseTunerHost</c> helper is internal to the server assembly and not on NuGet, so this
/// implements <see cref="ITunerHost"/> directly and mirrors the base host's per-tuner enumeration.
/// Streaming reuses the same <see cref="HtspLiveStream"/> and muxer as the service path.
/// </remarks>
public sealed class HtspTunerHost : ITunerHost, IConfigurableTunerHost, IDisposable
{
    /// <summary>Prefix every channel id this host issues carries; it is how our channels are recognised.</summary>
    internal const string ChannelIdPrefix = "htsp_";

    // Jellyfin's own guide-refresh task. Matched by key because the type lives in the server's
    // Jellyfin.LiveTv assembly, which is not on NuGet, so the generic ITaskManager overloads that every
    // in-tree caller uses are out of reach here.
    private const string RefreshGuideTaskKey = "RefreshGuide";

    // Weight a background frame grab subscribes with: the lowest Tvheadend accepts, so it is always the
    // first thing dropped when a tuner is contended. A thumbnail is never worth interrupting anything.
    private const int CaptureWeight = 1;

    private readonly IConfigurationManager _config;
    private readonly IServerApplicationHost _appHost;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ITaskManager _taskManager;
    private readonly ILogger<HtspTunerHost> _logger;
    private readonly ConcurrentDictionary<string, HtspClient> _clients = new();
    private readonly ConcurrentDictionary<string, (byte[] Data, string ContentType)> _iconCache = new();

    // Streams we handed to Jellyfin, so background work can piggy-back on a channel someone is already
    // watching instead of tuning it again. Jellyfin passes its own list to GetChannelStream but there is no
    // way to ask for it, so we keep our own; entries are pruned by IsAlive rather than on close.
    private readonly ConcurrentDictionary<string, HtspLiveStream> _live = new();

    // Last mux each channel was seen on, learned from subscriptionStart. Only used to order background
    // captures so consecutive ones share a mux, which Tvheadend can serve without re-tuning.
    private readonly ConcurrentDictionary<string, string> _muxByChannel = new();
    private readonly SemaphoreSlim _clientLock = new(1, 1);
    private readonly Lock _refreshGate = new();

    // Starts "now", not MinValue: Tvheadend pushes EPG constantly, so a zero start would make the first
    // push after every single restart kick off a full multi-minute refresh.
    private DateTime _lastGuideRefreshUtc = DateTime.UtcNow;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="HtspTunerHost"/> class.</summary>
    /// <param name="config">The configuration manager, used to read configured tuners.</param>
    /// <param name="appHost">The application host.</param>
    /// <param name="mediaEncoder">ffprobe wrapper, passed to each live stream for metadata probing.</param>
    /// <param name="taskManager">Task manager, used to queue Jellyfin's guide refresh when Tvheadend pushes EPG changes.</param>
    /// <param name="logger">The logger.</param>
    public HtspTunerHost(
        IConfigurationManager config,
        IServerApplicationHost appHost,
        IMediaEncoder mediaEncoder,
        ITaskManager taskManager,
        ILogger<HtspTunerHost> logger)
    {
        _config = config;
        _appHost = appHost;
        _mediaEncoder = mediaEncoder;
        _taskManager = taskManager;
        _logger = logger;
    }

    /// <summary>
    /// Drops every cached HTSP connection. Jellyfin's "restart" rebuilds the app host inside the SAME
    /// process, so nothing but this disposes our clients: an undisposed <see cref="HtspClient"/> keeps its
    /// socket, its 45s keepalive and — worst — its reconnect supervisor running forever, orphaned from any
    /// app host that could ever use it. Every restart used to strand one more, each still re-syncing 322
    /// channels and 64k EPG events, which is why Tvheadend showed connections Jellyfin was not using.
    /// </summary>
    /// <remarks>
    /// Deliberately <see cref="IDisposable"/> and not <see cref="IAsyncDisposable"/>: Microsoft's DI throws
    /// if a singleton implements only the async form and the provider is disposed synchronously, whereas an
    /// async disposal falls back to this happily. Idempotent because the container tracks this instance
    /// twice (as itself and via the <see cref="ITunerHost"/> factory) and will dispose it twice.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var client in _clients.Values)
        {
            try
            {
                // Blocking, but this is shutdown: DisposeAsync cancels the supervisor and awaits it, and
                // every await inside is ConfigureAwait(false), so there is no context to deadlock on.
                client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "HTSP client did not shut down cleanly");
            }
        }

        _clients.Clear();
        _clientLock.Dispose();
    }

    /// <inheritdoc/>
    public string Name => "HTSP Tuner";

    /// <inheritdoc/>
    public string Type => "htsp";

    /// <inheritdoc/>
    public bool IsSupported => true;

    /// <inheritdoc/>
    public async Task<List<ChannelInfo>> GetChannels(bool enableCache, CancellationToken cancellationToken)
    {
        var all = new List<ChannelInfo>();
        foreach (var tuner in Tuners())
        {
            try
            {
                var client = await ClientForAsync(tuner, cancellationToken).ConfigureAwait(false);
                all.AddRange(ChannelsFor(tuner, client));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HTSP tuner {Tuner}: failed to list channels", tuner.Url);
            }
        }

        return all;
    }

    /// <inheritdoc/>
    public async Task<ILiveStream> GetChannelStream(
        string channelId, string streamId, IList<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
    {
        var shared = currentLiveStreams
            .OfType<HtspLiveStream>()
            .FirstOrDefault(s => s.EnableStreamSharing && s.IsAlive
                && string.Equals(s.OriginalStreamId, channelId, StringComparison.Ordinal));
        if (shared is not null)
        {
            shared.ConsumerCount++;
            return shared;
        }

        var (tuner, tvhChannelId) = Resolve(channelId);
        var client = await ClientForAsync(tuner, cancellationToken).ConfigureAwait(false);
        var subscription = await client.SubscribeAsync(tvhChannelId, cancellationToken).ConfigureAwait(false);
        var stream = new HtspLiveStream(
            client, subscription, _appHost, _mediaEncoder,
            (long)Plugin.Instance!.Configuration.MaxBufferMb * 1024 * 1024, _logger)
        {
            OriginalStreamId = channelId,
        };
        await stream.Open(cancellationToken).ConfigureAwait(false);
        Remember(channelId, stream);
        return stream;
    }

    /// <inheritdoc/>
    public Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(string channelId, CancellationToken cancellationToken)
    {
        var source = new MediaSourceInfo
        {
            Id = channelId,
            Protocol = MediaBrowser.Model.MediaInfo.MediaProtocol.File,
            Container = "ts",
            IsInfiniteStream = true,
            RequiresOpening = true,
            RequiresClosing = true,
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            SupportsProbing = false,
        };
        return Task.FromResult(new List<MediaSourceInfo> { source });
    }

    /// <inheritdoc/>
    public Task<List<TunerHostInfo>> DiscoverDevices(int discoveryDurationMs, CancellationToken cancellationToken)
        => Task.FromResult(new List<TunerHostInfo>()); // Tvheadend has no broadcast discovery; add manually.

    /// <summary>Gets EPG programs for one of this host's channels. Used by the HTSP listings provider.</summary>
    /// <param name="channelId">The prefixed channel id.</param>
    /// <param name="startDateUtc">The window start.</param>
    /// <param name="endDateUtc">The window end.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The programs.</returns>
    public async Task<IEnumerable<ProgramInfo>> GetProgramsAsync(
        string channelId, DateTime startDateUtc, DateTime endDateUtc, CancellationToken cancellationToken)
    {
        var (tuner, tvhChannelId) = Resolve(channelId);
        var client = await ClientForAsync(tuner, cancellationToken).ConfigureAwait(false);

        // Where our Image endpoint lives for THIS server: an imagecache id only means something on the
        // Tvheadend that issued it, so the key has to travel with it.
        var imageBase = LocalBaseUrl() + "/HtspTuner/Image/" + StableKey(client.Options) + "/";
        return client.GetEvents(tvhChannelId, startDateUtc, endDateUtc)
            .Select(e => Epg.EpgMapper.ToProgram(channelId, e, imageBase))
            .ToList();
    }

    /// <summary>Gets a value indicating whether any HTSP tuner is configured.</summary>
    public bool HasTuners => Tuners().Any();

    // Tvheadend pushes EPG and channel changes as they happen (debounced in HtspClient), which is the only
    // reason a guide can be near-live instead of up to 24 hours stale. What we CANNOT do is apply just the
    // change: IGuideManager exposes RefreshGuide() and nothing else -- no per-channel entry point exists --
    // and the work is all Jellyfin's (a DB read, diff and write per channel, plus artwork pre-caching), so
    // it costs minutes on a large channel list. Hence a rate limit, not a poll: at most one refresh per
    // AutoGuideRefreshMinutes, and never while one is already running.
    private void OnGuideDataChanged()
    {
        var minutes = Plugin.Instance?.Configuration.AutoGuideRefreshMinutes ?? 0;
        if (minutes <= 0)
        {
            return; // opt-in; otherwise Jellyfin's own 24h schedule owns the guide
        }

        lock (_refreshGate)
        {
            if (DateTime.UtcNow - _lastGuideRefreshUtc < TimeSpan.FromMinutes(minutes))
            {
                return;
            }

            _lastGuideRefreshUtc = DateTime.UtcNow;
        }

        try
        {
            var worker = _taskManager.ScheduledTasks.FirstOrDefault(
                t => string.Equals(t.ScheduledTask.Key, RefreshGuideTaskKey, StringComparison.Ordinal));
            if (worker is null)
            {
                _logger.LogDebug("No {Key} task registered; leaving the guide to Jellyfin", RefreshGuideTaskKey);
                return;
            }

            if (worker.State != TaskState.Idle)
            {
                _logger.LogDebug("Guide refresh already {State}; skipping this push", worker.State);
                return;
            }

            _logger.LogInformation("Tvheadend pushed guide changes; queueing a Jellyfin guide refresh");
            _taskManager.QueueScheduledTask(worker.ScheduledTask, new TaskOptions());
        }
        catch (Exception ex)
        {
            // Never let a background push break the tuner.
            _logger.LogWarning(ex, "Could not queue the guide refresh");
        }
    }

    /// <inheritdoc/>
    public async Task Validate(TunerHostInfo info)
    {
        // Fail fast with a human message so the "Add Tuner Device" dialog (and our config page's Test
        // button) can show what went wrong instead of the user having to read logs.
        var opts = OptionsFor(info);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var client = new HtspClient(opts, _logger);
        try
        {
            await client.ConnectOnlyAsync(cts.Token).ConfigureAwait(false);
        }
        catch (HtspAuthenticationException)
        {
            throw; // already a clear "check user name/password" message
        }
        catch (OperationCanceledException)
        {
            throw new HtspServerException($"Could not reach Tvheadend at {opts.Host}:{opts.Port} within 12 seconds.");
        }
        catch (Exception ex)
        {
            throw new HtspServerException($"Could not connect to Tvheadend at {opts.Host}:{opts.Port}: {ex.Message}");
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }

        // The connection is good, so the tuner is about to be saved: make sure the HTSP guide provider
        // exists too, so the user gets EPG without hunting through "TV Guide Data Providers".
        EnsureGuideProvider();
    }

    // Records which lineup the guide is using. Jellyfin's web UI renders the "TV Guide Data Providers" list
    // from a hardcoded switch on the provider type (getProviderName / getProviderConfigurationUrl), and no
    // plugin can add itself to it: our rows are always titled "Unknown" and link nowhere. The one thing a
    // plugin controls on that row is its subtitle -- `Path || ListingsId` -- so setting this is the
    // difference between an anonymous "Unknown" row and one the user can recognise as ours.
    internal const string LineupId = "htsp";

    private void EnsureGuideProvider()
    {
        try
        {
            var options = _config.GetConfiguration<LiveTvOptions>("livetv");
            var providers = options.ListingProviders?.ToList() ?? new List<ListingsProviderInfo>();
            var existing = providers.Find(p => string.Equals(p.Type, Type, StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                providers.Add(new ListingsProviderInfo
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Type = Type, // "htsp"
                    EnableAllTuners = true,
                    ListingsId = LineupId,
                });
                _logger.LogInformation("Auto-registered the HTSP guide provider for all HTSP tuners");
            }
            else if (string.IsNullOrEmpty(existing.ListingsId))
            {
                // Registered by an older build that left the lineup blank, which renders as an unlabelled
                // row. Backfill rather than leave it nameless forever -- this path is why the check above
                // is not just an early return.
                existing.ListingsId = LineupId;
                _logger.LogInformation("Labelled the existing HTSP guide provider with its lineup");
            }
            else
            {
                return;
            }

            options.ListingProviders = providers.ToArray();
            _config.SaveConfiguration("livetv", options);
        }
        catch (Exception ex)
        {
            // Non-fatal: the user can still add the HTSP guide manually under TV Guide Data Providers.
            _logger.LogWarning(ex, "Could not auto-register the HTSP guide provider");
        }
    }

    private IEnumerable<TunerHostInfo> Tuners()
        => _config.GetConfiguration<LiveTvOptions>("livetv").TunerHosts
            .Where(t => string.Equals(t.Type, Type, StringComparison.OrdinalIgnoreCase));

    private List<ChannelInfo> ChannelsFor(TunerHostInfo tuner, HtspClient client)
    {
        var key = StableKey(client.Options);
        var baseUrl = LocalBaseUrl();
        var tagNames = Plugin.Instance?.Configuration.ImportChannelTags == true
            ? client.GetTags().ToDictionary(t => t.Id, t => t.Name)
            : null;

        return client.GetChannels()
            .OrderBy(c => c.Number == 0 ? int.MaxValue : c.Number)
            .Select(c =>
            {
                var channelId = ChannelIdPrefix + key + "_" + c.Id.ToString(CultureInfo.InvariantCulture);
                return new ChannelInfo
                {
                    Id = channelId,
                    TunerHostId = tuner.Id,
                    Name = c.Name,
                    Number = c.DisplayNumber,
                    ImageUrl = IconUrl(c.Icon, channelId, baseUrl),
                    ChannelType = c.IsRadio ? ChannelType.Radio : ChannelType.TV,
                    Tags = TagsFor(c, tagNames),
                };
            })
            .ToList();
    }

    // Tvheadend groups channels with tags ("Sports", "HD", "Kids", ...) and pushes them over HTSP; we cached
    // them and threw them away. ChannelInfo.Tags is the only channel taxonomy Jellyfin accepts -- there is no
    // Genres field on it -- and no built-in tuner populates it, but it does reach LiveTvChannel.Tags and so
    // becomes queryable (e.g. /Items?IncludeItemTypes=LiveTvChannel&Tags=Sports).
    private static string[]? TagsFor(HtspChannel channel, Dictionary<long, string>? tagNames)
    {
        if (tagNames is null)
        {
            return null; // opt-out: leave Tags untouched rather than clearing what is there
        }

        return channel.TagIds
            .Select(id => tagNames.GetValueOrDefault(id))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // External logo URLs are used as-is; Tvheadend's own icons (imagecache/N paths) are served by our
    // /HtspTuner/Icon endpoint, which fetches them over HTSP -- so no HTTP access to Tvheadend is needed.
    private static string? IconUrl(string? icon, string channelId, string baseUrl)
    {
        if (string.IsNullOrEmpty(icon))
        {
            return null;
        }

        return icon.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? icon
            : baseUrl + "/HtspTuner/Icon/" + channelId;
    }

    // Loopback base for the icon endpoint. Jellyfin fetches channel images server-side, so 127.0.0.1 is
    // always reachable and dodges auto-detecting a wrong NIC on a multi-homed host.
    private string LocalBaseUrl()
    {
        try
        {
            var uri = new Uri(_appHost.GetApiUrlForLocalAccess());
            return $"{uri.Scheme}://127.0.0.1:{uri.Port}";
        }
        catch (UriFormatException)
        {
            return _appHost.GetApiUrlForLocalAccess().TrimEnd('/');
        }
    }

    /// <summary>Lists the streaming profiles the configured Tvheadend grants this login.</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The profiles, or an empty list if no tuner is configured or the server is unreachable.</returns>
    public async Task<IReadOnlyList<HtspProfile>> GetProfilesAsync(CancellationToken cancellationToken)
    {
        var tuner = Tuners().FirstOrDefault();
        if (tuner is null)
        {
            return Array.Empty<HtspProfile>();
        }

        try
        {
            var client = await ClientForAsync(tuner, cancellationToken).ConfigureAwait(false);
            return await client.GetProfilesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Suggestions are a nicety: the config page falls back to free text.
            _logger.LogDebug(ex, "Could not list Tvheadend streaming profiles");
            return Array.Empty<HtspProfile>();
        }
    }

    /// <summary>
    /// A point-in-time view of every live HTSP subscription, for the config page's status panel.
    /// </summary>
    /// <remarks>
    /// Tvheadend pushes <c>signalStatus</c> and <c>queueStatus</c> for each subscription and we already
    /// decode both; this is the only place they surface. Jellyfin has nowhere to show "which mux is this
    /// tuned on, is the signal healthy, are frames being dropped" — that answer only exists here.
    /// Reads cached state only: no HTSP round-trip, so polling it is free.
    /// </remarks>
    /// <returns>One entry per active subscription, across every configured server.</returns>
    public IReadOnlyList<HtspTunerSnapshot> GetTunerStatus()
        => _clients.Values
            .SelectMany(c => c.ActiveSubscriptions.Select(s => new HtspTunerSnapshot
            {
                SubscriptionId = s.Id,
                ChannelId = s.ChannelId,
                ChannelName = c.GetChannel(s.ChannelId)?.Name,
                Adapter = s.Start?.SourceInfo?.Adapter,
                Mux = s.Start?.SourceInfo?.Mux,
                Signal = s.Signal,
                Queue = s.Queue,
                Status = s.LastError?.Message,
                IsStarted = s.Start is not null,
                StartedUtc = s.CreatedUtc,
            }))
            .OrderBy(s => s.SubscriptionId)
            .ToList();

    /// <summary>Fetches one EPG artwork image from Tvheadend over HTSP.</summary>
    /// <remarks>
    /// Deliberately NOT cached in-process, unlike channel icons: there are a handful of channels but
    /// thousands of programme images, so caching them all would be a memory bomb. Jellyfin copies each
    /// image to local storage on first fetch and only pre-caches newly-added programmes, so it does not
    /// come back for the same id — the endpoint's cache header covers the rest.
    /// </remarks>
    /// <param name="serverKey">The stable key of the Tvheadend that issued the id.</param>
    /// <param name="imageId">The image-cache id.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The image bytes and content type, or null if it could not be fetched.</returns>
    public async Task<(byte[] Data, string ContentType)?> GetEpgImageAsync(
        string serverKey, long imageId, CancellationToken cancellationToken)
    {
        try
        {
            var tuner = Tuners().FirstOrDefault(t =>
                string.Equals(StableKey(OptionsFor(t)), serverKey, StringComparison.Ordinal));
            if (tuner is null)
            {
                return null;
            }

            var client = await ClientForAsync(tuner, cancellationToken).ConfigureAwait(false);
            var data = await client
                .ReadFileAsync(ImageCachePath(imageId), cancellationToken).ConfigureAwait(false);
            return data.Length == 0 ? null : (data, ContentTypeOf(data));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not fetch EPG image {Image} over HTSP", imageId);
            return null;
        }
    }

    /// <summary>Builds the Tvheadend file path for an image-cache id.</summary>
    /// <param name="imageId">The image-cache id.</param>
    /// <returns>The path to pass to <c>fileOpen</c>.</returns>
    internal static string ImageCachePath(long imageId)
        => "imagecache/" + imageId.ToString(CultureInfo.InvariantCulture);

    /// <summary>Fetches a channel's icon from Tvheadend over HTSP (cached per channel).</summary>
    /// <param name="channelId">The prefixed channel id.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The icon bytes and content type, or null if the channel has no fetchable icon.</returns>
    public async Task<(byte[] Data, string ContentType)?> GetChannelIconAsync(
        string channelId, CancellationToken cancellationToken)
    {
        if (_iconCache.TryGetValue(channelId, out var cached))
        {
            return cached;
        }

        try
        {
            var (tuner, tvhId) = Resolve(channelId);
            var client = await ClientForAsync(tuner, cancellationToken).ConfigureAwait(false);
            var icon = client.GetChannels().FirstOrDefault(c => c.Id == tvhId)?.Icon;
            if (icon is not { Length: > 0 } || icon.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                return null; // no icon, or an external URL Jellyfin fetches directly
            }

            var data = await client.ReadFileAsync(icon, cancellationToken).ConfigureAwait(false);
            if (data.Length == 0)
            {
                return null;
            }

            var result = (data, ContentTypeOf(data));
            _iconCache[channelId] = result;
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not fetch icon for channel {Channel} over HTSP", channelId);
            return null;
        }
    }

    /// <summary>Gets the channels currently being streamed to somebody.</summary>
    /// <remarks>
    /// Sampling one of these costs Tvheadend nothing at all: the bytes are already in our ring, so no tuner
    /// is touched and nothing can be preempted.
    /// </remarks>
    /// <returns>The prefixed channel ids.</returns>
    internal IReadOnlyList<string> WatchedChannelIds()
        => _live.Where(kv => kv.Value.IsAlive).Select(kv => kv.Key).ToList();

    /// <summary>Gets the mux a channel was last tuned from, or null if it has never been tuned.</summary>
    /// <param name="channelId">The prefixed channel id.</param>
    /// <returns>The mux name.</returns>
    internal string? MuxOf(string channelId) => _muxByChannel.GetValueOrDefault(channelId);

    /// <summary>Grabs a single still frame from a channel.</summary>
    /// <remarks>
    /// Reuses a stream someone is already watching when there is one — that costs Tvheadend nothing at all,
    /// the bytes are already in our ring. Otherwise it tunes the channel itself, at the lowest possible
    /// subscription weight so a real viewer always wins the tuner.
    /// </remarks>
    /// <param name="channelId">The prefixed channel id.</param>
    /// <param name="logoPath">
    /// The channel logo to stamp into the corner, as a local file Jellyfin already holds, or null for none.
    /// </param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>
    /// The path to a temporary image file, which the caller owns and must delete, or the reason there is
    /// none. Callers report the reason: on a large lineup most channels can fail for one shared cause (a
    /// login without access, a dish pointed elsewhere) and "no thumbnails appeared" does not say which.
    /// </returns>
    internal async Task<FrameCapture> CaptureFrameAsync(
        string channelId, string? logoPath, CancellationToken cancellationToken)
    {
        var live = _live.GetValueOrDefault(channelId);
        if (live is { IsAlive: false })
        {
            _live.TryRemove(channelId, out _);
            live = null;
        }

        // One budget for the whole capture, not one per step. A cold channel chains several waits that each
        // have their own generous timeout -- the subscribe, the first byte, the one-off probe, the sample --
        // and a channel Tvheadend cannot tune sits through all of them. Unbounded, four dead channels turn a
        // 61s scan into a four-minute one.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(30));
        var ct = budget.Token;

        if (live is not null)
        {
            // The free case, and the one worth being able to see in the log: no tuner, no subscription, just
            // a second reader on bytes already in memory.
            _logger.LogInformation("Capturing a frame for channel {Channel} from the stream already open", channelId);
        }

        HtspLiveStream? owned = null;
        HtspSubscription? orphan = null;
        try
        {
            if (live is null)
            {
                var (tuner, tvhChannelId) = Resolve(channelId);
                var client = await ClientForAsync(tuner, ct).ConfigureAwait(false);
                // Two different things, worth reporting separately: a radio service has no video and never
                // will, whereas a channel Tvheadend has never heard of means Jellyfin is holding a channel
                // that no longer exists on the server.
                var channel = client.GetChannel(tvhChannelId);
                if (channel is null)
                {
                    return FrameCapture.Failed("channel no longer exists on Tvheadend");
                }

                if (channel.IsRadio)
                {
                    return FrameCapture.Failed("radio channel, no video to capture");
                }

                // Tuner priority is entirely Tvheadend's to decide, and weight is the only lever HTSP gives
                // us: it carries no inventory of tuners, so the plugin cannot know whether the server has one
                // or ten, or how many are free. Subscribing a frame grab below the weight used for watching
                // is what makes that unnecessary — Tvheadend hands a contended tuner to the heavier
                // subscription, so a viewer starting a channel takes ours away mid-capture and we simply come
                // back later. If the two weights were equal that guarantee would be gone, and a background
                // thumbnail is never worth making somebody wait for, so in that case do not capture at all.
                var weight = OptionsFor(tuner).SubscriptionWeight;
                if (weight <= CaptureWeight)
                {
                    return FrameCapture.Failed(
                        "the tuner's subscription weight is not above the capture weight, so a viewer "
                        + "could not take the tuner back");
                }

                // A separate, much shorter budget for the tune itself. HtspSubscription gives a channel 20s
                // to start and lets Tvheadend extend that with subscriptionGrace, which is right for someone
                // pressing play on a satellite channel whose LNB needs a moment -- but it means every channel
                // Tvheadend cannot currently tune costs the sweep half a minute, and on a large lineup those
                // failures, not the successes, are what the sweep spends its time on. A channel that is going
                // to tune does so in a second or two, so a thumbnail waits a fraction as long and moves on.
                // The failure is remembered, so the channel is not retried for another half hour anyway.
                using var tuneBudget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                tuneBudget.CancelAfter(TimeSpan.FromSeconds(
                    Math.Clamp(Plugin.Instance!.Configuration.ProgramImageTuneSeconds, 2, 60)));
                orphan = await client
                    .SubscribeAsync(tvhChannelId, tuneBudget.Token, CaptureWeight).ConfigureAwait(false);

                // Comfortably more than the sample we are about to take: a ring smaller than the read would
                // lap the reader mid-capture and hand ffmpeg a TS with a hole in it.
                owned = new HtspLiveStream(client, orphan, _appHost, _mediaEncoder, 4 << 20, _logger)
                {
                    OriginalStreamId = channelId,
                    SkipProbe = true,
                };

                // The stream owns the subscription from here, and closing it unsubscribes. Until this line
                // nothing else would have: a throw in between -- an unusual stream table upsetting the muxer,
                // say -- used to leave Tvheadend holding a subscription for good.
                orphan = null;
                await owned.Open(ct).ConfigureAwait(false);
                RememberMux(channelId, owned);
                live = owned;
            }

            var path = await ExtractFrameAsync(live, logoPath, ct).ConfigureAwait(false);
            return path is null ? FrameCapture.Failed("no frame could be decoded") : new FrameCapture(path, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Running out of budget is the ordinary outcome for a channel that will not tune.
            return FrameCapture.Failed("timed out tuning the channel");
        }
        catch (ObjectDisposedException)
        {
            // The stream we were reading from was closed underneath us -- the viewer stopped between our
            // liveness check and the read. Nothing is wrong; the channel simply is not open any more.
            _live.TryRemove(channelId, out _);
            return FrameCapture.Failed("the stream closed while it was being read");
        }
        catch (HtspServerException ex)
        {
            // The server is unreachable, so every remaining channel would fail the same way.
            _logger.LogDebug(ex, "Could not capture a frame from channel {Channel}", channelId);
            return FrameCapture.Unreachable(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not capture a frame from channel {Channel}", channelId);
            return FrameCapture.Failed(ex.Message);
        }
        finally
        {
            if (orphan is not null)
            {
                await UnsubscribeQuietlyAsync(channelId, orphan).ConfigureAwait(false);
            }

            if (owned is not null)
            {
                await owned.Close().ConfigureAwait(false);
            }
        }
    }

    private async Task UnsubscribeQuietlyAsync(string channelId, HtspSubscription subscription)
    {
        try
        {
            var (tuner, _) = Resolve(channelId);
            var client = await ClientForAsync(tuner, CancellationToken.None).ConfigureAwait(false);
            await client.UnsubscribeAsync(subscription).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not release the subscription for channel {Channel}", channelId);
        }
    }

    private void Remember(string channelId, HtspLiveStream stream)
    {
        _live[channelId] = stream;
        RememberMux(channelId, stream);
    }

    private void RememberMux(string channelId, HtspLiveStream stream)
    {
        if (MuxKey.For(stream.Subscription.Start?.SourceInfo) is { Length: > 0 } mux)
        {
            _muxByChannel[channelId] = mux;
        }
    }

    private async Task<string?> ExtractFrameAsync(
        HtspLiveStream live, string? logoPath, CancellationToken cancellationToken)
    {
        var video = live.MediaSource.MediaStreams
            ?.FirstOrDefault(s => s.Type == MediaBrowser.Model.Entities.MediaStreamType.Video);
        if (video is null)
        {
            return null;
        }

        var samplePath = Path.Combine(Path.GetTempPath(), "htsp-frame-" + Guid.NewGuid().ToString("N") + ".ts");
        try
        {
            if (await live.CaptureSampleAsync(samplePath, HtspLiveStream.ThumbnailSampleBytes, cancellationToken)
                    .ConfigureAwait(false) == 0)
            {
                return null;
            }

            // ffmpeg's own thumbnail filter picks the most representative frame of the slice, which beats
            // taking the first one: a channel that cuts to black or a station ident is common at any offset.
            var frame = await _mediaEncoder.ExtractVideoImage(
                samplePath,
                "ts",
                new MediaSourceInfo
                {
                    Protocol = MediaBrowser.Model.MediaInfo.MediaProtocol.File,
                    Path = samplePath,
                    Container = "ts",
                },
                video,
                threedFormat: null,
                offset: null,
                cancellationToken).ConfigureAwait(false);

            var width = video.Width ?? 0;
            if (frame is null || width < 16)
            {
                return frame;
            }

            var branded = await OverlayLogoAsync(frame, logoPath, width, cancellationToken).ConfigureAwait(false);
            if (branded is null)
            {
                return frame; // no logo, or the overlay failed -- the bare frame is still worth having
            }

            TryDelete(frame);
            return branded;
        }
        finally
        {
            TryDelete(samplePath);
        }
    }

    // Stamp the channel's own logo into the corner of a captured frame, so a wall of thumbnails still says
    // which channel each one is from. Sizes are a share of the frame width, which is the only way a single
    // setting can hold for an SD, an HD and a UHD channel side by side.
    private async Task<string?> OverlayLogoAsync(
        string framePath, string? logoPath, int frameWidth, CancellationToken cancellationToken)
    {
        var cfg = Plugin.Instance!.Configuration;
        var size = cfg.ProgramImageLogoPercent / 100d;

        // Whatever Jellyfin already stored for the channel, which is the same picture it draws on the channel
        // tile. Deliberately not fetched over HTSP: the artwork is already on disk, and channels whose logo
        // is an external URL have one here too even though HTSP cannot hand it to us.
        if (size <= 0 || logoPath is null || !File.Exists(logoPath))
        {
            return null;
        }

        var outPath = Path.Combine(Path.GetTempPath(), "htsp-branded-" + Guid.NewGuid().ToString("N") + ".jpg");
        try
        {
            var margin = cfg.ProgramImageLogoMarginPercent / 100d;
            var opacity = Math.Clamp(cfg.ProgramImageLogoBackdropPercent / 100d, 0, 1);
            var spread = Math.Max(1.0, cfg.ProgramImageLogoBackdropSpread / 100d);

            // The logo is the one thing that needs a pixel size: ffmpeg's scale filter has no way to express
            // "a share of that other input". Everything after it is relative to its own input, so the
            // placement stays correct whatever the frame turns out to be.
            var logoWidth = Math.Max(16, (int)Math.Round(frameWidth * size));

            // Every capture is placed the same way. Which card shape Jellyfin will use is decided by the web
            // client per section when it renders -- a portrait card crops a 16:9 frame to its middle third and
            // takes a corner logo with it -- and nothing on the item says which section it will appear in, or
            // stops it appearing in several. So there is nothing to detect, and one consistent placement beats
            // a special case that would only be right some of the time.
            var place = FormattableString.Invariant(
                $"overlay=x=main_w-overlay_w-main_w*{margin}:y=main_h-overlay_h-main_w*{margin}");

            var backdrop = Backdrop(logoPath, logoWidth, margin, opacity, spread);
            var filter = backdrop is null
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"[1:v]format=rgba,scale={logoWidth}:-1[lg];[0:v][lg]{place}[o]")
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"[1:v]format=rgba,scale={logoWidth}:-1[lg];{backdrop.Value.Chain}"
                    + $"[0:v][disc]{backdrop.Value.Place}[bg];[bg][lg]{place}[o]");

            var psi = new System.Diagnostics.ProcessStartInfo(_mediaEncoder.EncoderPath)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var arg in new[]
                     {
                         "-y", "-v", "error", "-i", framePath, "-i", logoPath,
                         "-filter_complex", filter, "-map", "[o]", "-frames:v", "1", "-q:v", "2", outPath,
                     })
            {
                psi.ArgumentList.Add(arg);
            }

            using var ffmpeg = System.Diagnostics.Process.Start(psi)
                               ?? throw new InvalidOperationException("ffmpeg did not start");
            var stderr = await ffmpeg.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await ffmpeg.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            if (ffmpeg.ExitCode != 0 || !File.Exists(outPath))
            {
                _logger.LogDebug(
                    "Logo overlay failed for {Logo} (exit {Code}): {Error}",
                    logoPath, ffmpeg.ExitCode, stderr.Trim());
                return null;
            }

            return outPath;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not overlay the channel logo {Logo}", logoPath);
            TryDelete(outPath);
            return null;
        }
    }

    // A black disc behind the logo: solid out to the logo's own corner, then fading to nothing. A drop
    // shadow only outlines a logo, which is not enough on a frame that happens to be dark or busy behind it
    // -- and a still from a live broadcast can be anything at all. Returns null if the logo's dimensions
    // cannot be read, in which case the logo is drawn bare rather than on a mis-sized disc.
    private static (string Chain, string Place)? Backdrop(
        string logoPath, int logoWidth, double margin, double opacity, double spread)
    {
        if (opacity <= 0 || ImageSize.Read(logoPath) is not { } intrinsic || intrinsic.Width <= 0)
        {
            return null;
        }

        var logoHeight = Math.Max(1, (int)Math.Round(logoWidth * (double)intrinsic.Height / intrinsic.Width));

        // Solid to the logo's corner, with a hair of margin, then fading out to spread times that. A gradient
        // that starts at the centre instead leaves the logo sitting on almost nothing, which defeats it.
        var solid = Math.Sqrt((logoWidth * (double)logoWidth) + (logoHeight * (double)logoHeight)) / 2 * 1.05;
        var edge = solid * spread;
        var canvas = ((int)Math.Round(edge * 2)) | 1;

        // ponytail: the fade curve is fixed. It shapes how abruptly the disc gives way, which is a look
        // rather than something that needs to suit a particular setup; the two knobs that do are settings.
        const double Falloff = 1.6;

        var chain = string.Create(
            CultureInfo.InvariantCulture,
            $"color=black:s={canvas}x{canvas},format=rgba,"
            + $"geq=r=0:g=0:b=0:a='255*{opacity}*pow(clip(({edge}-hypot(X-{canvas}/2,Y-{canvas}/2))/({edge}-{solid}),0,1),{Falloff})'[disc];");

        // Centred on the logo, which means its own size cannot anchor it: the disc is bigger than the logo,
        // so placing both by their bottom-right corner puts the disc up and to the left of what it is meant
        // to sit behind. Offset from the logo's centre instead.
        var place = string.Create(
            CultureInfo.InvariantCulture,
            $"overlay=x=main_w-main_w*{margin}-{logoWidth / 2.0}-{canvas / 2.0}"
            + $":y=main_h-main_w*{margin}-{logoHeight / 2.0}-{canvas / 2.0}");

        return (chain, place);
    }

    /// <summary>Deletes a temporary file, ignoring the usual reasons it might not be there.</summary>
    /// <param name="path">The file to delete.</param>
    internal static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort — a small temp file
        }
    }

    private static string ContentTypeOf(byte[] d) => d switch
    {
        [0x89, 0x50, ..] => "image/png",
        [0xFF, 0xD8, ..] => "image/jpeg",
        [0x47, 0x49, 0x46, ..] => "image/gif",
        [0x3C, ..] => "image/svg+xml",
        _ => "image/png",
    };

    // A stable per-server key so channel ids survive removing and re-adding a tuner. Jellyfin assigns a
    // fresh TunerHostInfo.Id (GUID) every time a tuner is added, and the old scheme baked that into the
    // channel id -- so each re-add produced a whole new set of channels and the old ones piled up as
    // orphans. Deriving the key from the resolved host:port instead means the same Tvheadend always yields
    // the same channel ids, while different servers stay distinct.
    internal static string StableKey(HtspClientOptions opts)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(opts.Host + ":" + opts.Port.ToString(CultureInfo.InvariantCulture));
        var hash = System.Security.Cryptography.SHA1.HashData(bytes);
        return Convert.ToHexString(hash, 0, 6).ToLowerInvariant(); // 12 stable hex chars
    }

    private (TunerHostInfo Tuner, long ChannelId) Resolve(string channelId)
    {
        // channelId = "htsp_{serverKey}_{tvhChannelId}" (serverKey = StableKey of the host:port)
        var rest = channelId.StartsWith(ChannelIdPrefix, StringComparison.Ordinal) ? channelId[ChannelIdPrefix.Length..] : channelId;
        var split = rest.LastIndexOf('_');
        var serverKey = split > 0 ? rest[..split] : string.Empty;
        var tvhId = long.Parse(rest[(split + 1)..], CultureInfo.InvariantCulture);
        var tuner = Tuners().FirstOrDefault(t => string.Equals(StableKey(OptionsFor(t)), serverKey, StringComparison.Ordinal))
                    ?? throw new InvalidOperationException($"No HTSP tuner owns channel {channelId}");
        return (tuner, tvhId);
    }

    private async Task<HtspClient> ClientForAsync(TunerHostInfo tuner, CancellationToken cancellationToken)
    {
        var opts = OptionsFor(tuner);
        var key = $"{opts.Host}:{opts.Port}:{opts.Username}";

        // Reuse the cached client. HtspConnection reconnects itself indefinitely, so a momentary
        // disconnect is NOT a reason to build a whole new connection -- doing that (the old
        // `&& existing.IsConnected` check, with no lock) let concurrent callers and every transient
        // reconnect spawn fresh sockets and full re-syncs, which piled up connections, contended
        // Tvheadend's tuners and stalled live streams. One client per server, created once.
        if (_clients.TryGetValue(key, out var existing))
        {
            return existing;
        }

        await _clientLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_clients.TryGetValue(key, out existing))
            {
                return existing;
            }

            var client = new HtspClient(opts, _logger);

            // Both mean "Jellyfin's copy of the guide is now stale". Subscribed before connecting so the
            // pushes that arrive during the initial sync are not missed; the handler rate-limits itself and
            // does nothing at all until the user opts in.
            client.EpgChanged += OnGuideDataChanged;
            client.ChannelsChanged += OnGuideDataChanged;

            try
            {
                await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await client.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            _clients[key] = client;
            return client;
        }
        finally
        {
            _clientLock.Release();
        }
    }

    // Resolve a tuner's connection details.
    //
    // Jellyfin's web UI hardcodes the config form per built-in tuner type (hdhomerun, m3u) and has no
    // way to render one for a plugin type, so today an HTSP tuner is added with an EMPTY Url. Until that
    // is fixed upstream, the plugin's own settings page is the source of truth and we fall back to it
    // here. The per-tuner Url is still honoured when present ("host", "host:port" or
    // "http://user:pass@host:port"), so the day Jellyfin allows per-tuner fields, they take precedence
    // with no code change.
    private static HtspClientOptions OptionsFor(TunerHostInfo tuner)
    {
        var cfg = Plugin.Instance!.Configuration;
        var raw = tuner.Url ?? string.Empty;
        var host = cfg.Host;
        var port = cfg.HtspPort;
        var user = cfg.Username;
        var pass = cfg.Password;

        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
        {
            host = uri.Host;
            if (uri.Port > 0)
            {
                port = uri.Port;
            }

            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                var parts = uri.UserInfo.Split(':', 2);
                user = Uri.UnescapeDataString(parts[0]);
                pass = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : pass;
            }
        }
        else if (!string.IsNullOrWhiteSpace(raw))
        {
            var parts = raw.Split(':', 2);
            host = parts[0];
            if (parts.Length > 1 && int.TryParse(parts[1], out var p))
            {
                port = p;
            }
        }
        // else: blank Url -> keep the plugin's global host/port (the fallback described above).

        return new HtspClientOptions
        {
            Host = host,
            Port = port,
            Username = user,
            Password = pass,
            Profile = cfg.Profile,
            SubscriptionWeight = cfg.SubscriptionWeight,
        };
    }
}
