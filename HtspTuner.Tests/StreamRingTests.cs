using HtspTuner.LiveTv;
using Xunit;

namespace HtspTuner.Tests;

/// <summary>Checks the bounded ring's core invariants: drop-oldest, multi-reader, and sync-point join.</summary>
public class StreamRingTests
{
    [Fact]
    public void Reader_from_sync_point_gets_contiguous_data_and_never_blocks_writer()
    {
        var ring = new StreamRing(64 * 1024); // clamped up to the 1 MiB minimum
        ring.Write(new byte[500]);            // pre-roll a reader will skip
        ring.MarkSyncPoint();
        var marker = new byte[] { 1, 2, 3, 4 };
        ring.Write(marker);

        var reader = ring.OpenReader();       // starts at the sync point
        ring.Write(new byte[1000]);           // more live data after the join
        ring.Complete();                      // let the reader see EOF once drained

        var buf = new byte[8];
        var n = reader.Read(buf, 0, buf.Length);
        Assert.True(n >= 4);
        Assert.Equal(marker, buf[..4]);       // the reader begins exactly at the sync point
    }

    // The live stream's idle watchdog closes an abandoned subscription based on this count alone, so a
    // reader that fails to release on dispose would keep a Tvheadend tuner busy forever.
    [Fact]
    public void Active_readers_tracks_open_readers_and_releases_on_dispose()
    {
        var ring = new StreamRing(1);
        Assert.Equal(0, ring.ActiveReaders);

        var a = ring.OpenReader();
        var b = ring.OpenReader();
        Assert.Equal(2, ring.ActiveReaders);

        a.Dispose();
        Assert.Equal(1, ring.ActiveReaders);

        a.Dispose();                          // double dispose must not double-decrement
        Assert.Equal(1, ring.ActiveReaders);

        b.Dispose();
        Assert.Equal(0, ring.ActiveReaders);
    }

    [Fact]
    public void Overflow_drops_oldest_without_blocking()
    {
        var ring = new StreamRing(1); // clamped to 1 MiB
        // Write well past capacity; must return promptly rather than block.
        for (var i = 0; i < 4; i++)
        {
            ring.Write(new byte[512 * 1024]);
        }

        Assert.Equal(4L * 512 * 1024, ring.TotalWritten);
    }
}
