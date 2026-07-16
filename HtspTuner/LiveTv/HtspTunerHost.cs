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
    private const string Prefix = "htsp_";

    // Jellyfin's own guide-refresh task. Matched by key because the type lives in the server's
    // Jellyfin.LiveTv assembly, which is not on NuGet, so the generic ITaskManager overloads that every
    // in-tree caller uses are out of reach here.
    private const string RefreshGuideTaskKey = "RefreshGuide";

    private readonly IConfigurationManager _config;
    private readonly IServerApplicationHost _appHost;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ITaskManager _taskManager;
    private readonly ILogger<HtspTunerHost> _logger;
    private readonly ConcurrentDictionary<string, HtspClient> _clients = new();
    private readonly ConcurrentDictionary<string, (byte[] Data, string ContentType)> _iconCache = new();
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
                var channelId = Prefix + key + "_" + c.Id.ToString(CultureInfo.InvariantCulture);
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
        var rest = channelId.StartsWith(Prefix, StringComparison.Ordinal) ? channelId[Prefix.Length..] : channelId;
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
