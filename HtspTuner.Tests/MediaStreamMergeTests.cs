using HtspTuner.LiveTv;
using MediaBrowser.Model.Entities;
using Xunit;

namespace HtspTuner.Tests;

/// <summary>
/// Tests <see cref="MediaStreamBuilder.MergeProbe"/> — the overlay of probe-only fields (interlacing,
/// colour/HDR, bit depth, profile/level, real bit rates) that HTSP never reports. The "probed" values are
/// the real ffprobe output captured from the plugin's own muxed TS for an interlaced H264 channel and an
/// HDR HEVC channel.
/// </summary>
public class MediaStreamMergeTests
{
    private static List<MediaStream> HtspBuilt() => new()
    {
        // What MediaStreamBuilder produces from HTSP alone: no interlacing/colour/profile, estimated bitrate.
        new MediaStream { Type = MediaStreamType.Video, Codec = "h264", BitRate = 6_000_000, AverageFrameRate = 25f, RealFrameRate = 25f },
        new MediaStream { Type = MediaStreamType.Audio, Codec = "mp2", Channels = 2, SampleRate = 48000 },
    };

    [Fact]
    public void MergeProbe_InterlacedH264_SetsScanTypeProfileAndRealAudioBitrate()
    {
        var ours = HtspBuilt();
        var probed = new List<MediaStream>
        {
            new MediaStream
            {
                Type = MediaStreamType.Video,
                IsInterlaced = true, Profile = "High", Level = 41, BitDepth = 8, RefFrames = 1,
                PixelFormat = "yuv420p", ColorSpace = "bt709", ColorTransfer = "bt709", ColorPrimaries = "bt709",
                AverageFrameRate = 25f, RealFrameRate = 50f, // 50 = field rate; probe convention
            },
            new MediaStream { Type = MediaStreamType.Audio, BitRate = 128_000, ChannelLayout = "stereo" },
        };

        MediaStreamBuilder.MergeProbe(ours, probed);

        var video = ours[0];
        Assert.True(video.IsInterlaced);              // the combing fix
        Assert.Equal("High", video.Profile);
        Assert.Equal(41.0, video.Level!.Value);
        Assert.Equal(8, video.BitDepth!.Value);
        Assert.Equal(1, video.RefFrames!.Value);
        Assert.Equal("bt709", video.ColorTransfer);
        Assert.Equal(6_000_000, video.BitRate!.Value);   // probe had no video bitrate -> estimate kept
        Assert.Equal(25f, video.AverageFrameRate!.Value); // picture rate, not overwritten to 50
        Assert.Equal(50f, video.RealFrameRate!.Value);    // probe's field rate applied
        Assert.Null(video.IsAVC);                         // never forced false

        Assert.Equal(128_000, ours[1].BitRate!.Value);    // real audio bitrate replaced the estimate
        Assert.Equal("stereo", ours[1].ChannelLayout);
    }

    [Fact]
    public void MergeProbe_HdrHevc_SetsBitDepthAndHdrTransfer()
    {
        var ours = HtspBuilt();
        ours[0].Codec = "hevc";
        var probed = new List<MediaStream>
        {
            new MediaStream
            {
                Type = MediaStreamType.Video,
                IsInterlaced = false, Profile = "Main 10", Level = 153, BitDepth = 10,
                PixelFormat = "yuv420p10le", ColorSpace = "bt2020nc", ColorTransfer = "arib-std-b67", ColorPrimaries = "bt2020",
            },
        };

        MediaStreamBuilder.MergeProbe(ours, probed);

        var video = ours[0];
        Assert.False(video.IsInterlaced);
        Assert.Equal("Main 10", video.Profile);
        Assert.Equal(10, video.BitDepth!.Value);
        Assert.Equal("arib-std-b67", video.ColorTransfer); // HLG -> Jellyfin derives HDR VideoRange
        Assert.Equal("bt2020", video.ColorPrimaries);
    }

    [Fact]
    public void MergeProbe_FewerProbedAudioStreams_DoesNotThrow()
    {
        var ours = HtspBuilt();
        ours.Add(new MediaStream { Type = MediaStreamType.Audio, Codec = "mp2", Channels = 1 });

        // Only one audio in the probe; the second must be left untouched, not crash.
        var probed = new List<MediaStream>
        {
            new MediaStream { Type = MediaStreamType.Video, Profile = "High" },
            new MediaStream { Type = MediaStreamType.Audio, BitRate = 128_000 },
        };

        MediaStreamBuilder.MergeProbe(ours, probed);

        Assert.Equal(128_000, ours[1].BitRate!.Value);
        Assert.Null(ours[2].BitRate);
    }
}
