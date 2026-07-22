using MediaBrowser.Model.Plugins;

namespace HtspTuner.Configuration;

/// <summary>
/// Plugin settings. Defaults must be a valid configuration on their own.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets the Tvheadend host name or IP address.</summary>
    public string Host { get; set; } = "localhost";

    /// <summary>Gets or sets the HTSP port.</summary>
    public int HtspPort { get; set; } = 9982;

    /// <summary>Gets or sets the user name. Empty is valid: Tvheadend may allow anonymous access.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Gets or sets the password. Empty is valid.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Gets or sets the streaming profile passed to <c>subscribe</c>.</summary>
    public string Profile { get; set; } = "htsp";

    /// <summary>
    /// Gets or sets the HTSP subscription weight. Higher wins when tuners are contended.
    /// Tvheadend's own default for a live view is 100.
    /// </summary>
    public int SubscriptionWeight { get; set; } = 100;

    /// <summary>
    /// Gets or sets the maximum size, in megabytes, of the in-memory ring buffer that holds muxed TS
    /// for one live channel. When a viewer falls this far behind, the oldest data is dropped rather than
    /// blocking the tuner — it is live TV. Bounds memory so a stalled client cannot exhaust the host.
    /// </summary>
    public int MaxBufferMb { get; set; } = 100;

    /// <summary>
    /// Gets or sets how long, in milliseconds, ffmpeg analyses the stream before it starts playing it
    /// (its <c>-analyzeduration</c>). Lower means a faster start; too low risks a mis-probe on an unusual
    /// stream.
    /// </summary>
    /// <remarks>
    /// Advanced. The default 2000 ms is already far below ffmpeg's 200-second default — that default made a
    /// no-EOF live stream take minutes to start, which is why it is capped here. We can afford a low value
    /// because Jellyfin never actually probes our stream (we hand it the full stream table) and the video is
    /// remuxed, not re-encoded; this budget only covers ffmpeg confirming timestamps. Dropping it toward
    /// ~1000 shaves about a second off the start; it is only part of the delay, most of which is Jellyfin's
    /// own HLS segmenting, which this cannot touch. Clamped to 250..10000.
    /// </remarks>
    public int AnalyzeDurationMs { get; set; } = 2000;

    /// <summary>
    /// Gets or sets the base URL Jellyfin uses to read the plugin's own live stream
    /// (its <c>/LiveTv/LiveStreamFiles/</c> endpoint). Leave empty to use loopback
    /// <c>http://127.0.0.1:&lt;port&gt;</c>, which is correct for the normal single-host setup and avoids
    /// Jellyfin auto-detecting a wrong network interface. Only set this if Jellyfin's transcoder runs on a
    /// different host than its API (e.g. <c>http://transcoder.example:8096</c>).
    /// </summary>
    public string StreamBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether Tvheadend's channel tags are copied onto the Jellyfin
    /// channels. They land on <c>LiveTvChannel.Tags</c> — Jellyfin has no genre field for channels.
    /// </summary>
    public bool ImportChannelTags { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether programmes with no artwork get a still frame captured from
    /// the live broadcast, so the Live TV home page shows pictures instead of blank placeholder tiles.
    /// </summary>
    /// <remarks>
    /// Only what is on air right now is considered, a few channels per minute, and a channel already being
    /// watched is sampled from the stream we are holding rather than tuned again. Anything that does have to
    /// tune subscribes at the lowest weight Tvheadend accepts, so a viewer always wins a contended tuner.
    /// </remarks>
    public bool CaptureProgramImages { get; set; } = true;

    /// <summary>
    /// Gets or sets how many airing programmes each scan considers, newest start date first. Zero means
    /// every programme currently on air.
    /// </summary>
    /// <remarks>
    /// The default matches the pool Jellyfin's Live TV home page draws its "On Now" row from, so this covers
    /// what is on screen and nothing else. It is also what keeps the feature proportional to a large
    /// Tvheadend: a server can carry thousands of channels, and a still frame is only worth taking for
    /// programmes somebody is going to look at. Raise it to reach further down the guide.
    /// </remarks>
    public int ProgramImageCandidates { get; set; } = 200;

    /// <summary>Gets or sets how often, in seconds, the plugin looks for programmes that need a picture.</summary>
    /// <remarks>
    /// This is a scan interval, not a capture rate: a scan only ever takes a few frames, and one that finds
    /// nothing missing costs a single database query. Clamped to 15..3600.
    /// </remarks>
    public int ProgramImageScanSeconds { get; set; } = 61;

    /// <summary>
    /// Gets or sets the width of the channel logo stamped into a captured frame, as a percentage of the
    /// frame width. Zero leaves the frame unbranded.
    /// </summary>
    /// <remarks>
    /// A percentage rather than a pixel size because channels are not all the same resolution: one setting
    /// has to look the same on an SD, an HD and a UHD capture sitting next to each other in the same row.
    /// The logo goes in the bottom-right corner, where broadcasters' own on-screen graphics rarely sit.
    /// </remarks>
    public int ProgramImageLogoPercent { get; set; } = 22;

    /// <summary>
    /// Gets or sets the gap between the channel logo and the frame edge, as a percentage of the frame width.
    /// </summary>
    public int ProgramImageLogoMarginPercent { get; set; } = 3;

    /// <summary>
    /// Gets or sets the opacity of the drop shadow behind the channel logo, as a percentage. Zero removes it.
    /// </summary>
    /// <remarks>
    /// Worth keeping: a logo is dropped onto whatever the broadcast happened to be showing, so without a
    /// shadow a dark or busy frame swallows its edges. The blur and offset scale with the logo, so they need
    /// no settings of their own.
    /// </remarks>
    public int ProgramImageLogoShadowPercent { get; set; } = 75;

    /// <summary>
    /// Gets or sets the minimum age, in minutes, before a Tvheadend EPG push may trigger a Jellyfin guide
    /// refresh. Zero disables it entirely, leaving the guide to Jellyfin's own schedule.
    /// </summary>
    /// <remarks>
    /// Advanced. Reading our EPG is a cheap in-memory lookup, but the refresh itself is NOT cheap and none
    /// of it is ours: Jellyfin's <c>GuideManager.RefreshGuide</c> is all-or-nothing (<c>IGuideManager</c>
    /// exposes no per-channel entry point, so the delta Tvheadend hands us cannot be applied on its own),
    /// and for every channel it queries the database for that channel's programmes, diffs them, writes the
    /// changes back, and pre-caches artwork. On a few hundred channels that is many minutes of work.
    /// So this is a rate limit, not a poll interval, and the default is deliberately long: it exists to
    /// catch a guide that has gone stale, not to keep one live. Setting it near the duration of a refresh
    /// would queue refreshes back-to-back forever.
    /// </remarks>
    public int AutoGuideRefreshMinutes { get; set; } = 720;
}
