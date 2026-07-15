namespace HtspTuner.Htsp;

/// <summary>
/// A codec carried by an HTSP subscription, as named by Tvheadend's <c>streaming_component_type2txt</c>.
/// </summary>
internal enum HtspCodec
{
    /// <summary>Unrecognised; the stream is dropped.</summary>
    Unknown = 0,

    /// <summary>H.264 / AVC, Annex-B framed.</summary>
    H264,

    /// <summary>H.265 / HEVC, Annex-B framed.</summary>
    Hevc,

    /// <summary>MPEG-2 video.</summary>
    Mpeg2Video,

    /// <summary>MPEG-1/2 layer I-III audio.</summary>
    Mpeg2Audio,

    /// <summary>AAC, raw frames; needs ADTS framing to enter MPEG-TS.</summary>
    Aac,

    /// <summary>AAC LATM.</summary>
    AacLatm,

    /// <summary>Dolby Digital.</summary>
    Ac3,

    /// <summary>Dolby Digital Plus.</summary>
    Eac3,

    /// <summary>Vorbis.</summary>
    Vorbis,

    /// <summary>DVB subtitles.</summary>
    DvbSub,

    /// <summary>DVB teletext.</summary>
    Teletext,

    /// <summary>Tvheadend's synthesised plain-text subtitle track. Has no MPEG-TS representation.</summary>
    TextSub,

    /// <summary>A PCR-only pseudo-stream. Carries no payload.</summary>
    Pcr,
}

/// <summary>
/// One elementary stream, from <c>subscriptionStart.streams[]</c>.
/// </summary>
internal sealed record HtspStream
{
    /// <summary>Gets the Tvheadend stream index. Referenced by <c>muxpkt.stream</c>.</summary>
    public required int Index { get; init; }

    /// <summary>Gets the codec.</summary>
    public required HtspCodec Codec { get; init; }

    /// <summary>Gets the raw Tvheadend type string, for logging and unknown codecs.</summary>
    public required string RawType { get; init; }

    /// <summary>Gets the ISO-639 language, if any.</summary>
    public string? Language { get; init; }

    /// <summary>Gets the video width in pixels.</summary>
    public int? Width { get; init; }

    /// <summary>Gets the video height in pixels.</summary>
    public int? Height { get; init; }

    /// <summary>Gets the frame duration. In 90 kHz ticks when the subscription requested 90 kHz.</summary>
    public int? Duration { get; init; }

    /// <summary>Gets the display aspect ratio numerator.</summary>
    public int? AspectNum { get; init; }

    /// <summary>Gets the display aspect ratio denominator.</summary>
    public int? AspectDen { get; init; }

    /// <summary>Gets the audio channel count.</summary>
    public int? Channels { get; init; }

    /// <summary>
    /// Gets the audio sample rate, in Hz, resolved from Tvheadend's SRI index.
    /// </summary>
    /// <remarks>
    /// The wire field <c>rate</c> is an <em>index</em> into the MPEG-4 sampling-frequency table, not a
    /// frequency. <c>rate: 3</c> means 48000 Hz, not 3 Hz.
    /// </remarks>
    public int? SampleRate { get; init; }

    /// <summary>Gets the DVB subtitle composition page id.</summary>
    public int? CompositionId { get; init; }

    /// <summary>Gets the DVB subtitle ancillary page id.</summary>
    public int? AncillaryId { get; init; }

    /// <summary>Gets the audio type: 0 normal, 1 clean effects, 2 hearing impaired, 3 visually impaired.</summary>
    public int AudioType { get; init; }

    /// <summary>Gets a value indicating whether this stream carries decodable media.</summary>
    public bool IsMedia => Codec is not (HtspCodec.Unknown or HtspCodec.Pcr or HtspCodec.TextSub);

    /// <summary>Gets a value indicating whether this is a video stream.</summary>
    public bool IsVideo => Codec is HtspCodec.H264 or HtspCodec.Hevc or HtspCodec.Mpeg2Video;

    /// <summary>Gets a value indicating whether this is an audio stream.</summary>
    public bool IsAudio => Codec is HtspCodec.Mpeg2Audio or HtspCodec.Aac or HtspCodec.AacLatm
        or HtspCodec.Ac3 or HtspCodec.Eac3 or HtspCodec.Vorbis;

    /// <summary>Gets a value indicating whether this is a subtitle stream.</summary>
    public bool IsSubtitle => Codec is HtspCodec.DvbSub or HtspCodec.Teletext or HtspCodec.TextSub;
}

/// <summary>
/// Where a subscription is being sourced from, from <c>subscriptionStart.sourceinfo</c>.
/// </summary>
/// <remarks>
/// <see cref="MuxUuid"/> is what makes fast channel changing possible: two channels sharing a mux
/// share an already-tuned adapter, so switching between them need not retune.
/// </remarks>
internal sealed record HtspSourceInfo
{
    /// <summary>Gets the adapter UUID.</summary>
    public string? AdapterUuid { get; init; }

    /// <summary>Gets the mux UUID.</summary>
    public string? MuxUuid { get; init; }

    /// <summary>Gets the network UUID.</summary>
    public string? NetworkUuid { get; init; }

    /// <summary>Gets the human-readable adapter name.</summary>
    public string? Adapter { get; init; }

    /// <summary>Gets the human-readable mux name.</summary>
    public string? Mux { get; init; }

    /// <summary>Gets the network name.</summary>
    public string? Network { get; init; }

    /// <summary>Gets the service provider.</summary>
    public string? Provider { get; init; }

    /// <summary>Gets the service name.</summary>
    public string? Service { get; init; }
}

/// <summary>
/// The <c>subscriptionStart</c> event: the full stream table for a subscription.
/// </summary>
internal sealed record HtspSubscriptionStart
{
    /// <summary>Gets the subscription id.</summary>
    public required int SubscriptionId { get; init; }

    /// <summary>Gets the elementary streams.</summary>
    public required IReadOnlyList<HtspStream> Streams { get; init; }

    /// <summary>Gets the source information.</summary>
    public HtspSourceInfo? SourceInfo { get; init; }

    /// <summary>
    /// Gets the global codec headers, when the service supplies them.
    /// </summary>
    /// <remarks>
    /// <c>meta</c> is a <em>top-level</em> field, not a per-stream one, and Tvheadend emits one per
    /// component that has global headers — so a service with both H264 and AAC emits <c>meta</c>
    /// twice in the same map, with no stream index to disambiguate them. It is therefore only
    /// usable when there is exactly one.
    /// </remarks>
    public IReadOnlyList<byte[]> Meta { get; init; } = Array.Empty<byte[]>();
}

/// <summary>
/// A single elementary-stream packet, from <c>muxpkt</c>.
/// </summary>
internal sealed record HtspMuxPacket
{
    /// <summary>Gets the Tvheadend stream index this packet belongs to.</summary>
    public required int StreamIndex { get; init; }

    /// <summary>Gets the elementary stream payload.</summary>
    public required byte[] Payload { get; init; }

    /// <summary>Gets the presentation timestamp, in 90 kHz ticks. Null when absent.</summary>
    public long? Pts { get; init; }

    /// <summary>Gets the decode timestamp, in 90 kHz ticks. Null when absent.</summary>
    public long? Dts { get; init; }

    /// <summary>Gets the frame duration, in 90 kHz ticks.</summary>
    public long Duration { get; init; }

    /// <summary>
    /// Gets the frame type: 'I', 'P' or 'B'. Null for every non-video packet.
    /// </summary>
    /// <remarks>Tvheadend only emits <c>frametype</c> for video, and sends it as an ASCII code point.</remarks>
    public char? FrameType { get; init; }

    /// <summary>Gets a value indicating whether this is a video key frame.</summary>
    public bool IsKeyFrame => FrameType == 'I';
}

/// <summary>Why a subscription stopped or failed to start.</summary>
/// <param name="Status">Tvheadend's human-readable, localised message.</param>
/// <param name="Error">The stable machine-readable token, such as <c>noFreeAdapter</c>.</param>
internal sealed record HtspSubscriptionError(string? Status, string? Error)
{
    /// <summary>Gets a message fit to show a user.</summary>
    public string Message => Status ?? Error ?? "Tvheadend subscription failed";
}

/// <summary>A Tvheadend channel, from <c>channelAdd</c> / <c>channelUpdate</c>.</summary>
internal sealed record HtspChannel
{
    /// <summary>Gets the numeric channel id.</summary>
    public required long Id { get; init; }

    /// <summary>Gets the channel name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the channel number. Zero when Tvheadend has not assigned one.</summary>
    public int Number { get; init; }

    /// <summary>Gets the channel minor number.</summary>
    public int NumberMinor { get; init; }

    /// <summary>Gets the raw icon reference, typically <c>imagecache/&lt;id&gt;</c> or an absolute URL.</summary>
    public string? Icon { get; init; }

    /// <summary>Gets a value indicating whether this is a radio channel.</summary>
    public bool IsRadio { get; init; }

    /// <summary>Gets the tag ids this channel belongs to.</summary>
    public IReadOnlyList<long> TagIds { get; init; } = Array.Empty<long>();

    /// <summary>Gets the id of the event currently on air.</summary>
    public long? EventId { get; init; }

    /// <summary>
    /// Gets the display number, falling back to the id-derived ordering when Tvheadend assigned none.
    /// </summary>
    /// <remarks>
    /// A channel without a number must still appear. The previous plugin dropped such channels
    /// entirely, which is why users had to renumber in Tvheadend just to see them.
    /// </remarks>
    public string DisplayNumber => Number <= 0
        ? string.Empty
        : NumberMinor > 0
            ? $"{Number}.{NumberMinor}"
            : Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>A Tvheadend channel tag, from <c>tagAdd</c> / <c>tagUpdate</c>.</summary>
internal sealed record HtspTag
{
    /// <summary>Gets the tag id.</summary>
    public required long Id { get; init; }

    /// <summary>Gets the tag name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the channel ids in this tag.</summary>
    public IReadOnlyList<long> Members { get; init; } = Array.Empty<long>();
}

/// <summary>An EPG event, from <c>eventAdd</c> / <c>eventUpdate</c> / <c>getEvents</c>.</summary>
internal sealed record HtspEvent
{
    /// <summary>Gets the event id.</summary>
    public required long Id { get; init; }

    /// <summary>Gets the channel id.</summary>
    public required long ChannelId { get; init; }

    /// <summary>Gets the start time.</summary>
    public required DateTime Start { get; init; }

    /// <summary>Gets the end time.</summary>
    public required DateTime Stop { get; init; }

    /// <summary>Gets the title.</summary>
    public string? Title { get; init; }

    /// <summary>Gets the episode sub-title.</summary>
    public string? Subtitle { get; init; }

    /// <summary>Gets the long description.</summary>
    public string? Description { get; init; }

    /// <summary>Gets the DVB content type: high nibble is the category, low nibble the sub-category.</summary>
    public int ContentType { get; init; }

    /// <summary>Gets the season number, when Tvheadend knows a real one.</summary>
    public int? SeasonNumber { get; init; }

    /// <summary>Gets the episode number, when Tvheadend knows a real one.</summary>
    public int? EpisodeNumber { get; init; }

    /// <summary>Gets the series link id, used to group episodes.</summary>
    public string? SeriesLinkId { get; init; }

    /// <summary>Gets the age rating. Maps to Jellyfin's OfficialRating.</summary>
    public int? AgeRating { get; init; }

    /// <summary>Gets the 0-10 quality score. Maps to Jellyfin's CommunityRating, never OfficialRating.</summary>
    public int? StarRating { get; init; }

    /// <summary>Gets the first-aired date.</summary>
    public DateTime? FirstAired { get; init; }

    /// <summary>Gets the artwork URL.</summary>
    public string? Image { get; init; }

    /// <summary>Gets a value indicating whether the broadcast is a repeat.</summary>
    public bool IsRepeat { get; init; }

    /// <summary>Gets a value indicating whether the broadcast is a premiere.</summary>
    public bool IsNew { get; init; }

    /// <summary>Gets the copyright year.</summary>
    public int? CopyrightYear { get; init; }
}

/// <summary>A Tvheadend DVR entry, from <c>dvrEntryAdd</c> / <c>dvrEntryUpdate</c>.</summary>
internal sealed record HtspDvrEntry
{
    /// <summary>Gets the DVR entry id.</summary>
    public required long Id { get; init; }

    /// <summary>Gets the channel id.</summary>
    public long ChannelId { get; init; }

    /// <summary>Gets the scheduled start, excluding padding.</summary>
    public required DateTime Start { get; init; }

    /// <summary>Gets the scheduled stop, excluding padding.</summary>
    public required DateTime Stop { get; init; }

    /// <summary>Gets the padding before the programme, in seconds.</summary>
    public int PrePaddingSeconds { get; init; }

    /// <summary>Gets the padding after the programme, in seconds.</summary>
    public int PostPaddingSeconds { get; init; }

    /// <summary>Gets the title.</summary>
    public string? Title { get; init; }

    /// <summary>Gets the episode sub-title.</summary>
    public string? Subtitle { get; init; }

    /// <summary>Gets the description.</summary>
    public string? Description { get; init; }

    /// <summary>Gets the Tvheadend state: scheduled, recording, completed, missed or invalid.</summary>
    public string? State { get; init; }

    /// <summary>Gets the error string, when the recording failed.</summary>
    public string? Error { get; init; }

    /// <summary>Gets the autorec rule that created this entry, if any.</summary>
    public string? AutorecId { get; init; }

    /// <summary>Gets the originating EPG event id.</summary>
    public long? EventId { get; init; }

    /// <summary>Gets the DVB content type.</summary>
    public int ContentType { get; init; }

    /// <summary>Gets the recorded file size in bytes.</summary>
    public long? FileSize { get; init; }

    /// <summary>Gets the artwork URL.</summary>
    public string? Image { get; init; }

    /// <summary>Gets a value indicating whether the recording has finished and is playable.</summary>
    public bool IsCompleted => string.Equals(State, "completed", StringComparison.OrdinalIgnoreCase);

    /// <summary>Gets a value indicating whether the recording is in progress.</summary>
    public bool IsRecording => string.Equals(State, "recording", StringComparison.OrdinalIgnoreCase);

    /// <summary>Gets a value indicating whether the recording is scheduled but not started.</summary>
    public bool IsScheduled => string.Equals(State, "scheduled", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A Tvheadend autorec rule, from <c>autorecEntryAdd</c> / <c>autorecEntryUpdate</c>.</summary>
internal sealed record HtspAutorecEntry
{
    /// <summary>Gets the rule id.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the rule title / match pattern.</summary>
    public string? Title { get; init; }

    /// <summary>Gets the channel id, or zero to match any channel.</summary>
    public long ChannelId { get; init; }

    /// <summary>Gets a value indicating whether the rule is enabled.</summary>
    public bool Enabled { get; init; }

    /// <summary>Gets the earliest start, in minutes from midnight, or null for any.</summary>
    public int? StartMinutes { get; init; }

    /// <summary>Gets the latest start, in minutes from midnight, or null for any.</summary>
    public int? StartWindowMinutes { get; init; }

    /// <summary>Gets the day-of-week bitmask, bit 0 = Monday.</summary>
    public int DaysOfWeek { get; init; }

    /// <summary>Gets the padding before, in seconds.</summary>
    public int PrePaddingSeconds { get; init; }

    /// <summary>Gets the padding after, in seconds.</summary>
    public int PostPaddingSeconds { get; init; }

    /// <summary>Gets the priority.</summary>
    public int Priority { get; init; }

    /// <summary>Gets the series link this rule follows, if any.</summary>
    public string? SeriesLinkId { get; init; }
}
