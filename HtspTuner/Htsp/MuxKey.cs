using System.Text.RegularExpressions;

namespace HtspTuner.Htsp;

/// <summary>
/// Works out which physical multiplex a subscription is tuned to, for deciding whether two channels can
/// share one tune.
/// </summary>
/// <remarks>
/// Normally Tvheadend's mux uuid answers this exactly: one object per multiplex, and a name like
/// <c>12130H</c> is only unique within its network. Some setups break that one-to-one. A network feeding
/// channels in through a pipe names its muxes after what the pipe does, so
/// <c>abertpy: MUX 11347H pPID 2308</c> and <c>abertpy: MUX 11347H pPID 2309</c> are two Tvheadend objects
/// with two uuids that are the same transponder — the uuid says "retune", the tuner says "already there".
/// For those, the transponder in the name is the real identity.
/// </remarks>
internal static partial class MuxKey
{
    // Networks known to name several mux objects after one transponder. A short list on purpose: matching
    // a transponder out of a name is a guess, and it is only safe where the naming is known.
    private static readonly string[] SharedTransponderNetworks = ["abertpy"];

    /// <summary>Gets the key identifying the multiplex a subscription is tuned to.</summary>
    /// <param name="source">The subscription's source info, as reported by Tvheadend.</param>
    /// <returns>The key, or null if the source says nothing useful.</returns>
    public static string? For(HtspSourceInfo? source)
    {
        if (source is null)
        {
            return null;
        }

        var name = source.Mux;
        if (!string.IsNullOrEmpty(name))
        {
            var haystack = source.Network + " " + name;
            foreach (var marker in SharedTransponderNetworks)
            {
                if (haystack.Contains(marker, StringComparison.OrdinalIgnoreCase)
                    && TransponderRegex().Match(name) is { Success: true } m)
                {
                    // Scoped to the network: a bare "11347H" means nothing on its own, and two networks can
                    // each have one.
                    return string.Concat(
                        source.Network ?? marker,
                        ":",
                        m.Groups[1].Value,
                        m.Groups[2].Value.ToUpperInvariant());
                }
            }
        }

        return source.MuxUuid ?? name;
    }

    // A DVB-S transponder as it is written in a mux name: frequency in MHz followed by polarisation.
    [GeneratedRegex(@"(\d{3,6})\s*([HVhv])(?![0-9A-Za-z])")]
    private static partial Regex TransponderRegex();
}
