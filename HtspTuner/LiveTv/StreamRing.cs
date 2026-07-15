using System.IO;

namespace HtspTuner.LiveTv;

/// <summary>
/// A bounded, in-memory, single-writer / many-reader byte ring for one live channel.
/// </summary>
/// <remarks>
/// Live TV, so the writer never blocks: when a reader falls more than the capacity behind, its cursor is
/// snapped forward to the oldest still-buffered byte and that gap is lost. This is deliberately not a
/// <see cref="System.IO.Pipelines.Pipe"/> (single reader, blocks the writer) nor a growing temp file
/// (unbounded disk). Each Jellyfin consumer of a shared channel opens its own <see cref="OpenReader"/>
/// cursor, which is what makes stream sharing across clients work.
/// </remarks>
internal sealed class StreamRing : IDisposable
{
    private readonly byte[] _buffer;

    // A classic monitor object: it guards the state AND backs the reader wait/writer pulse. It must be
    // a plain object (not System.Threading.Lock), because Monitor.Wait/PulseAll need this exact monitor.
    private readonly object _gate = new();
    private long _written;          // total bytes ever written (monotonic)
    private long _syncPoint = -1;   // offset of the most recent decoder entry point (PAT/PMT + key frame)
    private bool _completed;

    /// <summary>Initializes a new instance of the <see cref="StreamRing"/> class.</summary>
    /// <param name="capacityBytes">The ring capacity in bytes; clamped to a sane minimum.</param>
    public StreamRing(long capacityBytes)
    {
        _buffer = new byte[Math.Max(capacityBytes, 1 << 20)];
    }

    /// <summary>Gets the total number of bytes ever written to the ring.</summary>
    public long TotalWritten
    {
        get
        {
            lock (_gate)
            {
                return _written;
            }
        }
    }

    /// <summary>Appends data, overwriting the oldest bytes once full. Never blocks.</summary>
    /// <param name="data">The bytes to append.</param>
    public void Write(ReadOnlySpan<byte> data)
    {
        // Only the last capacity bytes can ever survive, so a giant write need only keep its tail.
        if (data.Length > _buffer.Length)
        {
            data = data[^_buffer.Length..];
        }

        lock (_gate)
        {
            var start = (int)(_written % _buffer.Length);
            var first = Math.Min(data.Length, _buffer.Length - start);
            data[..first].CopyTo(_buffer.AsSpan(start));
            data[first..].CopyTo(_buffer.AsSpan(0));
            _written += data.Length;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// Marks the current position as a decoder entry point (about to write PAT/PMT and a key frame).
    /// A reader that joins here can start decoding cleanly instead of mid-GOP.
    /// </summary>
    public void MarkSyncPoint()
    {
        lock (_gate)
        {
            _syncPoint = _written;
        }
    }

    /// <summary>Marks the stream finished; readers drain the remainder then see end-of-stream.</summary>
    public void Complete()
    {
        lock (_gate)
        {
            _completed = true;
            Monitor.PulseAll(_gate);
        }
    }

    /// <inheritdoc/>
    public void Dispose() => Complete();

    /// <summary>Opens an independent reader positioned at the most recent decoder entry point.</summary>
    /// <returns>A read-only, forward-only stream over the ring.</returns>
    public Stream OpenReader() => new RingReader(this);

    // The offset a fresh reader should start at: the last key-frame sync point if it is still buffered,
    // otherwise the oldest buffered byte. Either way the reader begins where a decoder can lock on.
    private long NewReaderOffset()
    {
        lock (_gate)
        {
            var oldest = Math.Max(0, _written - _buffer.Length);
            return _syncPoint >= oldest ? _syncPoint : oldest;
        }
    }

    private int ReadInto(ref long cursor, Span<byte> dest, CancellationToken ct)
    {
        lock (_gate)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var oldest = Math.Max(0, _written - _buffer.Length);
                if (cursor < oldest)
                {
                    cursor = oldest;   // fell behind: drop the gap, jump to oldest live byte
                }

                var available = _written - cursor;
                if (available > 0)
                {
                    var n = (int)Math.Min(available, dest.Length);
                    var start = (int)(cursor % _buffer.Length);
                    var first = Math.Min(n, _buffer.Length - start);
                    _buffer.AsSpan(start, first).CopyTo(dest);
                    _buffer.AsSpan(0, n - first).CopyTo(dest[first..]);
                    cursor += n;
                    return n;
                }

                if (_completed)
                {
                    return 0;
                }

                Monitor.Wait(_gate, 250);
            }
        }
    }

    private sealed class RingReader : Stream
    {
        private readonly StreamRing _ring;
        private long _cursor;

        public RingReader(StreamRing ring)
        {
            _ring = ring;
            // Start at the most recent key-frame boundary so the decoder locks on immediately.
            _cursor = ring.NewReaderOffset();
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _cursor;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
            => _ring.ReadInto(ref _cursor, buffer.AsSpan(offset, count), CancellationToken.None);

        public override int Read(Span<byte> buffer)
            => _ring.ReadInto(ref _cursor, buffer, CancellationToken.None);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
