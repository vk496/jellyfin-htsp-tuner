using HtspTuner.Htsp;
using HtspTuner.LiveTv;
using HtspTuner.Mpeg;
using MediaBrowser.Model.Entities;
using Xunit;

namespace HtspTuner.Tests;

/// <summary>
/// DVB audio_type is the only thing distinguishing an audio-description track from a normal one, and
/// Jellyfin has no field for it — only IsHearingImpaired. Nothing on the wire names a track, so the label
/// is synthesised here. Without it, picking the track yields narration over silence (DVB audio description
/// is normally "receiver mix": narration only, expecting the receiver to mix in the main audio) and looks
/// like a broken stream.
/// </summary>
public class AudioTrackLabelTests
{
    private static List<MediaStream> Build(params HtspStream[] streams)
        => MediaStreamBuilder.Build(new TsMuxer(new HtspSubscriptionStart
        {
            SubscriptionId = 1,
            Meta = new List<byte[]>(),
            Streams = streams,
        }).Streams);

    private static HtspStream Video() => new() { Index = 1, Codec = HtspCodec.H264, RawType = "H264", Width = 1920, Height = 1080 };

    private static HtspStream Audio(int index, int audioType, int channels = 2) => new()
    {
        Index = index, Codec = HtspCodec.Mpeg2Audio, RawType = "MPEG2AUDIO",
        Language = "spa", Channels = channels, AudioType = audioType,
    };

    // Boing's real layout: two normal tracks plus a mono audio_type=3 description track.
    [Fact]
    public void Audio_description_track_is_labelled_and_normal_tracks_are_not()
    {
        var built = Build(Video(), Audio(2, 0), Audio(3, 0), Audio(4, audioType: 3, channels: 1));
        var audio = built.Where(s => s.Type == MediaStreamType.Audio).ToList();

        Assert.Null(audio[0].Title);
        Assert.Null(audio[1].Title);
        Assert.Equal("Audio description", audio[2].Title);

        // The label must reach the picker, not just sit in the DTO.
        Assert.Contains("Audio description", audio[2].DisplayTitle, StringComparison.Ordinal);
    }

    // audio_type 2 sets Jellyfin's own flag and gets no title from us.
    //
    // Note what this pins: Jellyfin sets IsHearingImpaired but does NOT render it for audio tracks — its
    // DisplayTitle only prints that attribute in the Subtitle branch. So the flag is carried and shown
    // nowhere. That is a Jellyfin gap, not ours, and it is left alone deliberately: a hearing-impaired
    // track still plays normal programme audio, so picking one blind is a mild surprise, unlike the
    // narration-over-silence of an audio-description track. If this ever needs a title too, this test is
    // where the assumption is recorded.
    [Fact]
    public void Hearing_impaired_sets_jellyfins_flag_and_gets_no_title_from_us()
    {
        var built = Build(Video(), Audio(2, audioType: 2));
        var audio = built.Single(s => s.Type == MediaStreamType.Audio);

        Assert.True(audio.IsHearingImpaired);
        Assert.Null(audio.Title);
        Assert.DoesNotContain("Audio description", audio.DisplayTitle, StringComparison.Ordinal);
    }
}
