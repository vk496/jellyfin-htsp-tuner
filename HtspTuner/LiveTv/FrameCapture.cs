namespace HtspTuner.LiveTv;

/// <summary>The outcome of one attempt to grab a still frame from a channel.</summary>
/// <param name="Path">The captured image, which the caller owns and must delete, or null on failure.</param>
/// <param name="Error">Why there is no image, or null on success.</param>
internal readonly record struct FrameCapture(string? Path, string? Error)
{
    /// <summary>Builds a failed outcome.</summary>
    /// <param name="reason">Why the capture produced nothing.</param>
    /// <returns>The outcome.</returns>
    public static FrameCapture Failed(string reason) => new(null, reason);
}
