using HtspTuner.Htsp;
using Xunit;

namespace HtspTuner.Tests;

/// <summary>
/// Two channels share a tune only if they are on the same physical multiplex. Tvheadend's mux uuid usually
/// says that exactly, but a network that feeds channels through a pipe gets one mux object per PID, all of
/// them the same transponder — so the uuid claims a retune is needed where the tuner is already there.
/// </summary>
public class MuxKeyTests
{
    private static HtspSourceInfo Source(string? network, string? mux, string? uuid)
        => new() { Network = network, Mux = mux, MuxUuid = uuid };

    [Fact]
    public void OrdinaryMuxUsesTheUuid()
    {
        // The name "12130H" is only unique inside its network, so the uuid is the right identity.
        var key = MuxKey.For(Source("Hispasat", "12130H", "55288a8ce3583553dc0d1a7bde7757bb"));

        Assert.Equal("55288a8ce3583553dc0d1a7bde7757bb", key);
    }

    [Fact]
    public void SameTransponderDifferentPidsShareAKey()
    {
        // The real case: two Tvheadend mux objects, two uuids, one transponder.
        var a = MuxKey.For(Source("abertpy: Abertis", "abertpy: MUX 11347H pPID 2308", "aaaaaaaa"));
        var b = MuxKey.For(Source("abertpy: Abertis", "abertpy: MUX 11347H pPID 2309", "bbbbbbbb"));

        Assert.Equal(a, b);
        Assert.NotNull(a);
    }

    [Fact]
    public void DifferentTranspondersDoNotShareAKey()
    {
        var a = MuxKey.For(Source("abertpy: Abertis", "abertpy: MUX 11347H pPID 2308", "aaaaaaaa"));
        var b = MuxKey.For(Source("abertpy: Abertis", "abertpy: MUX 12476V pPID 2308", "bbbbbbbb"));

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void TheKeyIsScopedToItsNetwork()
    {
        // A bare "11347H" means nothing on its own, and two networks can each have one.
        var key = MuxKey.For(Source("abertpy: Abertis", "abertpy: MUX 11347H pPID 2308", "aaaaaaaa"));

        Assert.StartsWith("abertpy: Abertis:", key, StringComparison.Ordinal);
        Assert.EndsWith("11347H", key, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePidIsNotMistakenForATransponder()
    {
        // "2308" is a PID, not a frequency: only a number followed by a polarisation counts.
        var key = MuxKey.For(Source("abertpy: Abertis", "abertpy: pPID 2308 MUX 11347H", "aaaaaaaa"));

        Assert.EndsWith("11347H", key, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownNetworkIsLeftOnItsUuidEvenIfTheNameLooksLikeATransponder()
    {
        // Matching a transponder out of a name is a guess. Where the naming is not known to be one-object-
        // per-PID, the uuid is still the safer answer -- guessing here would merge muxes that are distinct.
        var key = MuxKey.For(Source("Some IPTV", "MUX 11347H stream 4", "cccccccc"));

        Assert.Equal("cccccccc", key);
    }

    [Fact]
    public void MissingSourceInfoHasNoKey() => Assert.Null(MuxKey.For(null));

    [Fact]
    public void ANamelessMuxFallsBackToItsUuid()
        => Assert.Equal("dddddddd", MuxKey.For(Source("TNT", null, "dddddddd")));
}
