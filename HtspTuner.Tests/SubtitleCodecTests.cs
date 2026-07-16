using HtspTuner.Htsp;
using HtspTuner.LiveTv;
using HtspTuner.Mpeg;
using MediaBrowser.Model.Entities;
using Xunit;

namespace HtspTuner.Tests;

/// <summary>
/// Guards the codec names we report for subtitles. We set <c>SupportsProbing=false</c> and build the stream
/// table from HTSP ourselves, so Jellyfin's <c>ProbeResultNormalizer</c> never gets to rewrite these — which
/// means we have to emit what it would have produced. Asserted against Jellyfin's own
/// <see cref="MediaStream.IsTextSubtitleStream"/> rather than a hardcoded string, so that if Jellyfin ever
/// changes how it tells graphical subtitles from text ones, this fails instead of a user's playback.
/// </summary>
public class SubtitleCodecTests
{
    private static List<MediaStream> BuildFor(params HtspStream[] streams)
        => MediaStreamBuilder.Build(new TsMuxer(new HtspSubscriptionStart
        {
            SubscriptionId = 1,
            Streams = streams,
            Meta = new List<byte[]>(),
        }).Streams);

    private static HtspStream Video() => new()
    {
        Index = 1, Codec = HtspCodec.H264, RawType = "H264", Width = 1920, Height = 1080,
    };

    // A bitmap subtitle reported as text sends Jellyfin down its extract-to-.srt path; the file never
    // appears and ffmpeg exits on "Unable to open .../N.srt", so enabling subtitles kills playback.
    [Fact]
    public void Dvb_bitmap_subtitles_are_not_reported_as_text()
    {
        var built = BuildFor(
            Video(),
            new HtspStream
            {
                Index = 2, Codec = HtspCodec.DvbSub, RawType = "DVBSUB", Language = "spa",
                CompositionId = 1, AncillaryId = 1,
            });

        var subtitle = built.Single(s => s.Type == MediaStreamType.Subtitle);
        Assert.False(subtitle.IsTextSubtitleStream);
    }

    // Teletext is the opposite case, and the reason this is not just "subtitles are never text": ffmpeg
    // decodes dvb_teletext to real text via libzvbi, so Jellyfin's DVBTXT genuinely IS a text format and
    // the extract path is the correct one for it. Pinned so a future "fix" cannot lump the two together.
    [Fact]
    public void Teletext_subtitles_are_reported_as_text()
    {
        var built = BuildFor(
            Video(),
            new HtspStream { Index = 2, Codec = HtspCodec.Teletext, RawType = "TELETEXT", Language = "spa" });

        var subtitle = built.Single(s => s.Type == MediaStreamType.Subtitle);
        Assert.True(subtitle.IsTextSubtitleStream);
    }
}
