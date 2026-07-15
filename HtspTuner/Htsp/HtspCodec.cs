using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace HtspTuner.Htsp;

/// <summary>
/// The htsmsg binary codec: the wire format Tvheadend speaks over HTSP.
/// </summary>
/// <remarks>
/// <para>
/// A frame is a big-endian <c>u32</c> length followed by that many bytes of body. A body is a
/// flat sequence of fields; each field is <c>u8 type | u8 namelen | u32be datalen | name | data</c>.
/// A field's value is a signed 64-bit integer, a UTF-8 string, opaque binary, a boolean, a nested
/// map (its own field sequence) or a list (fields whose names are empty).
/// </para>
/// <para>
/// The one subtlety that has historically broken plugins is the S64 encoding. A positive value is
/// stored as minimal-length little-endian with <em>only trailing</em> (most-significant) zero bytes
/// removed — interior zero bytes must be preserved, so 65536 encodes as <c>00 00 01</c>, never
/// <c>00 01</c>. Zero encodes as no bytes at all. A negative value is stored as the full eight-byte
/// little-endian two's-complement form. Decoding accumulates the little-endian bytes into a
/// <see cref="ulong"/> and reinterprets the result as a <see cref="long"/>.
/// </para>
/// </remarks>
internal static class HtsmsgCodec
{
    /// <summary>
    /// The largest frame the reader will accept. Guards <see cref="TryReadFrame"/> against an
    /// attacker-controlled length prefix that would otherwise make us buffer gigabytes.
    /// </summary>
    private const uint MaxFrameSize = 32 * 1024 * 1024;

    private const byte TypeMap = 1;
    private const byte TypeInt = 2;
    private const byte TypeStr = 3;
    private const byte TypeBin = 4;
    private const byte TypeList = 5;
    private const byte TypeBool = 7;

    private const int FieldHeaderSize = 6; // type(1) + namelen(1) + datalen(4)

    /// <summary>Serializes a message to a complete frame: a 4-byte big-endian length, then the body.</summary>
    /// <param name="msg">The message to serialize.</param>
    /// <returns>The framed bytes, ready to write to the socket.</returns>
    public static byte[] Serialize(HtspMessage msg)
    {
        ArgumentNullException.ThrowIfNull(msg);
        var writer = new ArrayBufferWriter<byte>();
        Write(msg, writer);
        return writer.WrittenSpan.ToArray();
    }

    /// <summary>Writes a complete frame (length prefix and body) to <paramref name="output"/>.</summary>
    /// <param name="msg">The message to write.</param>
    /// <param name="output">The destination buffer.</param>
    public static void Write(HtspMessage msg, IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(msg);
        ArgumentNullException.ThrowIfNull(output);

        int bodyLength = MeasureBody(msg.Fields);

        Span<byte> lengthSpan = output.GetSpan(4);
        BinaryPrimitives.WriteUInt32BigEndian(lengthSpan, (uint)bodyLength);
        output.Advance(4);

        WriteBody(msg.Fields, output);
    }

    /// <summary>Decodes a frame body — the bytes <em>after</em> the length prefix — into a message.</summary>
    /// <param name="body">The frame body.</param>
    /// <returns>The decoded message.</returns>
    /// <exception cref="HtspProtocolException">The bytes are not a valid htsmsg body.</exception>
    public static HtspMessage Deserialize(ReadOnlySpan<byte> body)
    {
        var msg = new HtspMessage();
        ParseInto(body, msg);
        return msg;
    }

    /// <summary>
    /// Tries to pull one complete frame off the front of a <c>PipeReader</c> buffer.
    /// </summary>
    /// <param name="buffer">
    /// The buffered bytes. On success it is advanced past the consumed frame; on failure it is left
    /// untouched so the caller can wait for more data.
    /// </param>
    /// <param name="msg">The decoded message, when a whole frame was available.</param>
    /// <returns><c>true</c> when a frame was decoded; <c>false</c> when more bytes are needed.</returns>
    /// <exception cref="HtspProtocolException">
    /// The length prefix exceeds <see cref="MaxFrameSize"/>, or the framed bytes are malformed.
    /// </exception>
    public static bool TryReadFrame(ref ReadOnlySequence<byte> buffer, out HtspMessage? msg)
    {
        msg = null;

        if (buffer.Length < 4)
        {
            return false;
        }

        Span<byte> lengthSpan = stackalloc byte[4];
        buffer.Slice(0, 4).CopyTo(lengthSpan);
        uint length = BinaryPrimitives.ReadUInt32BigEndian(lengthSpan);

        if (length > MaxFrameSize)
        {
            throw new HtspProtocolException(
                $"HTSP frame length {length} exceeds the {MaxFrameSize}-byte limit.");
        }

        if (buffer.Length < 4L + length)
        {
            return false;
        }

        ReadOnlySequence<byte> bodySeq = buffer.Slice(4, length);
        if (bodySeq.IsSingleSegment)
        {
            msg = Deserialize(bodySeq.FirstSpan);
        }
        else
        {
            // Bounded by MaxFrameSize and already fully buffered, so this is not an
            // attacker-controlled unbounded allocation.
            byte[] contiguous = bodySeq.ToArray();
            msg = Deserialize(contiguous);
        }

        buffer = buffer.Slice(4L + length);
        return true;
    }

    /// <summary>Computes the wire length of a positive/zero/negative S64 value.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The number of data bytes the value occupies.</returns>
    internal static int EncodedIntLength(long value)
    {
        if (value == 0)
        {
            return 0;
        }

        if (value < 0)
        {
            return 8;
        }

        int length = 0;
        ulong u = (ulong)value;
        while (u != 0)
        {
            u >>= 8;
            length++;
        }

        return length;
    }

    private static int MeasureBody(IReadOnlyList<KeyValuePair<string, object>> fields)
    {
        int total = 0;
        for (int i = 0; i < fields.Count; i++)
        {
            int nameBytes = Encoding.UTF8.GetByteCount(fields[i].Key);
            if (nameBytes > byte.MaxValue)
            {
                throw new HtspProtocolException(
                    $"HTSP field name is {nameBytes} bytes; the wire format allows at most 255.");
            }

            total += FieldHeaderSize + nameBytes + MeasureValue(fields[i].Value);
        }

        return total;
    }

    private static int MeasureValue(object value)
    {
        switch (value)
        {
            case long l:
                return EncodedIntLength(l);
            case int i:
                return EncodedIntLength(i);
            case bool b:
                return b ? 1 : 0;
            case string s:
                return Encoding.UTF8.GetByteCount(s);
            case byte[] bytes:
                return bytes.Length;
            case HtspMessage map:
                return MeasureBody(map.Fields);
            case List<object> list:
                int total = 0;
                for (int n = 0; n < list.Count; n++)
                {
                    // List elements carry an empty name.
                    total += FieldHeaderSize + MeasureValue(list[n]);
                }

                return total;
            default:
                throw new HtspProtocolException(
                    $"Cannot serialize a value of type {value?.GetType().FullName ?? "null"}.");
        }
    }

    private static void WriteBody(IReadOnlyList<KeyValuePair<string, object>> fields, IBufferWriter<byte> output)
    {
        for (int i = 0; i < fields.Count; i++)
        {
            WriteField(fields[i].Key, fields[i].Value, output);
        }
    }

    private static void WriteField(string name, object value, IBufferWriter<byte> output)
    {
        byte type = TypeOf(value);
        int dataLength = MeasureValue(value);
        int nameBytes = Encoding.UTF8.GetByteCount(name);

        Span<byte> header = output.GetSpan(FieldHeaderSize);
        header[0] = type;
        header[1] = (byte)nameBytes;
        BinaryPrimitives.WriteUInt32BigEndian(header.Slice(2, 4), (uint)dataLength);
        output.Advance(FieldHeaderSize);

        if (nameBytes > 0)
        {
            Span<byte> nameSpan = output.GetSpan(nameBytes);
            Encoding.UTF8.GetBytes(name, nameSpan);
            output.Advance(nameBytes);
        }

        WriteData(value, output);
    }

    private static void WriteData(object value, IBufferWriter<byte> output)
    {
        switch (value)
        {
            case long l:
                WriteInt(l, output);
                break;
            case int i:
                WriteInt(i, output);
                break;
            case bool b:
                if (b)
                {
                    Span<byte> one = output.GetSpan(1);
                    one[0] = 1;
                    output.Advance(1);
                }

                break;
            case string s:
                int sBytes = Encoding.UTF8.GetByteCount(s);
                if (sBytes > 0)
                {
                    Span<byte> sSpan = output.GetSpan(sBytes);
                    Encoding.UTF8.GetBytes(s, sSpan);
                    output.Advance(sBytes);
                }

                break;
            case byte[] bytes:
                if (bytes.Length > 0)
                {
                    output.Write(bytes);
                }

                break;
            case HtspMessage map:
                WriteBody(map.Fields, output);
                break;
            case List<object> list:
                for (int n = 0; n < list.Count; n++)
                {
                    WriteField(string.Empty, list[n], output);
                }

                break;
            default:
                throw new HtspProtocolException(
                    $"Cannot serialize a value of type {value?.GetType().FullName ?? "null"}.");
        }
    }

    private static void WriteInt(long value, IBufferWriter<byte> output)
    {
        if (value == 0)
        {
            return;
        }

        Span<byte> span = output.GetSpan(8);
        if (value < 0)
        {
            // Negative: full eight-byte little-endian two's complement.
            BinaryPrimitives.WriteUInt64LittleEndian(span, (ulong)value);
            output.Advance(8);
            return;
        }

        // Positive: minimal little-endian, keeping interior zeros, dropping only trailing zeros.
        int n = 0;
        ulong u = (ulong)value;
        while (u != 0)
        {
            span[n++] = (byte)(u & 0xFF);
            u >>= 8;
        }

        output.Advance(n);
    }

    private static byte TypeOf(object value) => value switch
    {
        long => TypeInt,
        int => TypeInt,
        bool => TypeBool,
        string => TypeStr,
        byte[] => TypeBin,
        HtspMessage => TypeMap,
        List<object> => TypeList,
        _ => throw new HtspProtocolException(
            $"Cannot serialize a value of type {value?.GetType().FullName ?? "null"}."),
    };

    private static void ParseInto(ReadOnlySpan<byte> span, HtspMessage msg)
    {
        int i = 0;
        while (i < span.Length)
        {
            (string name, object value, int next) = ReadField(span, i);
            msg.Add(name, value);
            i = next;
        }
    }

    private static List<object> ParseList(ReadOnlySpan<byte> span)
    {
        var list = new List<object>();
        int i = 0;
        while (i < span.Length)
        {
            (_, object value, int next) = ReadField(span, i);
            list.Add(value);
            i = next;
        }

        return list;
    }

    private static (string Name, object Value, int Next) ReadField(ReadOnlySpan<byte> span, int offset)
    {
        if (span.Length - offset < FieldHeaderSize)
        {
            throw new HtspProtocolException("Truncated htsmsg field header.");
        }

        byte type = span[offset];
        int nameLen = span[offset + 1];
        uint dataLen = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(offset + 2, 4));
        int cursor = offset + FieldHeaderSize;

        int remaining = span.Length - cursor;
        if (nameLen > remaining)
        {
            throw new HtspProtocolException("Truncated htsmsg field name.");
        }

        string name = nameLen == 0
            ? string.Empty
            : Encoding.UTF8.GetString(span.Slice(cursor, nameLen));
        cursor += nameLen;

        remaining = span.Length - cursor;
        if (dataLen > (uint)remaining)
        {
            throw new HtspProtocolException(
                $"htsmsg field claims {dataLen} data bytes but only {remaining} remain.");
        }

        ReadOnlySpan<byte> data = span.Slice(cursor, (int)dataLen);
        cursor += (int)dataLen;

        object value = ReadValue(type, data);
        return (name, value, cursor);
    }

    private static object ReadValue(byte type, ReadOnlySpan<byte> data)
    {
        switch (type)
        {
            case TypeMap:
                var nested = new HtspMessage();
                ParseInto(data, nested);
                return nested;
            case TypeList:
                return ParseList(data);
            case TypeInt:
                return ReadInt(data);
            case TypeStr:
                return data.IsEmpty ? string.Empty : Encoding.UTF8.GetString(data);
            case TypeBin:
                return data.ToArray();
            case TypeBool:
                return !data.IsEmpty && data[0] != 0;
            default:
                throw new HtspProtocolException($"Unknown htsmsg field type tag {type}.");
        }
    }

    private static long ReadInt(ReadOnlySpan<byte> data)
    {
        if (data.Length > 8)
        {
            throw new HtspProtocolException(
                $"htsmsg S64 field is {data.Length} bytes; the maximum is 8.");
        }

        ulong u = 0;
        for (int i = 0; i < data.Length; i++)
        {
            u |= (ulong)data[i] << (8 * i);
        }

        return (long)u;
    }
}
