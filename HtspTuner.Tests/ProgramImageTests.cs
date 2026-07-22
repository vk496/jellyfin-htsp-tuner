using HtspTuner.LiveTv;
using Xunit;

namespace HtspTuner.Tests;

/// <summary>
/// A scan captures several channels back to back. Tvheadend can serve a second channel off a mux it is
/// already tuned to without touching the tuner, so the order the scan works through its candidates is the
/// difference between one tune and four.
/// </summary>
public class ProgramImageTests
{
    private static readonly Dictionary<string, string?> Muxes = new()
    {
        ["a"] = "12476H",
        ["b"] = "11856V",
        ["c"] = "12476H",
        ["d"] = null, // never tuned, so no mux is known for it
        ["e"] = "11856V",
    };

    [Fact]
    public void SameMuxChannelsAreCapturedBackToBack()
    {
        var order = ProgramImageService.GroupByMux(new[] { "a", "b", "c", "d", "e" }, c => Muxes[c]).ToList();

        // Two muxes, so two runs -- not four tunes interleaved.
        Assert.Equal(new[] { "b", "e", "a", "c", "d" }, order);
    }

    [Fact]
    public void ChannelsWithNoKnownMuxGoLast()
    {
        // Nothing is known about these yet, so they are the ones that will definitely cost a tune: doing
        // them first would split the runs the known muxes could have shared.
        var order = ProgramImageService.GroupByMux(new[] { "d", "a", "c" }, c => Muxes[c]).ToList();

        Assert.Equal("d", order[^1]);
    }
}
