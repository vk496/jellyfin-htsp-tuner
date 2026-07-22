namespace HtspTuner.LiveTv;

/// <summary>The outcome of one attempt to grab a still frame from a channel.</summary>
/// <param name="Path">The captured image, which the caller owns and must delete, or null on failure.</param>
/// <param name="Error">Why there is no image, or null on success.</param>
/// <param name="Fatal">
/// Whether the reason applies to every channel, not just this one. A sweep walks a list one channel at a
/// time; when the server is unreachable, working through the rest of it only produces the same answer
/// dozens more times.
/// </param>
internal readonly record struct FrameCapture(string? Path, string? Error, bool Fatal = false)
{
    /// <summary>Builds a failed outcome for this channel.</summary>
    /// <param name="reason">Why the capture produced nothing.</param>
    /// <returns>The outcome.</returns>
    public static FrameCapture Failed(string reason) => new(null, reason);

    /// <summary>Builds a failed outcome that every other channel would hit too.</summary>
    /// <param name="reason">Why the capture produced nothing.</param>
    /// <returns>The outcome.</returns>
    public static FrameCapture Unreachable(string reason) => new(null, reason, true);
}
