namespace HtspTuner.Htsp;

/// <summary>
/// Front-end signal quality for a tuned subscription, from the <c>signalStatus</c> event.
/// </summary>
/// <remarks>
/// Every field is optional: Tvheadend only reports what the underlying adapter exposes, so an IPTV
/// input reports almost nothing while a DVB-S input reports the full set.
/// </remarks>
internal sealed record HtspSignalStatus
{
    /// <summary>Gets the human-readable front-end lock state, such as <c>"OK"</c> or <c>"NONE"</c>.</summary>
    public string? Status { get; init; }

    /// <summary>Gets the signal-to-noise ratio, in the units named by <see cref="SnrScale"/>.</summary>
    public long? Snr { get; init; }

    /// <summary>Gets the signal strength, in the units named by <see cref="SignalScale"/>.</summary>
    public long? Signal { get; init; }

    /// <summary>Gets the bit error rate.</summary>
    public long? BitErrorRate { get; init; }

    /// <summary>Gets the count of uncorrected blocks.</summary>
    public long? UncorrectedBlocks { get; init; }

    /// <summary>Gets the scale of <see cref="Snr"/>: 0 unknown, 1 relative (0..65535), 2 decibels (milli-dB).</summary>
    public long? SnrScale { get; init; }

    /// <summary>Gets the scale of <see cref="Signal"/>: 0 unknown, 1 relative (0..65535), 2 decibels (milli-dBm).</summary>
    public long? SignalScale { get; init; }
}

/// <summary>
/// Server-side queue health for a subscription, from the <c>queueStatus</c> event.
/// </summary>
/// <remarks>
/// The drop counters are the ones that matter operationally: a rising <see cref="IdropCount"/> means
/// key frames are being lost and the picture will visibly break up.
/// </remarks>
internal sealed record HtspQueueStatus
{
    /// <summary>Gets the number of packets currently queued.</summary>
    public long Packets { get; init; }

    /// <summary>Gets the number of bytes currently queued.</summary>
    public long Bytes { get; init; }

    /// <summary>Gets the queue delay, in microseconds.</summary>
    public long Delay { get; init; }

    /// <summary>Gets the number of B frames dropped.</summary>
    public long BdropCount { get; init; }

    /// <summary>Gets the number of P frames dropped.</summary>
    public long PdropCount { get; init; }

    /// <summary>Gets the number of I (key) frames dropped.</summary>
    public long IdropCount { get; init; }
}

/// <summary>A streaming profile advertised by Tvheadend, from the <c>getProfiles</c> response.</summary>
/// <param name="Uuid">The profile UUID.</param>
/// <param name="Name">The profile name, as passed to <c>subscribe</c>.</param>
/// <param name="Comment">The optional human-readable comment.</param>
internal sealed record HtspProfile(string Uuid, string Name, string? Comment);

/// <summary>
/// A point-in-time view of one active subscription, for the configuration-page dashboard.
/// </summary>
internal sealed record HtspTunerSnapshot
{
    /// <summary>Gets the subscription id.</summary>
    public required int SubscriptionId { get; init; }

    /// <summary>Gets the channel id being watched.</summary>
    public required long ChannelId { get; init; }

    /// <summary>Gets the channel name, when known.</summary>
    public string? ChannelName { get; init; }

    /// <summary>Gets the adapter name the subscription is tuned on, from <c>sourceinfo</c>.</summary>
    public string? Adapter { get; init; }

    /// <summary>Gets the mux name, from <c>sourceinfo</c>.</summary>
    public string? Mux { get; init; }

    /// <summary>Gets the latest signal status, when the adapter reports one.</summary>
    public HtspSignalStatus? Signal { get; init; }

    /// <summary>Gets the latest queue status.</summary>
    public HtspQueueStatus? Queue { get; init; }

    /// <summary>Gets the latest human-readable subscription status message, if any.</summary>
    public string? Status { get; init; }

    /// <summary>Gets a value indicating whether the subscription has started streaming.</summary>
    public bool IsStarted { get; init; }

    /// <summary>Gets the UTC time the subscription was created.</summary>
    public DateTime StartedUtc { get; init; }
}
