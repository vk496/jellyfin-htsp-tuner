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
