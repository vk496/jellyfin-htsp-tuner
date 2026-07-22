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
    /// Gets or sets how many of the Live TV home page's programmes each scan covers. Zero means all of them.
    /// </summary>
    /// <remarks>
    /// The list comes from Jellyfin itself -- the same call the home page makes, per user -- rather than an
    /// approximation of it, so this is a count of what is genuinely on screen and not of channels in
    /// general. That is what keeps the feature proportional to a large Tvheadend: the page shows tens of
    /// programmes whatever the lineup, and a still frame is only worth taking for one somebody will see.
    /// </remarks>
    public int ProgramImageCandidates { get; set; } = 60;

    /// <summary>
    /// Gets or sets how often, in seconds, the plugin looks for programmes that need a picture. Zero turns
    /// automatic sweeps off, leaving only the button on the settings page.
    /// </summary>
    /// <remarks>
    /// This is a scan interval, not a capture rate, and it is the gap between sweeps rather than a rate to
    /// keep up with -- a sweep that runs long is never followed immediately by another. A sweep that finds
    /// nothing missing costs a single database query. The default is deliberately unhurried: nothing here is
    /// urgent, and the tuners are better spent on whoever is actually watching. Clamped to 15..3600.
    /// </remarks>
    public int ProgramImageScanSeconds { get; set; } = 181;

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
    /// Gets or sets how black the disc drawn behind the channel logo is, as a percentage. Zero draws the
    /// logo bare.
    /// </summary>
    /// <remarks>
    /// A still from a live broadcast can be anything at all behind the corner where the logo goes, so the
    /// logo needs something to sit on rather than just an outline. The disc is solid out to the logo's own
    /// corner and fades to nothing beyond it.
    /// </remarks>
    public int ProgramImageLogoBackdropPercent { get; set; } = 85;

    /// <summary>
    /// Gets or sets how far the disc behind the logo fades out past the logo's corner, as a percentage of
    /// the solid part. 100 is a hard-edged disc.
    /// </summary>
    public int ProgramImageLogoBackdropSpread { get; set; } = 125;

    /// <summary>
    /// Gets or sets how long, in minutes, a channel that failed to produce a frame is skipped for. Zero
    /// retries it on every scan.
    /// </summary>
    /// <remarks>
    /// Parking stops a sweep spending its whole budget on the same dead channels, which matters on a lineup
    /// where most channels cannot be tuned. It costs the opposite case: a channel that was only briefly
    /// unavailable -- the tuners busy with a guide refresh, a moment of bad signal -- stays blank for the
    /// whole park. Zero by default, because a retry only costs one short tune.
    /// </remarks>
    public int ProgramImageParkMinutes { get; set; }

    /// <summary>
    /// Gets or sets how many seconds a background frame grab waits for a channel to start before giving up.
    /// </summary>
    /// <remarks>
    /// Watching a channel gets Tvheadend's full patience -- 20 seconds, extended further while it reports it
    /// is still tuning -- because somebody is waiting for it. A thumbnail is not worth that: on a lineup
    /// where many channels cannot currently be tuned, the failures rather than the successes are what a
    /// sweep spends its time on.
    /// </remarks>
    /// <remarks>
    /// How long a tune legitimately takes is a property of the setup, not a constant. Moving to a new
    /// multiplex is the slow part -- and a network that reaches its muxes through a pipe rather than straight
    /// off an RF input has a process to start before anything can lock, which pure RF does not. Landing on a
    /// multiplex already tuned is fast by comparison. The default allows for the slow case, since the fast
    /// one returns long before the budget matters. Clamped to 2..60.
    /// </remarks>
    public int ProgramImageTuneSeconds { get; set; } = 20;

    /// <summary>
    /// Gets or sets a value indicating whether Live TV artwork belonging to programmes that no longer exist
    /// is deleted from disk.
    /// </summary>
    /// <remarks>
    /// Jellyfin does not do this itself: when a programme ages out of the guide it drops the database row
    /// but leaves the artwork folder, because it only clears those for items that came from a file. The
    /// pictures therefore accumulate for as long as the server has had Live TV, and capturing frames adds
    /// one per programme per airing. Only folders directly under the Live TV metadata directory, named
    /// exactly as an item id, whose item is gone and which have not been touched for six hours are removed.
    /// </remarks>
    public bool CleanOrphanedProgramImages { get; set; } = true;

    /// <summary>
    /// Gets or sets how often, in minutes, the picture for a programme on a channel somebody is watching is
    /// taken again. Zero only ever captures a programme that has no picture at all.
    /// </summary>
    /// <remarks>
    /// A channel already being streamed is free to sample: the bytes are in memory, no tuner is touched and
    /// nothing can be preempted. And a single still is a poor summary of an hour-long programme -- the frame
    /// caught in the opening seconds is usually the least representative one there is. Each new picture
    /// replaces the last, so this does not accumulate on disk. Only pictures the plugin took itself are
    /// replaced: a programme the broadcaster supplied artwork for keeps it.
    /// </remarks>
    public int WatchedRefreshMinutes { get; set; } = 3;

    /// <summary>
    /// Gets or sets a value indicating whether captures that would need to tune a channel are deferred while
    /// somebody is watching. Captures from streams already open are unaffected either way.
    /// </summary>
    /// <remarks>
    /// Off by default: a server with tuners to spare has no reason to leave them idle, and subscription
    /// weight already guarantees the thing that matters -- a capture holds its tuner at the lowest weight
    /// Tvheadend accepts, so a viewer starting a channel takes it back rather than waiting.
    /// </remarks>
    /// <remarks>
    /// Turn it on if captures appear to disturb playback. Weight covers the tuner itself but not everything
    /// around it: on a satellite install the tuners share an LNB, so moving one to another transponder means
    /// DiSEqC traffic and a polarisation or band change on the shared cable, and each capture runs two
    /// ffmpeg processes that compete with whatever is transcoding. Neither shows up as a lost subscription;
    /// they show up as somebody's picture stuttering.
    /// </remarks>
    public bool PauseCapturesWhileWatching { get; set; }

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
