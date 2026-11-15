using System.Buffers;

namespace HtspTuner.LiveTv;

/// <summary>Captures a bounded sample from an existing live-stream ring.</summary>
internal static class StreamSample
{
    /// <summary>Writes at most <paramref name="maxBytes"/> and returns partial data when the capture budget expires.</summary>
    /// <param name="ring">The already-running stream; no additional subscription is opened.</param>
    /// <param name="path">The output file, closed before returning.</param>
    /// <param name="maxBytes">The sample byte limit.</param>
    /// <param name="ct">The caller's cancellation token; cancellation always propagates.</param>
    /// <param name="maxDuration">An optional capture-only budget, independent of caller cancellation.</param>
    /// <returns>The number of bytes written.</returns>
    internal static async Task<int> CaptureAsync(StreamRing ring, string path, int maxBytes, CancellationToken ct, TimeSpan? maxDuration = null)
    {
        var total = 0;
        using var captureTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (maxDuration is { } duration)
        {
            captureTimeout.CancelAfter(duration);
        }

        using var reader = ring.OpenReader();
        await using var file = File.Create(path);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (total < maxBytes)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maxBytes - total)), captureTimeout.Token).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                // Only caller cancellation can interrupt a write: capture expiry must leave a usable file.
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                total += read;
            }

            ct.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (maxDuration.HasValue && !ct.IsCancellationRequested)
        {
            // The capture budget expired. Probe the finite sample instead of discarding it.
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return total;
    }
}
