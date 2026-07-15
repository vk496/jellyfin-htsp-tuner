using HtspTuner.Htsp;
using HtspTuner.LiveTv;
using Xunit;

namespace HtspTuner.Tests;

/// <summary>
/// Guards the stable channel-id key. If this ever changes for the same server, every channel id changes
/// and Jellyfin re-creates the whole channel list as duplicates -- the exact bug stable ids fixed.
/// </summary>
public class HtspTunerHostTests
{
    private static HtspClientOptions Server(string host, int port) => new() { Host = host, Port = port };

    [Fact]
    public void StableKey_SameServer_IsDeterministic()
    {
        var a = HtspTunerHost.StableKey(Server("tvh.example", 9982));
        var b = HtspTunerHost.StableKey(Server("tvh.example", 9982));
        Assert.Equal(a, b);
        Assert.Matches("^[0-9a-f]{12}$", a); // 12 stable hex chars
    }

    [Fact]
    public void StableKey_DiffersByHostAndPort()
    {
        var baseKey = HtspTunerHost.StableKey(Server("tvh.example", 9982));
        Assert.NotEqual(baseKey, HtspTunerHost.StableKey(Server("other.example", 9982)));
        Assert.NotEqual(baseKey, HtspTunerHost.StableKey(Server("tvh.example", 9981)));
    }
}
