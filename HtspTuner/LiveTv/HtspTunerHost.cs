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
public sealed class HtspTunerHost : ITunerHost, IConfigurableTunerHost
{
    private const string Prefix = "htsp_";

    private readonly IConfigurationManager _config;
    private readonly IServerApplicationHost _appHost;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<HtspTunerHost> _logger;
    private readonly ConcurrentDictionary<string, HtspClient> _clients = new();

    /// <summary>Initializes a new instance of the <see cref="HtspTunerHost"/> class.</summary>
    /// <param name="config">The configuration manager, used to read configured tuners.</param>
    /// <param name="appHost">The application host.</param>
    /// <param name="mediaEncoder">ffprobe wrapper, passed to each live stream for metadata probing.</param>
    /// <param name="logger">The logger.</param>
    public HtspTunerHost(
        IConfigurationManager config, IServerApplicationHost appHost, IMediaEncoder mediaEncoder, ILogger<HtspTunerHost> logger)
    {
        _config = config;
        _appHost = appHost;
        _mediaEncoder = mediaEncoder;
        _logger = logger;
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
        return client.GetEvents(tvhChannelId, startDateUtc, endDateUtc)
            .Select(e => Epg.EpgMapper.ToProgram(channelId, e))
            .ToList();
    }

    /// <summary>Gets a value indicating whether any HTSP tuner is configured.</summary>
    public bool HasTuners => Tuners().Any();

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

    private void EnsureGuideProvider()
    {
        try
        {
            var options = _config.GetConfiguration<LiveTvOptions>("livetv");
            var providers = options.ListingProviders?.ToList() ?? new List<ListingsProviderInfo>();
            if (providers.Any(p => string.Equals(p.Type, Type, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            providers.Add(new ListingsProviderInfo
            {
                Id = Guid.NewGuid().ToString("N"),
                Type = Type, // "htsp"
                EnableAllTuners = true,
            });
            options.ListingProviders = providers.ToArray();
            _config.SaveConfiguration("livetv", options);
            _logger.LogInformation("Auto-registered the HTSP guide provider for all HTSP tuners");
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
        var opts = client.Options;
        var key = StableKey(opts);
        return client.GetChannels()
            .OrderBy(c => c.Number == 0 ? int.MaxValue : c.Number)
            .Select(c => new ChannelInfo
            {
                Id = Prefix + key + "_" + c.Id.ToString(CultureInfo.InvariantCulture),
                TunerHostId = tuner.Id,
                Name = c.Name,
                Number = c.DisplayNumber,
                ImageUrl = ResolveIcon(opts, c.Icon),
                ChannelType = c.IsRadio ? ChannelType.Radio : ChannelType.TV,
            })
            .ToList();
    }

    // A stable per-server key so channel ids survive removing and re-adding a tuner. Jellyfin assigns a
    // fresh TunerHostInfo.Id (GUID) every time a tuner is added, and the old scheme baked that into the
    // channel id -- so each re-add produced a whole new set of channels and the old ones piled up as
    // orphans. Deriving the key from the resolved host:port instead means the same Tvheadend always yields
    // the same channel ids, while different servers stay distinct.
    private static string StableKey(HtspClientOptions opts)
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
        if (_clients.TryGetValue(key, out var existing) && existing.IsConnected)
        {
            return existing;
        }

        var client = new HtspClient(opts, _logger);
        await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        _clients[key] = client;
        return client;
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
            HttpPort = cfg.HttpPort,
            UseHttps = cfg.UseHttps,
            WebRoot = cfg.WebRoot,
            Username = user,
            Password = pass,
            Profile = cfg.Profile,
            SubscriptionWeight = cfg.SubscriptionWeight,
        };
    }

    private static string? ResolveIcon(HtspClientOptions opts, string? icon)
    {
        if (string.IsNullOrEmpty(icon))
        {
            return null;
        }

        if (icon.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return icon;
        }

        var scheme = opts.UseHttps ? "https" : "http";
        var root = opts.WebRoot.Trim('/');
        var prefix = root.Length > 0 ? "/" + root : string.Empty;
        return $"{scheme}://{opts.Host}:{opts.HttpPort}{prefix}/{icon.TrimStart('/')}";
    }
}
