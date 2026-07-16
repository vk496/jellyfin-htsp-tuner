using HtspTuner.Htsp;
using HtspTuner.LiveTv;
using HtspTuner.Mpeg;
using MediaBrowser.Model.Entities;
using Xunit;

namespace HtspTuner.Tests;

/// <summary>
/// Jellyfin addresses an audio track by its index in the table we publish, but a client that direct-plays
/// the TS demuxes it itself — and one that skips a subtitle codec it cannot decode numbers everything after
/// that subtitle one lower. So a subtitle emitted before the audio silently shifts every audio track for
/// those clients. Keeping non-audio last makes that impossible.
/// </summary>
public class StreamOrderTests
{
    private static HtspStream Video() => new() { Index = 1, Codec = HtspCodec.H264, RawType = "H264", Width = 1920, Height = 1080 };

    private static HtspStream Audio(int index, string lang, int channels = 2) => new()
    {
        Index = index, Codec = HtspCodec.Mpeg2Audio, RawType = "MPEG2AUDIO", Language = lang, Channels = channels,
    };

    private static HtspStream Teletext(int index) => new()
    {
        Index = index, Codec = HtspCodec.Teletext, RawType = "TELETEXT", Language = "spa",
    };

    // Boing's real layout: Tvheadend hands us the teletext BETWEEN the video and the audio. Emitting that
    // order verbatim made one TV play English when Spanish was selected, play the audio-description track
    // when English was selected, and kill the stream on the last track — each selection landing one late.
    [Fact]
    public void Subtitles_are_emitted_after_audio_even_when_tvheadend_puts_them_first()
    {
        var muxer = new TsMuxer(new HtspSubscriptionStart
        {
            SubscriptionId = 1,
            Meta = new List<byte[]>(),
            Streams = new List<HtspStream>
            {
                Video(),
                Teletext(2),          // <- Tvheadend's order puts this before the audio
                Audio(3, "spa"),
                Audio(4, "qaa"),
                Audio(5, "spa", channels: 1),
            },
        });

        var kinds = muxer.Streams.Select(s => s.Source.IsVideo ? "V" : s.Source.IsAudio ? "A" : "S");
        Assert.Equal(new[] { "V", "A", "A", "A", "S" }, kinds);

        // The broadcaster's own audio order must survive, or the default track changes.
        var langs = muxer.Streams.Where(s => s.Source.IsAudio).Select(s => s.Source.Language);
        Assert.Equal(new[] { "spa", "qaa", "spa" }, langs);
    }

    // The indexes we publish to Jellyfin come from the emit order, so they must agree with it: a client
    // asking for "the Spanish track" must get the index that really carries Spanish.
    [Fact]
    public void Published_indexes_match_the_emitted_order()
    {
        var muxer = new TsMuxer(new HtspSubscriptionStart
        {
            SubscriptionId = 1,
            Meta = new List<byte[]>(),
            Streams = new List<HtspStream> { Video(), Teletext(2), Audio(3, "spa"), Audio(4, "qaa") },
        });

        var built = MediaStreamBuilder.Build(muxer.Streams);
        var spanish = built.Single(s => s.Type == MediaStreamType.Audio && s.Language == "spa");

        Assert.Equal(1, spanish.Index);                       // straight after the video, not after a subtitle
        Assert.Equal(built.Count - 1, built.Single(s => s.Type == MediaStreamType.Subtitle).Index);
    }
}
