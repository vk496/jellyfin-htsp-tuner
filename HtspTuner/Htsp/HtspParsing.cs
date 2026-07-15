namespace HtspTuner.Htsp;

/// <summary>
/// Shared helpers for turning raw HTSP wire values into the typed model.
/// </summary>
internal static class HtspParsing
{
    // MPEG-4 sampling-frequency table; Tvheadend's `rate` field is an index into it, not a value in Hz.
    private static readonly int[] _samplingFrequencies =
    {
        96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050,
        16000, 12000, 11025, 8000, 7350,
    };

    /// <summary>Maps a Tvheadend stream type string to a codec.</summary>
    /// <param name="type">The <c>type</c> field of a stream entry.</param>
    /// <returns>The codec, or <see cref="HtspCodec.Unknown"/>.</returns>
    public static HtspCodec ParseCodec(string? type) => type switch
    {
        "H264" => HtspCodec.H264,
        "HEVC" => HtspCodec.Hevc,
        "MPEG2VIDEO" => HtspCodec.Mpeg2Video,
        "MPEG2AUDIO" => HtspCodec.Mpeg2Audio,
        "AAC" => HtspCodec.Aac,
        "AACLATM" => HtspCodec.AacLatm,
        "AC3" => HtspCodec.Ac3,
        "EAC3" => HtspCodec.Eac3,
        "VORBIS" => HtspCodec.Vorbis,
        "DVBSUB" => HtspCodec.DvbSub,
        "TELETEXT" => HtspCodec.Teletext,
        "TEXTSUB" => HtspCodec.TextSub,
        "PCR" => HtspCodec.Pcr,
        _ => HtspCodec.Unknown,
    };

    /// <summary>Resolves Tvheadend's SRI index to a sample rate in Hz.</summary>
    /// <param name="sri">The <c>rate</c> field, or null.</param>
    /// <returns>The sample rate in Hz, or null when unknown.</returns>
    public static int? SampleRateFromIndex(long? sri)
        => sri is >= 0 && sri < 13 ? _samplingFrequencies[sri.Value] : null;

    /// <summary>Builds a typed stream from a <c>subscriptionStart.streams[]</c> map.</summary>
    /// <param name="m">The stream map.</param>
    /// <returns>The typed stream.</returns>
    public static HtspStream ParseStream(HtspMessage m)
    {
        var type = m.GetString("type") ?? string.Empty;
        return new HtspStream
        {
            Index = (int)m.GetInt("index"),
            Codec = ParseCodec(type),
            RawType = type,
            Language = m.GetString("language"),
            Width = (int?)m.GetIntOrNull("width"),
            Height = (int?)m.GetIntOrNull("height"),
            Duration = (int?)m.GetIntOrNull("duration"),
            AspectNum = (int?)m.GetIntOrNull("aspect_num"),
            AspectDen = (int?)m.GetIntOrNull("aspect_den"),
            Channels = (int?)m.GetIntOrNull("channels"),
            SampleRate = SampleRateFromIndex(m.GetIntOrNull("rate")),
            CompositionId = (int?)m.GetIntOrNull("composition_id"),
            AncillaryId = (int?)m.GetIntOrNull("ancillary_id"),
            AudioType = (int)m.GetInt("audio_type"),
        };
    }
}
