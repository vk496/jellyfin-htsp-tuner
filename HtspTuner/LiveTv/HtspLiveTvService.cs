using System.Globalization;
using HtspTuner.Configuration;
using HtspTuner.Epg;
using HtspTuner.Htsp;
using MediaBrowser.Controller;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace HtspTuner.LiveTv;

/// <summary>
/// The Tvheadend HTSP live TV service: channels, EPG, native streaming, and DVR timers.
/// </summary>
public sealed class HtspLiveTvService : ILiveTvService, ISupportsDirectStreamProvider, ISupportsNewTimerIds, IAsyncDisposable
{
    private readonly IServerApplicationHost _appHost;
    private readonly IConfigurationManager _config;
    private readonly ITaskManager _taskManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<HtspLiveTvService> _logger;
    private readonly SemaphoreSlim _clientLock = new(1, 1);

    private HtspClient? _client;
    private string? _clientKey;
    private DateTime _lastAutoRefresh = DateTime.MinValue;

    /// <summary>Initializes a new instance of the <see cref="HtspLiveTvService"/> class.</summary>
    /// <param name="appHost">The application host.</param>
    /// <param name="config">The configuration manager, used to detect tuner-device mode.</param>
    /// <param name="taskManager">The task manager, to refresh the guide when EPG changes.</param>
    /// <param name="mediaEncoder">ffprobe wrapper, passed to each live stream for metadata probing.</param>
    /// <param name="logger">The logger.</param>
    public HtspLiveTvService(
        IServerApplicationHost appHost, IConfigurationManager config, ITaskManager taskManager,
        IMediaEncoder mediaEncoder, ILogger<HtspLiveTvService> logger)
    {
        _appHost = appHost;
        _config = config;
        _taskManager = taskManager;
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    // EPG arrives live over HTSP; when it changes, nudge Jellyfin to re-read our (cheap, indexed) cache so
    // the guide reflects it, throttled to AutoGuideRefreshMinutes. Zero leaves it to Jellyfin's schedule.
    private void OnEpgChanged()
    {
        var minutes = Config.AutoGuideRefreshMinutes;
        if (minutes <= 0 || (DateTime.UtcNow - _lastAutoRefresh).TotalMinutes < minutes)
        {
            return;
        }

        _lastAutoRefresh = DateTime.UtcNow;
        var task = _taskManager.ScheduledTasks
            .FirstOrDefault(t => string.Equals(t.ScheduledTask.Key, "RefreshGuide", StringComparison.Ordinal));
        if (task is not null)
        {
            _ = _taskManager.Execute(task, new TaskOptions());
        }
    }

    // When the user has added an HTSP tuner device, that path owns the channels; the integrated service
    // stands down so channels are not listed twice.
    private bool TunerConfigured =>
        _config.GetConfiguration<MediaBrowser.Model.LiveTv.LiveTvOptions>("livetv").TunerHosts
            .Any(t => string.Equals(t.Type, "htsp", StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc/>
    public string Name => "Tvheadend (HTSP)";

    /// <inheritdoc/>
    public string HomePageUrl => "https://tvheadend.org";

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    /// <inheritdoc/>
    public async Task<IEnumerable<ChannelInfo>> GetChannelsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var client = await GetClientAsync(cancellationToken).ConfigureAwait(false);
            var tags = client.GetTags().ToDictionary(t => t.Id, t => t.Name);
            return client.GetChannels()
                .OrderBy(c => c.Number == 0 ? int.MaxValue : c.Number)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Select(c => new ChannelInfo
                {
                    Id = c.Id.ToString(CultureInfo.InvariantCulture),
                    Name = c.Name,
                    Number = c.DisplayNumber,
                    ImageUrl = ResolveIcon(c.Icon),
                    ChannelType = c.IsRadio ? ChannelType.Radio : ChannelType.TV,
                    Tags = Config.ImportChannelTags
                        ? c.TagIds.Select(id => tags.GetValueOrDefault(id)).OfType<string>().ToArray()
                        : null,
                })
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetChannelsAsync failed");
            return Array.Empty<ChannelInfo>();
        }
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<ProgramInfo>> GetProgramsAsync(
        string channelId, DateTime startDateUtc, DateTime endDateUtc, CancellationToken cancellationToken)
    {
        try
        {
            var client = await GetClientAsync(cancellationToken).ConfigureAwait(false);
            var id = long.Parse(channelId, CultureInfo.InvariantCulture);
            return client.GetEvents(id, startDateUtc, endDateUtc).Select(e => EpgMapper.ToProgram(channelId, e)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetProgramsAsync failed for channel {Channel}", channelId);
            return Array.Empty<ProgramInfo>();
        }
    }

    /// <inheritdoc/>
    public async Task<ILiveStream> GetChannelStreamWithDirectStreamProvider(
        string channelId, string streamId, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
    {
        // Reuse an existing subscription when another client is already watching this channel.
        var shared = currentLiveStreams
            .OfType<HtspLiveStream>()
            .FirstOrDefault(s => s.EnableStreamSharing && s.IsAlive
                && string.Equals(s.OriginalStreamId, channelId, StringComparison.Ordinal));
        if (shared is not null)
        {
            shared.ConsumerCount++;
            _logger.LogInformation("Sharing channel {Channel}; consumers now {Count}", channelId, shared.ConsumerCount);
            return shared;
        }

        var client = await GetClientAsync(cancellationToken).ConfigureAwait(false);
        var id = long.Parse(channelId, CultureInfo.InvariantCulture);
        var subscription = await client.SubscribeAsync(id, cancellationToken).ConfigureAwait(false);

        var stream = new HtspLiveStream(
            client, subscription, _appHost, _mediaEncoder, (long)Config.MaxBufferMb * 1024 * 1024, _logger)
        {
            OriginalStreamId = channelId,
        };

        // The direct-stream path does not call Open() for us; do it now so probing sees real bytes.
        await stream.Open(cancellationToken).ConfigureAwait(false);
        return stream;
    }

    /// <inheritdoc/>
    public async Task<MediaSourceInfo> GetChannelStream(
        string channelId, string streamId, CancellationToken cancellationToken)
    {
        // HTTP fallback: hand Jellyfin Tvheadend's own stream URL, authorised with a ticket.
        var client = await GetClientAsync(cancellationToken).ConfigureAwait(false);
        var id = long.Parse(channelId, CultureInfo.InvariantCulture);
        var ticket = await client.GetTicketAsync(id, cancellationToken).ConfigureAwait(false);
        var cfg = Config;
        var scheme = cfg.UseHttps ? "https" : "http";
        var url = $"{scheme}://{cfg.Host}:{cfg.HttpPort}{ticket.Path}?ticket={ticket.Ticket}";
        if (!string.IsNullOrEmpty(cfg.Profile))
        {
            url += $"&profile={cfg.Profile}";
        }

        return new MediaSourceInfo
        {
            Id = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            Path = url,
            Protocol = MediaProtocol.Http,
            IsInfiniteStream = true,
            Container = "ts",
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            SupportsProbing = true,
            RequiresClosing = false,
        };
    }

    /// <inheritdoc/>
    public Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(
        string channelId, CancellationToken cancellationToken)
    {
        // A descriptor for the source list; the real stream table is filled in when the stream opens.
        var source = new MediaSourceInfo
        {
            Id = channelId,
            Path = string.Empty,
            Protocol = MediaProtocol.Http,
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
    public Task CloseLiveStream(string id, CancellationToken cancellationToken)
    {
        // The direct-stream path is torn down by the server via ILiveStream.Close(); nothing to do here.
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task ResetTuner(string id, CancellationToken cancellationToken)
    {
        await _clientLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client is not null)
            {
                await _client.DisposeAsync().ConfigureAwait(false);
                _client = null;
                _clientKey = null;
            }
        }
        finally
        {
            _clientLock.Release();
        }
    }

    // ---- Timers -------------------------------------------------------------

    /// <inheritdoc/>
    public async Task<IEnumerable<TimerInfo>> GetTimersAsync(CancellationToken cancellationToken)
    {
        var client = await GetClientAsync(cancellationToken).ConfigureAwait(false);
        return client.GetDvrEntries()
            .Where(d => d.IsScheduled || d.IsRecording)
            .Select(ToTimer)
            .ToList();
    }

    /// <inheritdoc/>
    public async Task<string> CreateTimer(TimerInfo info, CancellationToken cancellationToken)
    {
        var client = await GetClientAsync(cancellationToken).ConfigureAwait(false);
        var msg = new HtspMessage().Add("method", "addDvrEntry");
        if (info.ProgramId is { Length: > 0 } && long.TryParse(info.ProgramId, out var eventId))
        {
            msg.Add("eventId", eventId);
        }
        else
        {
            msg.Add("channelId", long.Parse(info.ChannelId, CultureInfo.InvariantCulture))
                .Add("start", ToUnix(info.StartDate))
                .Add("stop", ToUnix(info.EndDate))
                .AddIfPresent("title", info.Name);
        }

        msg.Add("startExtra", (long)Math.Round(info.PrePaddingSeconds / 60.0))
            .Add("stopExtra", (long)Math.Round(info.PostPaddingSeconds / 60.0));
        if (!string.IsNullOrEmpty(Config.DvrConfig))
        {
            msg.Add("configName", Config.DvrConfig);
        }

        var reply = await client.SendAsync(msg, cancellationToken).ConfigureAwait(false);
        return reply.GetString("id") ?? reply.GetInt("id").ToString(CultureInfo.InvariantCulture);
    }

    /// <inheritdoc/>
    public async Task CreateTimerAsync(TimerInfo info, CancellationToken cancellationToken)
        => await CreateTimer(info, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc/>
    public async Task CancelTimerAsync(string timerId, CancellationToken cancellationToken)
    {
        var client = await GetClientAsync(cancellationToken).ConfigureAwait(false);
        await client.SendAsync(
            new HtspMessage().Add("method", "cancelDvrEntry").Add("id", long.Parse(timerId, CultureInfo.InvariantCulture)),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task UpdateTimerAsync(TimerInfo updatedTimer, CancellationToken cancellationToken)
    {
        var client = await GetClientAsync(cancellationToken).ConfigureAwait(false);
        await client.SendAsync(
            new HtspMessage().Add("method", "updateDvrEntry")
                .Add("id", long.Parse(updatedTimer.Id, CultureInfo.InvariantCulture))
                .Add("startExtra", (long)Math.Round(updatedTimer.PrePaddingSeconds / 60.0))
                .Add("stopExtra", (long)Math.Round(updatedTimer.PostPaddingSeconds / 60.0)),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task<SeriesTimerInfo> GetNewTimerDefaultsAsync(CancellationToken cancellationToken, ProgramInfo? program = null)
        => Task.FromResult(new SeriesTimerInfo
        {
            // Keep pre/post the right way round — the previous plugin transposed them.
            PrePaddingSeconds = Config.PrePaddingSeconds,
            PostPaddingSeconds = Config.PostPaddingSeconds,
            RecordAnyChannel = false,
            RecordAnyTime = false,
            Days = new List<DayOfWeek>(),
        });

    // ---- Series timers (autorec) -------------------------------------------

    /// <inheritdoc/>
    public async Task<IEnumerable<SeriesTimerInfo>> GetSeriesTimersAsync(CancellationToken cancellationToken)
    {
        var client = await GetClientAsync(cancellationToken).ConfigureAwait(false);
        return client.GetAutorecs().Select(ToSeriesTimer).ToList();
    }

    /// <inheritdoc/>
    public async Task<string> CreateSeriesTimer(SeriesTimerInfo info, CancellationToken cancellationToken)
    {
        // This actually works — the previous plugin threw NotImplementedException here.
        var client = await GetClientAsync(cancellationToken).ConfigureAwait(false);
        var msg = new HtspMessage()
            .Add("method", "addAutorecEntry")
            .AddIfPresent("title", info.Name)
            .Add("startExtra", (long)Math.Round(info.PrePaddingSeconds / 60.0))
            .Add("stopExtra", (long)Math.Round(info.PostPaddingSeconds / 60.0))
            .Add("enabled", 1L);

        if (!info.RecordAnyChannel && !string.IsNullOrEmpty(info.ChannelId))
        {
            msg.Add("channelId", long.Parse(info.ChannelId, CultureInfo.InvariantCulture));
        }

        if (!info.RecordAnyTime)
        {
            msg.Add("start", (long)info.StartDate.ToLocalTime().TimeOfDay.TotalMinutes);
        }

        if (info.Days is { Count: > 0 })
        {
            msg.Add("daysOfWeek", DaysMask(info.Days));
        }

        var reply = await client.SendAsync(msg, cancellationToken).ConfigureAwait(false);
        return reply.GetString("id") ?? string.Empty;
    }

    /// <inheritdoc/>
    public async Task CreateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
        => await CreateSeriesTimer(info, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc/>
    public async Task UpdateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
    {
        // Actually update: replace the rule, rather than the old plugin's silent delete.
        await CancelSeriesTimerAsync(info.Id, cancellationToken).ConfigureAwait(false);
        await CreateSeriesTimer(info, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task CancelSeriesTimerAsync(string timerId, CancellationToken cancellationToken)
    {
        var client = await GetClientAsync(cancellationToken).ConfigureAwait(false);
        await client.SendAsync(
            new HtspMessage().Add("method", "deleteAutorecEntry").Add("id", timerId),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
        }

        _clientLock.Dispose();
    }

    // ---- Helpers ------------------------------------------------------------

    private async Task<HtspClient> GetClientAsync(CancellationToken cancellationToken)
    {
        var cfg = Config;

        // Stand down when unconfigured, or when an HTSP tuner device is in use (it owns the channels).
        // Bailing out here also avoids connection timeouts on every guide query.
        if (string.IsNullOrWhiteSpace(cfg.Host) || TunerConfigured)
        {
            throw new HtspServerException("HTSP integrated service is dormant (no host, or tuner-device mode).");
        }

        var key = string.Join('|', cfg.Host, cfg.HtspPort, cfg.Username, cfg.Password, cfg.Profile, cfg.SubscriptionWeight);

        await _clientLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client is { IsConnected: true } && _clientKey == key)
            {
                return _client;
            }

            if (_client is not null)
            {
                await _client.DisposeAsync().ConfigureAwait(false);
                _client = null;
            }

            var client = new HtspClient(ToOptions(cfg), _logger);
            client.EpgChanged += OnEpgChanged;
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            _client = client;
            _clientKey = key;
            return client;
        }
        finally
        {
            _clientLock.Release();
        }
    }

    private static HtspClientOptions ToOptions(PluginConfiguration cfg) => new()
    {
        Host = cfg.Host,
        Port = cfg.HtspPort,
        HttpPort = cfg.HttpPort,
        UseHttps = cfg.UseHttps,
        WebRoot = cfg.WebRoot,
        Username = cfg.Username,
        Password = cfg.Password,
        Profile = cfg.Profile,
        SubscriptionWeight = cfg.SubscriptionWeight,
    };

    private string? ResolveIcon(string? icon)
    {
        if (string.IsNullOrEmpty(icon))
        {
            return null;
        }

        if (icon.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || icon.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return icon;
        }

        var cfg = Config;
        var scheme = cfg.UseHttps ? "https" : "http";
        var root = cfg.WebRoot.Trim('/');
        var prefix = root.Length > 0 ? "/" + root : string.Empty;
        return $"{scheme}://{cfg.Host}:{cfg.HttpPort}{prefix}/{icon.TrimStart('/')}";
    }

    private TimerInfo ToTimer(HtspDvrEntry d) => new()
    {
        Id = d.Id.ToString(CultureInfo.InvariantCulture),
        ChannelId = d.ChannelId.ToString(CultureInfo.InvariantCulture),
        ProgramId = d.EventId?.ToString(CultureInfo.InvariantCulture),
        Name = d.Title ?? string.Empty,
        Overview = d.Description,
        StartDate = d.Start,
        EndDate = d.Stop,
        PrePaddingSeconds = d.PrePaddingSeconds,
        PostPaddingSeconds = d.PostPaddingSeconds,
        Status = d.IsRecording ? RecordingStatus.InProgress : RecordingStatus.New,
        SeriesTimerId = d.AutorecId,
    };

    private SeriesTimerInfo ToSeriesTimer(HtspAutorecEntry a) => new()
    {
        Id = a.Id,
        Name = a.Title ?? string.Empty,
        ChannelId = a.ChannelId > 0 ? a.ChannelId.ToString(CultureInfo.InvariantCulture) : null,
        RecordAnyChannel = a.ChannelId <= 0,
        RecordAnyTime = a.StartMinutes is null,
        PrePaddingSeconds = a.PrePaddingSeconds,
        PostPaddingSeconds = a.PostPaddingSeconds,
        Priority = a.Priority,
        Days = DaysFromMask(a.DaysOfWeek),
    };

    private static long DaysMask(IReadOnlyList<DayOfWeek> days)
    {
        long mask = 0;
        foreach (var d in days)
        {
            // Tvheadend bit 0 = Monday.
            var bit = ((int)d + 6) % 7;
            mask |= 1L << bit;
        }

        return mask;
    }

    private static List<DayOfWeek> DaysFromMask(int mask)
    {
        var days = new List<DayOfWeek>();
        for (var bit = 0; bit < 7; bit++)
        {
            if ((mask & (1 << bit)) != 0)
            {
                days.Add((DayOfWeek)((bit + 1) % 7)); // bit 0 = Monday -> DayOfWeek.Monday(1)
            }
        }

        return days;
    }

    private static long ToUnix(DateTime dt)
        => (long)(dt.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
}
