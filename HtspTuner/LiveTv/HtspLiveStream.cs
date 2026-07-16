using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using HtspTuner.Htsp;
using HtspTuner.Mpeg;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace HtspTuner.LiveTv;

/// <summary>
/// A live channel: pumps HTSP packets through the muxer into a bounded ring, and serves the muxed TS to
/// Jellyfin. Implements both interfaces Jellyfin needs — <see cref="ILiveStream"/> for lifecycle and
/// <see cref="IDirectStreamProvider"/> for the byte hand-off.
/// </summary>
internal sealed class HtspLiveStream : ILiveStream, IDirectStreamProvider
{
    // Fields HTSP never reports (interlacing, colour/HDR, bit depth, profile/level) are the same for every
    // viewing of a channel, so the first tune probes them and the rest reuse this — keyed by HTSP channel id.
    private static readonly ConcurrentDictionary<long, IReadOnlyList<MediaStream>> _probeCache = new();

    // How long a stream may sit with nobody reading it before we tear the subscription down. Must clear the
    // gap between Open() and the consumer actually connecting, which is ~20s on the Android TV client (it
    // asks for PlaybackInfo, then starts ffmpeg). A paused/throttled consumer still holds its reader open,
    // so it is not idle by this measure.
    // ponytail: fixed grace, no config knob. Promote to PluginConfiguration if a slow client trips it.
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    private readonly HtspClient _client;
    private readonly HtspSubscription _subscription;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly TsMuxer _muxer;
    private readonly StreamRing _ring;
    private readonly List<MediaStream> _streams;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _firstByte = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _gate = new();

    private Task? _pump;
    private Timer? _idleWatchdog;
    private long _lastReaderTicks;
    private int _closed;
    private int _probeStarted;

    /// <summary>Initializes a new instance of the <see cref="HtspLiveStream"/> class.</summary>
    /// <param name="client">The HTSP client.</param>
    /// <param name="subscription">The started subscription.</param>
    /// <param name="appHost">The application host, for the local stream URL.</param>
    /// <param name="mediaEncoder">ffprobe wrapper, to read back the fields HTSP can't report.</param>
    /// <param name="maxBufferBytes">The ring buffer cap.</param>
    /// <param name="logger">The logger.</param>
    public HtspLiveStream(
        HtspClient client,
        HtspSubscription subscription,
        IServerApplicationHost appHost,
        IMediaEncoder mediaEncoder,
        long maxBufferBytes,
        ILogger logger)
    {
        _client = client;
        _subscription = subscription;
        _mediaEncoder = mediaEncoder;
        _logger = logger;
        _muxer = new TsMuxer(subscription.Start!);
        _ring = new StreamRing(maxBufferBytes);
        UniqueId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

        _streams = MediaStreamBuilder.Build(_muxer.Streams);

        var source = SourceDescription(subscription.Start!.SourceInfo);
        if (source is not null && _streams.Find(s => s.Type == MediaStreamType.Video) is { } videoStream)
        {
            videoStream.Title = source;
        }

        MediaSource = new MediaSourceInfo
        {
            Id = UniqueId,
            // Where the subscription is tuned from (network / mux / satellite position), so it shows in
            // Jellyfin's playback media-info. Also stamped on the video stream title below.
            Name = source,
            // Self-served so consumption goes through GetStream()+ProgressiveFileStream (which polls at the
            // live edge) rather than a File path ffmpeg would treat as finite and stop at EOF.
            Protocol = MediaProtocol.Http,
            Path = LocalBaseUrl(appHost) + "/LiveTv/LiveStreamFiles/" + UniqueId + "/stream.ts",
            Type = MediaSourceType.Default,
            Container = "ts",
            IsInfiniteStream = true,
            RequiresOpening = true,
            RequiresClosing = true,
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            // We supply the full stream table ourselves, so the server never probes (its live-probe path
            // would drop multi-audio and strip languages); we run our own probe below instead. Keeping this
            // false also avoids ffmpeg's default 200s analyze on a no-EOF live stream — the old loading hang.
            SupportsProbing = false,
            SupportsTranscoding = true,
            AnalyzeDurationMs = 2000,
            MediaStreams = _streams,
        };

        // Reuse a previous probe of this channel so this stream is accurate from the first frame; otherwise
        // Open() probes once and populates the cache. A source-level bitrate (real once probed, estimated
        // until then) stops Jellyfin assuming a huge default and needlessly re-encoding the video.
        if (_probeCache.TryGetValue(ChannelId, out var cached))
        {
            MediaStreamBuilder.MergeProbe(_streams, cached);
            _probeStarted = 1;
        }

        RecomputeSourceBitrate();
    }

    /// <inheritdoc/>
    public int ConsumerCount { get; set; } = 1;

    /// <inheritdoc/>
    public string OriginalStreamId { get; set; } = string.Empty;

    /// <inheritdoc/>
    public string TunerHostId => "htsp";

    /// <inheritdoc/>
    public bool EnableStreamSharing { get; private set; } = true;

    /// <summary>
    /// Gets a value indicating whether this stream is still live and safe to share. A subscription can
    /// die silently (e.g. an HTSP reconnect orphaned it) without <see cref="Close"/> being called; reusing
    /// such a stream for a new viewer is what makes "hit play, nothing happens" occur.
    /// </summary>
    public bool IsAlive => _closed == 0 && !_subscription.IsStopped;

    /// <inheritdoc/>
    public MediaSourceInfo MediaSource { get; set; }

    /// <inheritdoc/>
    public string UniqueId { get; }

    /// <summary>Gets the channel id this stream serves.</summary>
    public long ChannelId => _subscription.ChannelId;

    /// <summary>Gets the underlying subscription, for the status dashboard.</summary>
    public HtspSubscription Subscription => _subscription;

    // A human-readable "where is this tuned from" line for Jellyfin's media info: the network (with the
    // satellite position when present) and the mux, e.g. "Abertis · MUX 12476H" or "Hispasat (30W) · 12476H".
    private static string? SourceDescription(HtspSourceInfo? si)
    {
        if (si is null)
        {
            return null;
        }

        var network = si.Network;
        if (!string.IsNullOrEmpty(si.Satpos))
        {
            network = string.IsNullOrEmpty(network) ? si.Satpos : $"{network} ({si.Satpos})";
        }

        var text = string.Join(" · ", new[] { network, si.Mux }.Where(p => !string.IsNullOrEmpty(p)));
        return text.Length > 0 ? text : null;
    }

    // The base URL Jellyfin's own ffmpeg uses to read this stream back. It reads its OWN endpoint, so
    // loopback is always correct and dodges Jellyfin auto-detecting a wrong NIC (the source of unplayable
    // streams on multi-homed hosts). An explicit override wins for split API/transcoder deployments.
    private static string LocalBaseUrl(IServerApplicationHost appHost)
    {
        var configured = Plugin.Instance?.Configuration.StreamBaseUrl;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.TrimEnd('/');
        }

        try
        {
            var uri = new Uri(appHost.GetApiUrlForLocalAccess());
            return $"{uri.Scheme}://127.0.0.1:{uri.Port}";
        }
        catch (UriFormatException)
        {
            return appHost.GetApiUrlForLocalAccess().TrimEnd('/');
        }
    }

    /// <inheritdoc/>
    public async Task Open(CancellationToken openCancellationToken)
    {
        // The direct-stream path does not call Open() for us, and it may be called more than once for a
        // shared stream — so start the pump exactly once and then wait for real bytes before returning.
        lock (_gate)
        {
            _lastReaderTicks = DateTime.UtcNow.Ticks;
            _pump ??= Task.Run(PumpAsync);
            _idleWatchdog ??= new Timer(_ => CheckIdle(), null, IdleTimeout, TimeSpan.FromSeconds(10));
        }

        await _firstByte.Task.WaitAsync(TimeSpan.FromSeconds(15), openCancellationToken).ConfigureAwait(false);

        // Probe our own muxed output once per channel for the fields HTSP can't report (interlacing, HDR,
        // bit depth, profile/level). Briefly extends the first tune of a channel; every later tune is cached.
        if (Interlocked.Exchange(ref _probeStarted, 1) == 0)
        {
            await ProbeAndMergeAsync(openCancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public Stream GetStream() => _ring.OpenReader();

    /// <inheritdoc/>
    public async Task Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1)
        {
            return;
        }

        EnableStreamSharing = false;
        _idleWatchdog?.Dispose();
        await _cts.CancelAsync().ConfigureAwait(false);
        await _client.UnsubscribeAsync(_subscription).ConfigureAwait(false);
        _ring.Dispose();
        _cts.Dispose();
        _logger.LogInformation("Closed HTSP live stream for channel {Channel}", ChannelId);
    }

    // Jellyfin does not reliably close what it opens: SessionManager only registers a live stream for
    // closing when a session reports playback START, so a PlaybackInfo call that opens a stream and never
    // plays it (the Android TV client does exactly one per tune) is never closed by any path — it just
    // pumps a Tvheadend subscription, and a 100 MB ring, until the server restarts. Shared streams leak the
    // same way: the mapping is keyed per session, so a second open on one session adds a consumer that no
    // close ever removes, and the count sticks above zero forever.
    //
    // So do not trust the consumer count — trust the bytes. Nobody holding a reader means nobody is
    // watching, whoever still thinks they are.
    private void CheckIdle()
    {
        if (Volatile.Read(ref _closed) == 1)
        {
            return;
        }

        if (_ring.ActiveReaders > 0)
        {
            Volatile.Write(ref _lastReaderTicks, DateTime.UtcNow.Ticks);
            return;
        }

        var idle = DateTime.UtcNow - new DateTime(Volatile.Read(ref _lastReaderTicks), DateTimeKind.Utc);
        if (idle < IdleTimeout)
        {
            return;
        }

        _logger.LogInformation(
            "Closing abandoned HTSP live stream for channel {Channel}: no consumer for {Seconds}s",
            ChannelId, (int)idle.TotalSeconds);
        _ = Close();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_closed == 0)
        {
            _ = Close();
        }
    }

    private async Task PumpAsync()
    {
        var writer = new ArrayBufferWriter<byte>(64 * 1024);
        try
        {
            _muxer.WriteHeaders(writer);
            await foreach (var packet in _subscription.Packets.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                // A key frame is a clean entry point. Flush what came before, mark the join offset, and
                // re-emit PAT/PMT right at it so a fresh reader gets tables + SPS/PPS + IDR in order.
                if (packet.IsKeyFrame)
                {
                    Flush(writer);
                    _ring.MarkSyncPoint();
                    _muxer.WriteHeaders(writer);
                }

                _muxer.WritePacket(packet, writer);
                Flush(writer);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal on close.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HTSP pump failed for channel {Channel}", ChannelId);
        }
        finally
        {
            _firstByte.TrySetResult(); // unblock Open() even if the stream never produced data
            _ring.Complete();
        }
    }

    private void Flush(ArrayBufferWriter<byte> writer)
    {
        if (writer.WrittenCount == 0)
        {
            return;
        }

        _ring.Write(writer.WrittenSpan);
        writer.ResetWrittenCount();
        _firstByte.TrySetResult();
    }

    // Read a few seconds of our own muxed TS back through ffprobe and merge the fields HTSP omits. We probe
    // a small temp file rather than the self-served URL because that URL isn't registered with the server
    // until Open() returns, and rather than a second Tvheadend subscription because a ring reader replays
    // bytes we already hold. Best-effort: on timeout or failure the stream still plays with HTSP metadata.
    private async Task ProbeAndMergeAsync(CancellationToken ct)
    {
        var probePath = Path.Combine(Path.GetTempPath(), "htsp-probe-" + UniqueId + ".ts");
        try
        {
            // ponytail: 6s ceiling on the one-time-per-channel probe. Promote to config if a slow tuner
            // makes the first tune drag; every subsequent tune of the channel is served from the cache.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));

            var captured = await CaptureForProbeAsync(probePath, timeout.Token).ConfigureAwait(false);

            var info = await _mediaEncoder.GetMediaInfo(
                new MediaInfoRequest
                {
                    MediaSource = new MediaSourceInfo { Protocol = MediaProtocol.File, Path = probePath, Container = "ts" },
                    MediaType = DlnaProfileType.Video,
                },
                timeout.Token).ConfigureAwait(false);

            if (info?.MediaStreams is { Count: > 0 } probed)
            {
                MediaStreamBuilder.MergeProbe(_streams, probed);
                RecomputeSourceBitrate();
                _probeCache[ChannelId] = probed;

                var video = _streams.Find(s => s.Type == MediaStreamType.Video);
                _logger.LogDebug(
                    "Probed channel {Channel} from {Bytes}B: interlaced={Interlaced} profile={Profile} bitDepth={BitDepth} transfer={Transfer}",
                    ChannelId, captured, video?.IsInterlaced, video?.Profile, video?.BitDepth, video?.ColorTransfer);
            }
        }
        catch (Exception ex)
        {
            // Non-fatal: playback proceeds on HTSP-derived metadata; the empty cache retries next tune.
            _logger.LogWarning(ex, "Metadata probe failed for channel {Channel}; using HTSP metadata only", ChannelId);
        }
        finally
        {
            try
            {
                File.Delete(probePath);
            }
            catch (IOException)
            {
                // best effort — a tiny temp file
            }
        }
    }

    private async Task<int> CaptureForProbeAsync(string path, CancellationToken ct)
    {
        // A couple of GOPs is enough for ffprobe to read the SPS/VPS and detect field order; the ring reader
        // starts at the last sync point (PAT/PMT + key frame), so the capture is self-contained.
        const int MaxBytes = 1_500_000;

        var total = 0;
        using var reader = _ring.OpenReader();
        var file = File.Create(path);
        await using (file.ConfigureAwait(false))
        {
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                while (total < MaxBytes)
                {
                    var read = await reader.ReadAsync(buffer, ct).ConfigureAwait(false);
                    if (read <= 0)
                    {
                        break;
                    }

                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    total += read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        return total;
    }

    private void RecomputeSourceBitrate()
    {
        long total = 0;
        foreach (var s in _streams)
        {
            if (s.Type == MediaStreamType.Video)
            {
                total += s.BitRate ?? 0;
            }
            else if (s.Type == MediaStreamType.Audio)
            {
                total += s.BitRate ?? 256_000; // a typical rate when the probe hasn't run yet
            }
        }

        MediaSource.Bitrate = total > 0 ? (int)total : null;
    }
}
