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

    // Teletext is dropped entirely (TsMuxer.MapStreamType returns null for it), so it must never reach the
    // published stream table at all. Jellyfin cannot render DVB teletext subtitles through its transcode
    // pipeline, and offering one that breaks on selection is worse than offering none. If a future change
    // re-adds it, this fails and sends you back to the muxer comment explaining why it went.
    [Fact]
    public void Teletext_is_not_published_because_jellyfin_cannot_render_it()
    {
        var built = BuildFor(
            Video(),
            new HtspStream { Index = 2, Codec = HtspCodec.Teletext, RawType = "TELETEXT", Language = "spa" });

        Assert.DoesNotContain(built, s => s.Type == MediaStreamType.Subtitle);
    }
}
