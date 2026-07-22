using System.ComponentModel.DataAnnotations;
using HtspTuner.Htsp;
using HtspTuner.LiveTv;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace HtspTuner.Api;

/// <summary>The body of a connection test: the fields needed to reach a Tvheadend server.</summary>
public class TestConnectionRequest
{
    /// <summary>Gets or sets the host name or IP.</summary>
    [Required]
    public string Host { get; set; } = string.Empty;

    /// <summary>Gets or sets the HTSP port.</summary>
    public int HtspPort { get; set; } = 9982;

    /// <summary>Gets or sets the user name (empty for anonymous).</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Gets or sets the password.</summary>
    public string Password { get; set; } = string.Empty;
}

/// <summary>The result of a connection test.</summary>
public class TestConnectionResult
{
    /// <summary>Gets or sets a value indicating whether the connection succeeded.</summary>
    public bool Ok { get; set; }

    /// <summary>Gets or sets a human-readable message describing the outcome.</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>The outcome of asking for a capture sweep.</summary>
public class CaptureNowResult
{
    /// <summary>Gets or sets a value indicating whether a sweep was started, as opposed to one already running.</summary>
    public bool Started { get; set; }

    /// <summary>Gets or sets a human-readable message describing the outcome.</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Plugin REST API: a connection test the config page can call, so failures show in the UI instead of
/// the server log.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("HtspTuner")]
public class HtspTunerController : ControllerBase
{
    private readonly ILogger<HtspTunerController> _logger;
    private readonly HtspTunerHost _tunerHost;
    private readonly ProgramImageService _programImages;

    /// <summary>Initializes a new instance of the <see cref="HtspTunerController"/> class.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="tunerHost">The tuner host, used to fetch channel icons over HTSP.</param>
    /// <param name="programImages">The programme-image sweeper, so the settings page can run one on demand.</param>
    public HtspTunerController(
        ILogger<HtspTunerController> logger, HtspTunerHost tunerHost, ProgramImageService programImages)
    {
        _logger = logger;
        _tunerHost = tunerHost;
        _programImages = programImages;
    }

    /// <summary>Runs a programme-image sweep now.</summary>
    /// <remarks>
    /// Captures are paced so as not to disturb anybody watching, which makes them slow by design. This is the
    /// way to say "do it now, the TV is free" -- and, because it reports what happened, a way to find out
    /// whether the feature is behind a playback problem without waiting for the next sweep.
    /// </remarks>
    /// <returns>Whether a sweep was started.</returns>
    /// <response code="200">The request was handled; the body says whether a sweep was started.</response>
    [HttpPost("CaptureImages")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<CaptureNowResult> CaptureImagesNow()
    {
        if (Plugin.Instance?.Configuration.CaptureProgramImages != true)
        {
            return new CaptureNowResult { Message = "Programme image capture is switched off." };
        }

        var started = _programImages.TryStartSweep();
        if (started)
        {
            _logger.LogInformation("Programme image sweep requested from the settings page");
        }

        return new CaptureNowResult
        {
            Started = started,
            Message = started
                ? "Sweep started. It runs for a few minutes; the server log reports what it captured."
                : "A sweep is already running.",
        };
    }

    /// <summary>
    /// Serves a channel icon fetched from Tvheadend over HTSP (so the plugin needs no HTTP access to
    /// Tvheadend). Jellyfin's image downloader calls this server-side, then caches the result.
    /// </summary>
    /// <param name="channelId">The prefixed channel id.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The icon image, or 404 if the channel has none.</returns>
    [HttpGet("Icon/{channelId}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> GetIcon(string channelId, CancellationToken cancellationToken)
    {
        var icon = await _tunerHost.GetChannelIconAsync(channelId, cancellationToken).ConfigureAwait(false);
        return icon is { } i ? File(i.Data, i.ContentType) : NotFound();
    }

    /// <summary>
    /// Serves an EPG artwork image fetched from Tvheadend over HTSP. Tvheadend reports programme images
    /// as <c>imagecache/&lt;id&gt;</c> paths rather than URLs, which Jellyfin cannot fetch on its own, so we
    /// hand it a URL pointing here and pull the bytes over the HTSP connection we already hold.
    /// </summary>
    /// <remarks>
    /// <paramref name="id"/> is an image-cache id, NOT a path: the route only accepts digits, and the path
    /// sent to Tvheadend is rebuilt here as <c>imagecache/{id}</c>. That is deliberate — <c>fileOpen</c>
    /// takes an arbitrary server-side path, so letting a caller supply one would turn this into a file-read
    /// proxy for the whole Tvheadend host.
    /// Anonymous like <see cref="GetIcon"/>: Jellyfin's image downloader fetches it server-side without
    /// credentials, and it only ever exposes EPG artwork.
    /// </remarks>
    /// <param name="serverKey">The stable per-server key; an image-cache id only means something on the
    /// Tvheadend that issued it, and several servers can be configured.</param>
    /// <param name="id">The Tvheadend image-cache id.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The image, or 404 if Tvheadend has no such image.</returns>
    [HttpGet("Image/{serverKey}/{id:long}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> GetImage(
        [FromRoute, Required] string serverKey, [FromRoute, Required] long id, CancellationToken cancellationToken)
    {
        var image = await _tunerHost.GetEpgImageAsync(serverKey, id, cancellationToken).ConfigureAwait(false);
        if (image is not { } i)
        {
            return NotFound();
        }

        // An imagecache id is immutable in Tvheadend, so this is safe to cache hard. Jellyfin copies the
        // image to local storage on first fetch and only pre-caches NEW programmes, so it should not come
        // back for the same id -- but several programmes can share one image, and this makes that cheap.
        Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        return File(i.Data, i.ContentType);
    }

    /// <summary>
    /// Lists every live HTSP subscription with its signal and queue health.
    /// </summary>
    /// <remarks>
    /// Reads cached state that Tvheadend already pushed, so it costs nothing to poll. Elevated (unlike the
    /// image endpoints) because it reveals what is being watched right now.
    /// </remarks>
    /// <returns>One entry per active subscription.</returns>
    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<HtspTunerSnapshot>> GetStatus() => Ok(_tunerHost.GetTunerStatus());

    /// <summary>Lists the streaming profiles the configured Tvheadend grants.</summary>
    /// <remarks>Suggestions for the config page; empty when no tuner is set up or the server is unreachable.</remarks>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The available profiles.</returns>
    [HttpGet("Profiles")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<HtspProfile>>> GetProfiles(CancellationToken cancellationToken)
        => Ok(await _tunerHost.GetProfilesAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>Tests a Tvheadend connection with the given credentials.</summary>
    /// <param name="request">The connection details.</param>
    /// <returns>Whether it connected, and a message.</returns>
    [HttpPost("TestConnection")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<TestConnectionResult>> TestConnection([FromBody] TestConnectionRequest request)
    {
        var options = new HtspClientOptions
        {
            Host = request.Host,
            Port = request.HtspPort,
            Username = request.Username,
            Password = request.Password,
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var client = new HtspClient(options, _logger);
        try
        {
            await client.ConnectOnlyAsync(cts.Token).ConfigureAwait(false);
            return new TestConnectionResult { Ok = true, Message = $"Connected to {request.Host}:{request.HtspPort}." };
        }
        catch (OperationCanceledException)
        {
            return new TestConnectionResult { Ok = false, Message = $"Timed out reaching {request.Host}:{request.HtspPort}." };
        }
        catch (Exception ex)
        {
            return new TestConnectionResult { Ok = false, Message = ex.Message };
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }
}
