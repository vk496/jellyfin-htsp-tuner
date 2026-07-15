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

    /// <summary>Gets or sets the HTTP port, used for channel icons and the HTTP fallback.</summary>
    public int HttpPort { get; set; } = 9981;

    /// <summary>Gets or sets a value indicating whether HTTP requests use TLS.</summary>
    public bool UseHttps { get; set; }

    /// <summary>Gets or sets the Tvheadend webroot, when it sits behind a reverse proxy.</summary>
    public string WebRoot { get; set; } = string.Empty;

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
    /// Gets or sets the base URL Jellyfin uses to read the plugin's own live stream
    /// (its <c>/LiveTv/LiveStreamFiles/</c> endpoint). Leave empty to use loopback
    /// <c>http://127.0.0.1:&lt;port&gt;</c>, which is correct for the normal single-host setup and avoids
    /// Jellyfin auto-detecting a wrong network interface. Only set this if Jellyfin's transcoder runs on a
    /// different host than its API (e.g. <c>http://transcoder.example:8096</c>).
    /// </summary>
    public string StreamBaseUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets default recording padding before a programme, in seconds.</summary>
    public int PrePaddingSeconds { get; set; }

    /// <summary>Gets or sets default recording padding after a programme, in seconds.</summary>
    public int PostPaddingSeconds { get; set; }

    /// <summary>Gets or sets the Tvheadend DVR configuration UUID used for new recordings.</summary>
    public string DvrConfig { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether to import Tvheadend channel tags as Jellyfin genres.</summary>
    public bool ImportChannelTags { get; set; } = true;

    /// <summary>
    /// Gets or sets how often, at most, to refresh Jellyfin's guide when Tvheadend pushes EPG changes,
    /// in minutes. Zero leaves it to Jellyfin's own schedule. EPG is streamed live over HTSP and held in
    /// an indexed cache, so a refresh is a cheap in-memory read (no per-channel network scan) — this can
    /// safely be small (e.g. 30) for a near-live guide.
    /// </summary>
    public int AutoGuideRefreshMinutes { get; set; }

    /// <summary>Gets or sets a value indicating whether the recordings channel is hidden.</summary>
    public bool HideRecordingsChannel { get; set; }
}
