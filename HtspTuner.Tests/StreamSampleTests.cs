using HtspTuner.LiveTv;
using Xunit;

namespace HtspTuner.Tests;

public class StreamSampleTests
{
    [Fact]
    public async Task Capture_expiry_returns_partial_file_and_releases_reader()
    {
        using var ring = new StreamRing(1);
        var data = Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray();
        ring.Write(data);
        var path = Path.GetTempFileName();
        try
        {
            var count = await StreamSample.CaptureAsync(ring, path, 1_500_000, CancellationToken.None, TimeSpan.FromMilliseconds(100))
                .WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(data.Length, count);
            Assert.Equal(data, await File.ReadAllBytesAsync(path));
            Assert.Equal(0, ring.ActiveReaders);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Caller_cancellation_is_not_treated_as_capture_expiry()
    {
        using var ring = new StreamRing(1);
        using var parent = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var path = Path.GetTempFileName();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                StreamSample.CaptureAsync(ring, path, 1_500_000, parent.Token, TimeSpan.FromSeconds(5))
                    .WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(0, ring.ActiveReaders);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Empty_sample_expires_without_waiting_for_another_packet()
    {
        using var ring = new StreamRing(1);
        var path = Path.GetTempFileName();
        try
        {
            Assert.Equal(0, await StreamSample.CaptureAsync(ring, path, 188, CancellationToken.None, TimeSpan.FromMilliseconds(100))
                .WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Empty(await File.ReadAllBytesAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Unbounded_capture_drains_completed_stream_and_obeys_byte_limit()
    {
        using var ring = new StreamRing(1);
        ring.Write(new byte[] { 1, 2, 3, 4 });
        ring.Complete();
        var path = Path.GetTempFileName();
        try
        {
            Assert.Equal(3, await StreamSample.CaptureAsync(ring, path, 3, CancellationToken.None));
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Memory_read_honors_cancellation_on_empty_live_ring()
    {
        using var ring = new StreamRing(1);
        using var reader = ring.OpenReader();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            reader.ReadAsync(new byte[188].AsMemory(), timeout.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
    }
}
