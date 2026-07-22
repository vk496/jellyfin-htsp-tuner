using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace HtspTuner.Htsp;

/// <summary>
/// Thrown when a subscription fails to start or is torn down by Tvheadend, carrying the machine token
/// (such as <c>noFreeAdapter</c>) so callers can react to tuner scarcity specifically.
/// </summary>
internal sealed class HtspSubscriptionException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="HtspSubscriptionException"/> class.</summary>
    /// <param name="message">The human-readable message.</param>
    /// <param name="error">The stable machine token, when Tvheadend supplied one.</param>
    public HtspSubscriptionException(string message, string? error = null)
        : base(message)
    {
        Error = error;
    }

    /// <summary>Gets the stable machine-readable error token, such as <c>noFreeAdapter</c>.</summary>
    public string? Error { get; }

    /// <summary>Gets a value indicating whether the failure is "no free adapter" tuner scarcity.</summary>
    public bool IsNoFreeAdapter =>
        string.Equals(Error, "noFreeAdapter", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// One HTSP subscription: sends <c>subscribe</c>, waits for <c>subscriptionStart</c> (failing fast on a
/// repeated <c>subscriptionError</c> that never becomes a start), and surfaces mux packets through a
/// bounded, drop-oldest channel so a slow reader can never balloon memory.
/// </summary>
internal sealed class HtspSubscription : IAsyncDisposable
{
    private const int PacketBufferCapacity = 4000;

    private readonly HtspConnection _connection;
    private readonly ILogger _logger;
    private readonly Channel<HtspMuxPacket> _packets;
    private readonly object _gate = new();

    private TaskCompletionSource<HtspSubscriptionStart>? _startTcs;
    private CancellationTokenSource? _startTimeoutCts;
    private bool _errorSeen;
    private volatile bool _stopped;

    /// <summary>Initializes a new instance of the <see cref="HtspSubscription"/> class.</summary>
    /// <param name="connection">The owning connection.</param>
    /// <param name="subscriptionId">The unique subscription id.</param>
    /// <param name="logger">The logger.</param>
    public HtspSubscription(HtspConnection connection, int subscriptionId, ILogger logger)
    {
        _connection = connection;
        _logger = logger;
        Id = subscriptionId;
        _packets = System.Threading.Channels.Channel.CreateBounded<HtspMuxPacket>(
            new BoundedChannelOptions(PacketBufferCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
            });
    }

    /// <summary>Gets the unique subscription id.</summary>
    public int Id { get; }

    /// <summary>Gets the UTC time this subscription was created, for the status dashboard.</summary>
    public DateTime CreatedUtc { get; } = DateTime.UtcNow;

    /// <summary>Gets the channel id this subscription is watching. Zero until <see cref="StartAsync"/>.</summary>
    public long ChannelId { get; private set; }

    /// <summary>Gets the <c>subscriptionStart</c> payload, once received.</summary>
    public HtspSubscriptionStart? Start { get; private set; }

    /// <summary>Gets the mux UUID this subscription is tuned on, once known.</summary>
    public string? MuxUuid => Start?.SourceInfo?.MuxUuid;

    /// <summary>Gets the reader for the elementary-stream packets.</summary>
    public ChannelReader<HtspMuxPacket> Packets => _packets.Reader;

    /// <summary>Gets the latest signal status, or null if none has arrived.</summary>
    public HtspSignalStatus? Signal { get; private set; }

    /// <summary>Gets the latest queue status, or null if none has arrived.</summary>
    public HtspQueueStatus? Queue { get; private set; }

    /// <summary>Gets the latest status/error, whether a warning during streaming or the reason it stopped.</summary>
    public HtspSubscriptionError? LastError { get; private set; }

    /// <summary>Gets a value indicating whether the subscription has been stopped or disposed.</summary>
    public bool IsStopped => _stopped;

    // Diagnostic only: whether any subscription has ever seen a signalStatus message this process.
    private static int _signalSeen;

    /// <summary>
    /// Subscribes to a channel and waits for <c>subscriptionStart</c>.
    /// </summary>
    /// <remarks>
    /// Uses <c>90khz=1</c> so timestamps arrive already in the MPEG-TS 90 kHz timebase, and
    /// <c>normts=1</c> for normalised timestamps. If Tvheadend cannot tune it emits repeated
    /// <c>subscriptionStatus</c> with a <c>subscriptionError</c> and never a start, and never tears the
    /// subscription down — so this method times out, sends <c>unsubscribe</c> and throws.
    /// </remarks>
    /// <param name="channelId">The channel id.</param>
    /// <param name="weight">The subscription weight.</param>
    /// <param name="profile">The streaming profile, normally <c>htsp</c>.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The stream table.</returns>
    public async Task<HtspSubscriptionStart> StartAsync(
        long channelId,
        int weight,
        string profile,
        CancellationToken cancellationToken)
    {
        ChannelId = channelId;
        var tcs = new TaskCompletionSource<HtspSubscriptionStart>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _startTcs = tcs;
            _errorSeen = false;
        }

        var subscribe = new HtspMessage()
            .Add("method", "subscribe")
            .Add("channelId", channelId)
            .Add("subscriptionId", (long)Id)
            .Add("weight", (long)weight)
            .Add("profile", profile)
            .Add("queueDepth", 5_000_000L)
            .Add("90khz", 1L)
            .Add("normts", 1L);

        // subscribe returns an empty ack; the real result is the async subscriptionStart/Status.
        await _connection.SendAsync(subscribe, cancellationToken).ConfigureAwait(false);

        var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Base budget for a channel to start. Tvheadend extends this via subscriptionGrace while it is still
        // tuning (a satellite LNB lock can take a good while); HandleGrace below pushes the deadline out so
        // slow-tuning channels are not cut off prematurely.
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(20));
        lock (_gate)
        {
            _startTimeoutCts = timeoutCts;
        }

        try
        {
            var start = await tcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            // The name ("12130H"), not the uuid: the uuid is an identifier Tvheadend assigns each mux object
            // and reads as a meaningless hash, whereas the name is the frequency you can match against the
            // network's mux list. The network comes with it because a name is only unique within one.
            var si = start.SourceInfo;
            _logger.LogInformation(
                "Subscription {Id} started on channel {Channel}: {StreamCount} streams, mux {Mux} on {Network}",
                Id,
                channelId,
                start.Streams.Count,
                si?.Mux ?? si?.MuxUuid,
                si?.Network ?? si?.NetworkUuid);
            return start;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await SafeUnsubscribeAsync().ConfigureAwait(false);
            var msg = LastError?.Message ?? "Tvheadend did not start the subscription in time";
            throw new HtspSubscriptionException(msg, LastError?.Error);
        }
        catch (HtspSubscriptionException)
        {
            await SafeUnsubscribeAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            lock (_gate)
            {
                _startTimeoutCts = null;
            }

            timeoutCts.Dispose();
        }
    }

    /// <summary>Routes one subscription-scoped event into this subscription. Never throws.</summary>
    /// <param name="msg">The event message.</param>
    public void Deliver(HtspMessage msg)
    {
        try
        {
            switch (msg.Method)
            {
                case "subscriptionStart":
                    HandleStart(msg);
                    break;
                case "subscriptionStatus":
                    HandleStatus(msg);
                    break;
                case "subscriptionStop":
                    HandleStop(msg);
                    break;
                case "subscriptionGrace":
                    var grace = (int)msg.GetInt("graceTimeout");
                    _logger.LogDebug("Subscription {Id} grace {Seconds}s", Id, grace);
                    if (grace > 0)
                    {
                        lock (_gate)
                        {
                            try
                            {
                                // Tvheadend is telling us it needs more time to tune; wait as long as it
                                // asks (plus a small margin) instead of failing at the base budget.
                                _startTimeoutCts?.CancelAfter(TimeSpan.FromSeconds(grace + 5));
                            }
                            catch (ObjectDisposedException)
                            {
                                // start already completed; nothing to extend
                            }
                        }
                    }

                    break;
                case "subscriptionSkip":
                    break;
                case "queueStatus":
                    Queue = ParseQueue(msg);
                    break;
                case "signalStatus":
                    Signal = ParseSignal(msg);

                    // Once per process, so the settings page's empty Signal and SNR columns can be told
                    // apart: either Tvheadend never reports signal for these inputs -- a network fed through
                    // a pipe has none to report -- or it does and the columns are a fault at our end. Absent
                    // this line, both look identical.
                    if (Interlocked.Exchange(ref _signalSeen, 1) == 0)
                    {
                        _logger.LogInformation(
                            "Tvheadend reports signal status (subscription {Id}: {Status}, SNR {Snr}, signal {Signal})",
                            Id, Signal.Status, Signal.Snr, Signal.Signal);
                    }

                    break;
                case "muxpkt":
                    HandlePacket(msg);
                    break;
                default:
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Subscription {Id} failed to handle '{Method}'", Id, msg.Method);
        }
    }

    /// <summary>Sends <c>unsubscribe</c> and completes the packet channel. Idempotent.</summary>
    /// <returns>A task that completes once the unsubscribe has been sent.</returns>
    public async Task StopAsync()
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        _packets.Writer.TryComplete();
        await SafeUnsubscribeAsync().ConfigureAwait(false);
        _logger.LogDebug("Subscription {Id} stopped", Id);
    }

    /// <summary>
    /// Marks the subscription dead because the connection dropped. The server has already forgotten it,
    /// so there is nothing to unsubscribe; we just fail the consumer so the pump ends and the live stream
    /// closes (Jellyfin then re-opens on the fresh connection) instead of hanging forever.
    /// </summary>
    public void FailFromDisconnect()
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        var ex = new HtspSubscriptionException("HTSP connection dropped");
        _packets.Writer.TryComplete(ex);
        TaskCompletionSource<HtspSubscriptionStart>? tcs;
        lock (_gate)
        {
            tcs = _startTcs;
        }

        tcs?.TrySetException(ex);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private void HandleStart(HtspMessage msg)
    {
        var start = ParseStart(msg);
        Start = start;

        TaskCompletionSource<HtspSubscriptionStart>? tcs;
        lock (_gate)
        {
            tcs = _startTcs;
        }

        tcs?.TrySetResult(start);
    }

    private void HandleStatus(HtspMessage msg)
    {
        var status = msg.GetString("status");
        var error = msg.GetString("subscriptionError");

        // A healthy status message has neither key.
        if (status is null && error is null)
        {
            return;
        }

        LastError = new HtspSubscriptionError(status, error);
        _logger.LogWarning(
            "Subscription {Id} status: {Status} ({Error})", Id, status, error);

        if (Start is not null)
        {
            return;
        }

        // No start yet, and Tvheadend never tears a failed tune down: fail fast once it repeats.
        TaskCompletionSource<HtspSubscriptionStart>? tcs = null;
        lock (_gate)
        {
            if (_errorSeen)
            {
                tcs = _startTcs;
            }
            else
            {
                _errorSeen = true;
            }
        }

        tcs?.TrySetException(new HtspSubscriptionException(LastError.Message, error));
    }

    private void HandleStop(HtspMessage msg)
    {
        var status = msg.GetString("status");
        var error = msg.GetString("subscriptionError");
        if (status is not null || error is not null)
        {
            LastError = new HtspSubscriptionError(status, error);
        }

        _stopped = true;

        TaskCompletionSource<HtspSubscriptionStart>? tcs;
        lock (_gate)
        {
            tcs = _startTcs;
        }

        if (Start is null && tcs is not null)
        {
            tcs.TrySetException(new HtspSubscriptionException(
                LastError?.Message ?? "Subscription stopped before it started", LastError?.Error));
        }

        _packets.Writer.TryComplete(
            LastError is null ? null : new HtspSubscriptionException(LastError.Message, LastError.Error));
        _logger.LogInformation("Subscription {Id} stopped by server: {Error}", Id, LastError?.Message);
    }

    private void HandlePacket(HtspMessage msg)
    {
        var payload = msg.GetBin("payload");
        if (payload is null)
        {
            return;
        }

        char? frameType = null;
        if (msg.TryGet("frametype", out long ft))
        {
            frameType = (char)(int)ft;
        }

        var packet = new HtspMuxPacket
        {
            StreamIndex = (int)msg.GetInt("stream"),
            Payload = payload,
            Pts = msg.GetIntOrNull("pts"),
            Dts = msg.GetIntOrNull("dts"),
            Duration = msg.GetInt("duration"),
            FrameType = frameType,
        };

        // Bounded + DropOldest: TryWrite never blocks and silently evicts the oldest when full.
        _packets.Writer.TryWrite(packet);
    }

    private async Task SafeUnsubscribeAsync()
    {
        try
        {
            var msg = new HtspMessage()
                .Add("method", "unsubscribe")
                .Add("subscriptionId", (long)Id);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _connection.SendAsync(msg, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "unsubscribe for {Id} did not complete cleanly", Id);
        }
    }

    private static HtspSubscriptionStart ParseStart(HtspMessage msg)
    {
        var streams = new List<HtspStream>();
        foreach (var s in msg.GetMapList("streams"))
        {
            streams.Add(ParseStream(s));
        }

        var meta = msg.GetAll("meta").OfType<byte[]>().ToList();

        HtspSourceInfo? source = null;
        if (msg.GetMap("sourceinfo") is { } si)
        {
            source = new HtspSourceInfo
            {
                AdapterUuid = si.GetString("adapter_uuid"),
                MuxUuid = si.GetString("mux_uuid"),
                NetworkUuid = si.GetString("network_uuid"),
                Adapter = si.GetString("adapter"),
                Mux = si.GetString("mux"),
                Network = si.GetString("network"),
                NetworkType = si.GetString("network_type"),
                Satpos = si.GetString("satpos"),
                Provider = si.GetString("provider"),
                Service = si.GetString("service"),
            };
        }

        return new HtspSubscriptionStart
        {
            SubscriptionId = (int)msg.GetInt("subscriptionId"),
            Streams = streams,
            SourceInfo = source,
            Meta = meta,
        };
    }

    private static HtspStream ParseStream(HtspMessage s)
    {
        var rawType = s.GetString("type") ?? "UNKNOWN";
        return new HtspStream
        {
            Index = (int)s.GetInt("index"),
            Codec = ParseCodec(rawType),
            RawType = rawType,
            Language = s.GetString("language"),
            Width = NullableInt(s, "width"),
            Height = NullableInt(s, "height"),
            Duration = NullableInt(s, "duration"),
            AspectNum = NullableInt(s, "aspect_num"),
            AspectDen = NullableInt(s, "aspect_den"),
            Channels = NullableInt(s, "channels"),
            SampleRate = ResolveSampleRate(NullableInt(s, "rate")),
            CompositionId = NullableInt(s, "composition_id"),
            AncillaryId = NullableInt(s, "ancillary_id"),
            AudioType = (int)s.GetInt("audio_type"),
        };
    }

    private static int? NullableInt(HtspMessage m, string name)
        => m.TryGet(name, out long v) ? (int)v : null;

    private static HtspSignalStatus ParseSignal(HtspMessage m) => new()
    {
        Status = m.GetString("feStatus"),
        Snr = m.GetIntOrNull("feSNR"),
        Signal = m.GetIntOrNull("feSignal"),
        BitErrorRate = m.GetIntOrNull("feBER"),
        UncorrectedBlocks = m.GetIntOrNull("feUNC"),
        SnrScale = m.GetIntOrNull("feSNR_scale") ?? m.GetIntOrNull("feSNRScale"),
        SignalScale = m.GetIntOrNull("feSignal_scale") ?? m.GetIntOrNull("feSignalScale"),
    };

    private static HtspQueueStatus ParseQueue(HtspMessage m) => new()
    {
        Packets = m.GetInt("packets"),
        Bytes = m.GetInt("bytes"),
        Delay = m.GetInt("delay"),
        BdropCount = m.GetInt("Bdrops"),
        PdropCount = m.GetInt("Pdrops"),
        IdropCount = m.GetInt("Idrops"),
    };

    private static HtspCodec ParseCodec(string type) => type.ToUpperInvariant() switch
    {
        "H264" => HtspCodec.H264,
        "HEVC" => HtspCodec.Hevc,
        "MPEG2VIDEO" => HtspCodec.Mpeg2Video,
        "MPEG2AUDIO" => HtspCodec.Mpeg2Audio,
        "AAC" => HtspCodec.Aac,
        "MP4A" => HtspCodec.AacLatm,
        "AACLATM" => HtspCodec.AacLatm,
        "AC3" => HtspCodec.Ac3,
        "EAC3" => HtspCodec.Eac3,
        "VORBIS" => HtspCodec.Vorbis,
        "DVBSUB" => HtspCodec.DvbSub,
        "TELETEXT" => HtspCodec.Teletext,
        "TEXTSUB" => HtspCodec.TextSub,
        "PCR" => HtspCodec.Pcr,
        _ => HtspCodec.Unknown,
    };

    // MPEG-4 sampling-frequency table: the wire 'rate' is an INDEX into this, not a frequency.
    private static readonly int[] SampleRateTable =
    {
        96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050,
        16000, 12000, 11025, 8000, 7350,
    };

    private static int? ResolveSampleRate(int? sri)
        => sri is { } i && i >= 0 && i < SampleRateTable.Length ? SampleRateTable[i] : null;
}
