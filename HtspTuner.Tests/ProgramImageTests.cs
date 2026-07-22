using HtspTuner.LiveTv;
using Xunit;

namespace HtspTuner.Tests;

/// <summary>
/// A sweep captures channels one at a time and may not finish, so the order decides what actually gets a
/// picture. Two things drive it: how prominent a tile is on the Live TV page, since one nobody can see is
/// not worth a tuner ahead of one they are looking at; and the multiplex, since Tvheadend serves a second
/// channel off one it is already tuned to without touching the tuner.
/// </summary>
public class ProgramImageTests
{
    private sealed record Candidate(string Name, string? Mux, int Rank);

    private static List<string> Order(params Candidate[] items)
        => ProgramImageService.CaptureOrder(items, c => c.Mux, c => c.Rank).Select(c => c.Name).ToList();

    [Fact]
    public void TheMostProminentTileGoesFirst()
    {
        var order = Order(
            new Candidate("third", "A", 2),
            new Candidate("first", "B", 0),
            new Candidate("second", "C", 1));

        Assert.Equal("first", order[0]);
    }

    [Fact]
    public void ChannelsOnTheSameMuxFollowImmediately()
    {
        // Once a mux has been tuned for the top tile, the rest of its channels are nearly free, so they
        // come next even though other tiles rank higher than them individually.
        var order = Order(
            new Candidate("top", "A", 0),
            new Candidate("other-mux", "B", 1),
            new Candidate("same-mux", "A", 5));

        Assert.Equal(["top", "same-mux", "other-mux"], order);
    }

    [Fact]
    public void AWatchedChannelOutranksThePage()
    {
        // Ranked below zero: already tuned, so it costs nothing and is demonstrably being looked at.
        var order = Order(
            new Candidate("page-top", "A", 0),
            new Candidate("watched", "B", -1));

        Assert.Equal("watched", order[0]);
    }

    [Fact]
    public void ChannelsNotOnThePageGoLast()
    {
        var order = Order(
            new Candidate("offpage", null, int.MaxValue),
            new Candidate("onpage", "A", 3));

        Assert.Equal(["onpage", "offpage"], order);
    }

    [Fact]
    public void AnUnknownMuxIsNotAGroup()
    {
        // Two channels we have never tuned are not "the same multiplex" -- they are simply unknown, and
        // pooling them would drag an obscure tile up next to a prominent one for no reason.
        var order = Order(
            new Candidate("top", null, 0),
            new Candidate("middle", "A", 1),
            new Candidate("bottom", null, 2));

        Assert.Equal(["top", "middle", "bottom"], order);
    }

    [Fact]
    public void EverythingIsKept()
    {
        var order = Order(
            new Candidate("a", "A", 0),
            new Candidate("b", null, int.MaxValue),
            new Candidate("c", "A", 1),
            new Candidate("d", "B", 2));

        Assert.Equal(4, order.Count);
        Assert.Equal(["a", "b", "c", "d"], order.Order(StringComparer.Ordinal).ToList());
    }
}
