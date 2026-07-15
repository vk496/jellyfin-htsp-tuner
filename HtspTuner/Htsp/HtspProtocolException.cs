namespace HtspTuner.Htsp;

/// <summary>
/// Thrown when a byte stream cannot be decoded as a valid htsmsg frame.
/// </summary>
/// <remarks>
/// This is raised for every kind of malformed or hostile input the codec can meet on the wire:
/// a truncated field, a declared data length that runs off the end of the frame, an
/// integer field longer than eight bytes, an oversized frame length, or an unknown field
/// type tag. It never lets a raw <see cref="IndexOutOfRangeException"/>, an
/// <see cref="OverflowException"/>, or an unbounded allocation escape from the parser.
/// </remarks>
internal sealed class HtspProtocolException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="HtspProtocolException"/> class.</summary>
    public HtspProtocolException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="HtspProtocolException"/> class.</summary>
    /// <param name="message">A description of the decoding failure.</param>
    public HtspProtocolException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="HtspProtocolException"/> class.</summary>
    /// <param name="message">A description of the decoding failure.</param>
    /// <param name="innerException">The underlying failure, if any.</param>
    public HtspProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
