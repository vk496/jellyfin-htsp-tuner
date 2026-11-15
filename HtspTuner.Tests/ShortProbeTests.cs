using HtspTuner.LiveTv;
using MediaBrowser.Model.Entities;
using Xunit;

namespace HtspTuner.Tests;

public class ShortProbeTests
{
    private static List<MediaStream> Streams(string codec = "h264") => new()
    {
        new() { Type = MediaStreamType.Video, Index = 0, Codec = codec, Width = 1920, Height = 1080, PixelFormat = "yuv420p", Profile = "High", IsInterlaced = true },
        new() { Type = MediaStreamType.Audio, Index = 1, Codec = "mp2", Channels = 2, SampleRate = 48000 },
    };

    [Fact]
    public void Audio_only_probe_is_not_sufficient_for_a_video_channel()
        => Assert.False(MediaStreamBuilder.CanMergeProbe(Streams(), Streams().Skip(1).ToList()));

    [Fact]
    public void Video_PMT_entry_without_decoder_headers_is_not_sufficient()
    {
        var incomplete = Streams();
        incomplete[0] = new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = "h264" };
        Assert.False(MediaStreamBuilder.CanMergeProbe(Streams(), incomplete));
    }

    [Fact]
    public void Missing_alternate_audio_cannot_be_cached_as_complete()
    {
        var expected = Streams();
        expected.Add(new MediaStream { Type = MediaStreamType.Audio, Index = 2, Codec = "aac" });
        Assert.False(MediaStreamBuilder.CanMergeProbe(expected, Streams()));
    }

    [Fact]
    public void Reordered_or_mismatched_audio_cannot_be_merged_pairwise()
    {
        var found = Streams();
        found[1].Index = 2;
        Assert.False(MediaStreamBuilder.CanMergeProbe(Streams(), found));
        found[1].Index = 1;
        found[1].Codec = "ac3";
        Assert.False(MediaStreamBuilder.CanMergeProbe(Streams(), found));
    }

    [Fact]
    public void Audio_headers_are_required_and_radio_does_not_require_video()
    {
        var radio = Streams().Skip(1).ToList();
        Assert.True(MediaStreamBuilder.CanMergeProbe(radio, radio));
        var incomplete = Streams().Skip(1).ToList();
        incomplete[0].SampleRate = null;
        Assert.False(MediaStreamBuilder.CanMergeProbe(radio, incomplete));
        Assert.False(MediaStreamBuilder.CanMergeProbe(radio, null));
        Assert.False(MediaStreamBuilder.CanMergeProbe(radio, Array.Empty<MediaStream>()));
    }

    [Theory]
    [InlineData("h264")]
    [InlineData("mpeg2video")]
    [InlineData("hevc")]
    public void Complete_video_headers_are_accepted(string codec)
        => Assert.True(MediaStreamBuilder.CanMergeProbe(Streams(codec), Streams(codec)));

    [Fact]
    public void Complete_HDR_metadata_remains_intact_when_merged()
    {
        var expected = Streams("hevc");
        var found = Streams("hevc");
        var video = found[0];
        video.Profile = "Main 10";
        video.PixelFormat = "yuv420p10le";
        video.BitDepth = 10;
        video.ColorTransfer = "smpte2084";
        video.ColorSpace = "bt2020nc";
        video.ColorPrimaries = "bt2020";
        Assert.True(MediaStreamBuilder.CanMergeProbe(expected, found));
        MediaStreamBuilder.MergeProbe(expected, found);
        Assert.Equal(10, expected[0].BitDepth);
        Assert.Equal("smpte2084", expected[0].ColorTransfer);
        Assert.Equal("bt2020", expected[0].ColorPrimaries);
    }

    [Fact]
    public async Task Successful_short_sample_does_not_retry()
    {
        using var ring = new StreamRing(1);
        ring.Write(new byte[] { 1, 2, 3 });
        ring.Complete();
        var path = Path.GetTempFileName();
        var calls = 0;
        try
        {
            var result = await HtspLiveStream.ProbeSampleAsync(ring, Streams(), path, async (p, _) =>
            {
                calls++;
                Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(p));
                return Streams();
            }, CancellationToken.None);
            Assert.True(result[0].IsInterlaced);
            Assert.Equal(1, calls);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Missing_early_video_retries_from_the_newest_keyframe()
    {
        using var ring = new StreamRing(3_000_000);
        ring.Write(new byte[HtspLiveStream.ProbeSampleBytes]);
        var path = Path.GetTempFileName();
        var calls = 0;
        try
        {
            var result = await HtspLiveStream.ProbeSampleAsync(ring, Streams(), path, async (p, _) =>
            {
                if (++calls == 1)
                {
                    // Decoder headers/keyframe arrive after the first sample, while ffprobe is running.
                    ring.MarkSyncPoint();
                    var keyframe = new byte[HtspLiveStream.ProbeSampleBytes];
                    keyframe[0] = 0x47;
                    ring.Write(keyframe);
                    return Streams().Skip(1).ToList();
                }

                Assert.Equal(0x47, (await File.ReadAllBytesAsync(p))[0]);
                return Streams();
            }, CancellationToken.None);
            Assert.Equal(2, calls);
            Assert.True(result[0].IsInterlaced);
            Assert.Equal(0, ring.ActiveReaders);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Repeated_incomplete_metadata_is_never_returned_for_caching()
    {
        using var ring = new StreamRing(3_000_000);
        ring.Write(new byte[HtspLiveStream.ProbeSampleBytes]);
        var path = Path.GetTempFileName();
        var calls = 0;
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => HtspLiveStream.ProbeSampleAsync(ring, Streams(), path, (_, _) =>
            {
                calls++;
                return Task.FromResult<IReadOnlyList<MediaStream>?>(Streams().Skip(1).ToList());
            }, CancellationToken.None));
            Assert.Equal(2, calls);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Undecodable_first_sample_gets_one_retry()
    {
        using var ring = new StreamRing(3_000_000);
        ring.Write(new byte[HtspLiveStream.ProbeSampleBytes]);
        var path = Path.GetTempFileName();
        var calls = 0;
        try
        {
            await HtspLiveStream.ProbeSampleAsync(ring, Streams(), path, (_, _) =>
            {
                if (++calls == 1) { throw new InvalidDataException("No decoder headers yet"); }
                return Task.FromResult<IReadOnlyList<MediaStream>?>(Streams());
            }, CancellationToken.None);
            Assert.Equal(2, calls);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Overall_cancellation_during_retry_is_not_swallowed()
    {
        using var ring = new StreamRing(3_000_000);
        ring.Write(new byte[HtspLiveStream.ProbeSampleBytes]);
        using var parent = new CancellationTokenSource();
        var path = Path.GetTempFileName();
        var calls = 0;
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HtspLiveStream.ProbeSampleAsync(ring, Streams(), path, (_, _) =>
            {
                calls++;
                ring.MarkSyncPoint(); // The retry must wait for data instead of replaying the first sample.
                parent.CancelAfter(TimeSpan.FromMilliseconds(100));
                return Task.FromResult<IReadOnlyList<MediaStream>?>(Streams().Skip(1).ToList());
            }, parent.Token).WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(1, calls);
            Assert.Equal(0, ring.ActiveReaders);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Cancelled_caller_cannot_accept_an_otherwise_complete_probe()
    {
        using var ring = new StreamRing(1);
        ring.Write(new byte[] { 1 });
        ring.Complete();
        using var parent = new CancellationTokenSource();
        var path = Path.GetTempFileName();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HtspLiveStream.ProbeSampleAsync(ring, Streams(), path, (_, _) =>
            {
                parent.Cancel();
                return Task.FromResult<IReadOnlyList<MediaStream>?>(Streams());
            }, parent.Token));
        }
        finally { File.Delete(path); }
    }
}
