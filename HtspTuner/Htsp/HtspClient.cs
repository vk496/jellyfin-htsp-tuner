using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace HtspTuner.Htsp;

/// <summary>
/// The high-level HTSP session: owns a connection, keeps the async-metadata caches (channels, tags, EPG,
/// recordings, autorec rules) live from server pushes, and hands out subscriptions.
/// </summary>
internal sealed class HtspClient : IAsyncDisposable
{
    private static readonly DateTime _epoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly HtspConnection _connection;
    private readonly HtspClientOptions _options;
    private readonly ILogger _logger;

    private readonly ConcurrentDictionary<long, HtspChannel> _channels = new();
    private readonly ConcurrentDictionary<long, HtspTag> _tags = new();
    private readonly ConcurrentDictionary<long, HtspEvent> _events = new();

    // Events indexed by channel so a guide query is O(events-on-that-channel), not O(all 68k events).
    private readonly ConcurrentDictionary<long, ConcurrentDictionary<long, HtspEvent>> _eventsByChannel = new();
    private readonly ConcurrentDictionary<long, HtspDvrEntry> _dvr = new();
    private readonly ConcurrentDictionary<string, HtspAutorecEntry> _autorec = new();
    private readonly ConcurrentDictionary<int, HtspSubscription> _subscriptions = new();

    private readonly Debounce _channelsChanged;
    private readonly Debounce _epgChanged;
    private readonly Debounce _recordingsChanged;

    private TaskCompletionSource _sync = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _nextSubscriptionId;
    private volatile bool _synced;

    /// <summary>Initializes a new instance of the <see cref="HtspClient"/> class.</summary>
    /// <param name="options">The connection options.</param>
    /// <param name="logger">The logger.</param>
    public HtspClient(HtspClientOptions options, ILogger logger)
    {
        _options = options;
        _logger = logger;
        _connection = new HtspConnection(options, logger);
        _connection.EventReceived += OnEvent;
        _connection.Reconnected += OnReconnected;
        _channelsChanged = new Debounce(TimeSpan.FromSeconds(5), () => ChannelsChanged?.Invoke());
        _epgChanged = new Debounce(TimeSpan.FromSeconds(5), () => EpgChanged?.Invoke());
        _recordingsChanged = new Debounce(TimeSpan.FromSeconds(5), () => RecordingsChanged?.Invoke());
    }

    /// <summary>Raised, debounced, when channels or tags change after the initial sync.</summary>
    public event Action? ChannelsChanged;

    /// <summary>Raised, debounced, when EPG events change after the initial sync.</summary>
    public event Action? EpgChanged;

    /// <summary>Raised, debounced, when recordings or timers change after the initial sync.</summary>
    public event Action? RecordingsChanged;

    /// <summary>Gets a value indicating whether the connection is up and authenticated.</summary>
    public bool IsConnected => _connection.IsConnected;

    /// <summary>Gets the options this client was created with.</summary>
    public HtspClientOptions Options => _options;

    /// <summary>Connects, authenticates, and returns without waiting for metadata. For validation.</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task that completes once authenticated.</returns>
    public Task ConnectOnlyAsync(CancellationToken cancellationToken)
        => _connection.ConnectAsync(cancellationToken);

    /// <summary>Connects, enables async metadata and waits for the initial sync (bounded).</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task that completes once the first metadata sync is in.</returns>
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await _connection.ConnectAsync(cancellationToken).ConfigureAwait(false);
        await EnableMetadataAsync(cancellationToken).ConfigureAwait(false);

        // Never let a slow/broken sync hang Jellyfin: cap the wait and carry on with whatever arrived.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await _sync.Task.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("HTSP initial sync timed out; continuing with {Count} channels", _channels.Count);
        }
    }

    /// <summary>Gets a snapshot of the cached channels.</summary>
    /// <returns>The channels.</returns>
    public IReadOnlyList<HtspChannel> GetChannels() => _channels.Values.ToList();

    /// <summary>Gets a snapshot of the cached tags.</summary>
    /// <returns>The tags.</returns>
    public IReadOnlyList<HtspTag> GetTags() => _tags.Values.ToList();

    /// <summary>Gets the cached EPG events for a channel within a window.</summary>
    /// <param name="channelId">The channel id.</param>
    /// <param name="from">The window start.</param>
    /// <param name="to">The window end.</param>
    /// <returns>The matching events.</returns>
    public IReadOnlyList<HtspEvent> GetEvents(long channelId, DateTime from, DateTime to)
        => _eventsByChannel.TryGetValue(channelId, out var events)
            ? events.Values.Where(e => e.Stop > from && e.Start < to).OrderBy(e => e.Start).ToList()
            : Array.Empty<HtspEvent>();

    /// <summary>Gets a snapshot of the cached recordings and timers.</summary>
    /// <returns>The DVR entries.</returns>
    public IReadOnlyList<HtspDvrEntry> GetDvrEntries() => _dvr.Values.ToList();

    /// <summary>Gets a snapshot of the cached autorec (series) rules.</summary>
    /// <returns>The autorec rules.</returns>
    public IReadOnlyList<HtspAutorecEntry> GetAutorecs() => _autorec.Values.ToList();

    /// <summary>Looks up a cached channel.</summary>
    /// <param name="id">The channel id.</param>
    /// <returns>The channel, or null.</returns>
    public HtspChannel? GetChannel(long id) => _channels.GetValueOrDefault(id);

    /// <summary>Gets the active subscriptions, for the status dashboard.</summary>
    /// <returns>The subscriptions.</returns>
    public IReadOnlyList<HtspSubscription> ActiveSubscriptions => _subscriptions.Values.ToList();

    /// <summary>Subscribes to a channel and waits for the stream table.</summary>
    /// <param name="channelId">The channel id.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The started subscription.</returns>
    public async Task<HtspSubscription> SubscribeAsync(long channelId, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextSubscriptionId);
        var sub = new HtspSubscription(_connection, id, _logger);
        _subscriptions[id] = sub;
        try
        {
            await sub.StartAsync(channelId, _options.SubscriptionWeight, _options.Profile, cancellationToken)
                .ConfigureAwait(false);
            return sub;
        }
        catch
        {
            _subscriptions.TryRemove(id, out _);
            await sub.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Stops and forgets a subscription.</summary>
    /// <param name="sub">The subscription.</param>
    /// <returns>A task that completes once it is torn down.</returns>
    public async Task UnsubscribeAsync(HtspSubscription sub)
    {
        _subscriptions.TryRemove(sub.Id, out _);
        await sub.StopAsync().ConfigureAwait(false);
    }

    /// <summary>Requests an HTTP streaming ticket for a channel (used by the HTTP fallback).</summary>
    /// <param name="channelId">The channel id.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The ticket path and token.</returns>
    public async Task<HtspTicket> GetTicketAsync(long channelId, CancellationToken cancellationToken)
    {
        var reply = await _connection.SendAsync(
            new HtspMessage().Add("method", "getTicket").Add("channelId", channelId),
            cancellationToken).ConfigureAwait(false);
        return new HtspTicket(reply.GetString("path") ?? string.Empty, reply.GetString("ticket") ?? string.Empty);
    }

    /// <summary>Sends an arbitrary request, e.g. a DVR mutator, and returns the reply.</summary>
    /// <param name="message">The request.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The reply.</returns>
    public Task<HtspMessage> SendAsync(HtspMessage message, CancellationToken cancellationToken)
        => _connection.SendAsync(message, cancellationToken);

    /// <summary>
    /// Reads a server-side file over HTSP (<c>fileOpen</c>/<c>fileRead</c>/<c>fileClose</c>). Used for
    /// channel icons (<c>imagecache/N</c> paths) so the plugin never needs Tvheadend's HTTP interface.
    /// </summary>
    /// <param name="path">The Tvheadend file path, e.g. <c>imagecache/123</c>.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The file bytes.</returns>
    public async Task<byte[]> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        var open = await SendAsync(
            new HtspMessage().Add("method", "fileOpen").Add("file", path), cancellationToken).ConfigureAwait(false);
        var id = open.GetInt("id");
        var size = open.GetIntOrNull("size") ?? 0;
        try
        {
            using var buffer = new MemoryStream();
            var remaining = size > 0 ? size : 4L * 1024 * 1024; // cap when the server does not report a size
            while (remaining > 0)
            {
                var want = (int)Math.Min(remaining, 256 * 1024);
                var reply = await SendAsync(
                    new HtspMessage().Add("method", "fileRead").Add("id", id).Add("size", (long)want),
                    cancellationToken).ConfigureAwait(false);
                var chunk = reply.GetBin("data");
                if (chunk is not { Length: > 0 })
                {
                    break;
                }

                buffer.Write(chunk, 0, chunk.Length);
                remaining -= chunk.Length;
                if (chunk.Length < want)
                {
                    break; // short read = EOF
                }
            }

            return buffer.ToArray();
        }
        finally
        {
            try
            {
                await SendAsync(new HtspMessage().Add("method", "fileClose").Add("id", id), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // best effort; the server drops the handle when the connection closes anyway
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _channelsChanged.Dispose();
        _epgChanged.Dispose();
        _recordingsChanged.Dispose();
        foreach (var sub in _subscriptions.Values)
        {
            await sub.DisposeAsync().ConfigureAwait(false);
        }

        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    private async Task EnableMetadataAsync(CancellationToken cancellationToken)
    {
        var msg = new HtspMessage()
            .Add("method", "enableAsyncMetadata")
            .Add("epg", 1L)
            .Add("epgMaxTime", ToUnix(DateTime.UtcNow.AddDays(_options.EpgDays)));
        await _connection.SendNoReplyAsync(msg, cancellationToken).ConfigureAwait(false);
    }

    private void OnReconnected()
    {
        // Active subscriptions were orphaned by the drop: fail them so their pumps end and the live streams
        // close, rather than hanging on packets that will never arrive on the old (dead) subscription id.
        foreach (var sub in _subscriptions.Values)
        {
            sub.FailFromDisconnect();
        }

        _subscriptions.Clear();

        // Caches may be stale after a drop; clear and let the fresh sync repopulate.
        _channels.Clear();
        _tags.Clear();
        _events.Clear();
        _eventsByChannel.Clear();
        _dvr.Clear();
        _autorec.Clear();
        _synced = false;
        _sync = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = EnableMetadataAsync(CancellationToken.None);
    }

    private void OnEvent(HtspMessage m)
    {
        // Subscription-scoped traffic (muxpkt, subscriptionStart/Status/Stop, queue/signal) routes by id.
        if (m.TryGet("subscriptionId", out long sid)
            && _subscriptions.TryGetValue((int)sid, out var sub))
        {
            sub.Deliver(m);
            return;
        }

        switch (m.Method)
        {
            case "channelAdd":
            case "channelUpdate":
                _channels[m.GetInt("channelId")] = ParseChannel(m, _channels.GetValueOrDefault(m.GetInt("channelId")));
                Signal(_channelsChanged);
                break;
            case "channelDelete":
                _channels.TryRemove(m.GetInt("channelId"), out _);
                Signal(_channelsChanged);
                break;
            case "tagAdd":
            case "tagUpdate":
                _tags[m.GetInt("tagId")] = ParseTag(m);
                Signal(_channelsChanged);
                break;
            case "tagDelete":
                _tags.TryRemove(m.GetInt("tagId"), out _);
                Signal(_channelsChanged);
                break;
            case "eventAdd":
            case "eventUpdate":
                var ev = ParseEvent(m);
                _events[ev.Id] = ev;
                _eventsByChannel.GetOrAdd(ev.ChannelId, _ => new())[ev.Id] = ev;
                Signal(_epgChanged);
                break;
            case "eventDelete":
                if (_events.TryRemove(m.GetInt("eventId"), out var gone)
                    && _eventsByChannel.TryGetValue(gone.ChannelId, out var chEvents))
                {
                    chEvents.TryRemove(gone.Id, out _);
                }

                Signal(_epgChanged);
                break;
            case "dvrEntryAdd":
            case "dvrEntryUpdate":
                _dvr[m.GetInt("id")] = ParseDvr(m);
                Signal(_recordingsChanged);
                break;
            case "dvrEntryDelete":
                _dvr.TryRemove(m.GetInt("id"), out _);
                Signal(_recordingsChanged);
                break;
            case "autorecEntryAdd":
            case "autorecEntryUpdate":
                if (m.GetString("id") is { } aid)
                {
                    _autorec[aid] = ParseAutorec(m);
                    Signal(_recordingsChanged);
                }

                break;
            case "autorecEntryDelete":
                if (m.GetString("id") is { } did)
                {
                    _autorec.TryRemove(did, out _);
                    Signal(_recordingsChanged);
                }

                break;
            case "initialSyncCompleted":
                _synced = true;
                _sync.TrySetResult();
                _logger.LogInformation(
                    "HTSP initial sync: {Channels} channels, {Tags} tags, {Events} events, {Dvr} recordings",
                    _channels.Count, _tags.Count, _events.Count, _dvr.Count);
                break;
            default:
                break;
        }
    }

    private void Signal(Debounce d)
    {
        if (_synced)
        {
            d.Trigger();
        }
    }

    private static HtspChannel ParseChannel(HtspMessage m, HtspChannel? existing)
    {
        // Updates are partial: fall back to the previously cached value for absent fields.
        var services = m.GetMapList("services");
        var isRadio = services.Any(s =>
            string.Equals(s.GetString("type"), "Radio", StringComparison.OrdinalIgnoreCase));

        return new HtspChannel
        {
            Id = m.GetInt("channelId"),
            Name = m.GetString("channelName") ?? existing?.Name ?? string.Empty,
            Number = (int)m.GetInt("channelNumber", existing?.Number ?? 0),
            NumberMinor = (int)m.GetInt("channelNumberMinor", existing?.NumberMinor ?? 0),
            Icon = m.GetString("channelIcon") ?? existing?.Icon,
            IsRadio = services.Count > 0 ? isRadio : existing?.IsRadio ?? false,
            TagIds = m.TryGet("tags", out List<object>? _) ? m.GetIntList("tags") : existing?.TagIds ?? Array.Empty<long>(),
            EventId = m.GetIntOrNull("eventId") ?? existing?.EventId,
        };
    }

    private static HtspTag ParseTag(HtspMessage m) => new()
    {
        Id = m.GetInt("tagId"),
        Name = m.GetString("tagName") ?? string.Empty,
        Members = m.GetIntList("members"),
    };

    private static HtspEvent ParseEvent(HtspMessage m) => new()
    {
        Id = m.GetInt("eventId"),
        ChannelId = m.GetInt("channelId"),
        Start = FromUnix(m.GetInt("start")),
        Stop = FromUnix(m.GetInt("stop")),
        Title = m.GetString("title"),
        Subtitle = m.GetString("subtitle"),
        Description = m.GetString("description") ?? m.GetString("summary"),
        ContentType = (int)m.GetInt("contentType"),
        SeasonNumber = ToIntOrNull(m.GetIntOrNull("seasonNumber")),
        EpisodeNumber = ToIntOrNull(m.GetIntOrNull("episodeNumber")),
        SeriesLinkId = m.GetString("serieslinkUri") ?? (m.GetIntOrNull("serieslinkId")?.ToString()),
        AgeRating = ToIntOrNull(m.GetIntOrNull("ageRating")),
        StarRating = ToIntOrNull(m.GetIntOrNull("starRating")),
        FirstAired = m.GetIntOrNull("firstAired") is { } fa and > 0 ? FromUnix(fa) : null,
        Image = m.GetString("image"),
        CopyrightYear = ToIntOrNull(m.GetIntOrNull("copyrightYear")),
    };

    private static HtspDvrEntry ParseDvr(HtspMessage m) => new()
    {
        Id = m.GetInt("id"),
        ChannelId = m.GetInt("channel"),
        Start = FromUnix(m.GetInt("start")),
        Stop = FromUnix(m.GetInt("stop")),
        PrePaddingSeconds = (int)(m.GetInt("startExtra") * 60),   // Tvheadend stores padding in minutes
        PostPaddingSeconds = (int)(m.GetInt("stopExtra") * 60),
        Title = FirstLang(m, "title"),
        Subtitle = FirstLang(m, "subtitle"),
        Description = FirstLang(m, "description"),
        State = m.GetString("state"),
        Error = m.GetString("error"),
        AutorecId = m.GetString("autorecId"),
        EventId = m.GetIntOrNull("eventId"),
        ContentType = (int)m.GetInt("contentType"),
        FileSize = m.GetIntOrNull("dataSize"),
        Image = m.GetString("image"),
    };

    private static HtspAutorecEntry ParseAutorec(HtspMessage m) => new()
    {
        Id = m.GetString("id") ?? string.Empty,
        Title = m.GetString("title"),
        ChannelId = m.GetInt("channel"),
        Enabled = m.GetBool("enabled"),
        StartMinutes = ToIntOrNull(m.GetIntOrNull("start")),
        StartWindowMinutes = ToIntOrNull(m.GetIntOrNull("startWindow")),
        DaysOfWeek = (int)m.GetInt("daysOfWeek", 0x7F),
        PrePaddingSeconds = (int)(m.GetInt("startExtra") * 60),
        PostPaddingSeconds = (int)(m.GetInt("stopExtra") * 60),
        Priority = (int)m.GetInt("priority"),
        SeriesLinkId = m.GetString("serieslinkUri"),
    };

    // Tvheadend may send some text fields as a language-keyed map when configured multilingual.
    private static string? FirstLang(HtspMessage m, string field)
    {
        if (m.GetString(field) is { } s)
        {
            return s;
        }

        return m.GetMap(field)?.Fields.Select(f => f.Value as string).FirstOrDefault(v => v is not null);
    }

    private static int? ToIntOrNull(long? v) => v is { } x and > 0 ? (int)x : null;

    private static long ToUnix(DateTime utc) => (long)(utc - _epoch).TotalSeconds;

    private static DateTime FromUnix(long seconds) => _epoch.AddSeconds(seconds);

    /// <summary>A trailing-coalescing debounce: many triggers in a burst yield one delayed callback.</summary>
    private sealed class Debounce : IDisposable
    {
        private readonly TimeSpan _delay;
        private readonly Action _action;
        private readonly Lock _gate = new();
        private Timer? _timer;

        public Debounce(TimeSpan delay, Action action)
        {
            _delay = delay;
            _action = action;
        }

        public void Trigger()
        {
            lock (_gate)
            {
                _timer ??= new Timer(
                    _ =>
                    {
                        lock (_gate)
                        {
                            _timer?.Dispose();
                            _timer = null;
                        }

                        _action();
                    },
                    null,
                    _delay,
                    Timeout.InfiniteTimeSpan);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _timer?.Dispose();
                _timer = null;
            }
        }
    }
}
