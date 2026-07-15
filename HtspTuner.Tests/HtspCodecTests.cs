using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using HtspTuner.Htsp;
using Xunit;

namespace HtspTuner.Tests;

/// <summary>Tests for the htsmsg binary codec.</summary>
public class HtspCodecTests
{
    // ---- helpers ---------------------------------------------------------

    /// <summary>Encodes a single int field and returns just its S64 data bytes.</summary>
    private static byte[] EncodeIntData(long value)
    {
        byte[] frame = HtsmsgCodec.Serialize(new HtspMessage().Add("v", value));

        // frame = [len:4][type:1][namelen:1][datalen:4be][name:"v"][data]
        int dataLen = (frame[6] << 24) | (frame[7] << 16) | (frame[8] << 8) | frame[9];
        Assert.Equal(1, frame[5]); // one-byte name
        const int DataStart = 4 + 6 + 1;
        return frame.AsSpan(DataStart, dataLen).ToArray();
    }

    /// <summary>Round-trips a single long through Serialize/Deserialize.</summary>
    private static long RoundTripInt(long value)
    {
        byte[] frame = HtsmsgCodec.Serialize(new HtspMessage().Add("v", value));
        HtspMessage decoded = HtsmsgCodec.Deserialize(frame.AsSpan(4));
        Assert.True(decoded.TryGet("v", out long got));
        return got;
    }

    /// <summary>Builds a frame body (no length prefix) from raw field bytes.</summary>
    private static byte[] Field(byte type, string name, byte[] data)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(name);
        byte[] buf = new byte[6 + nameBytes.Length + data.Length];
        buf[0] = type;
        buf[1] = (byte)nameBytes.Length;
        buf[2] = (byte)(data.Length >> 24);
        buf[3] = (byte)(data.Length >> 16);
        buf[4] = (byte)(data.Length >> 8);
        buf[5] = (byte)data.Length;
        nameBytes.CopyTo(buf, 6);
        data.CopyTo(buf, 6 + nameBytes.Length);
        return buf;
    }

    // ---- 1. round-trip + exact wire bytes --------------------------------

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(-1L)]
    [InlineData(127L)]
    [InlineData(128L)]
    [InlineData(255L)]
    [InlineData(256L)]
    [InlineData(65535L)]
    [InlineData(65536L)]
    [InlineData(65537L)]
    [InlineData(1L << 24)]
    [InlineData((1L << 24) + 1)]
    [InlineData((1L << 31) - 1)]
    [InlineData(1L << 31)]
    [InlineData(1L << 32)]
    [InlineData(1L << 53)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    [InlineData(-2L)]
    public void Int_RoundTrips(long value)
    {
        Assert.Equal(value, RoundTripInt(value));
    }

    [Fact]
    public void Int_ExactWireBytes_Zero()
    {
        Assert.Equal(Array.Empty<byte>(), EncodeIntData(0));
        Assert.Equal(0, HtsmsgCodec.EncodedIntLength(0));
    }

    [Fact]
    public void Int_ExactWireBytes_120()
    {
        Assert.Equal(new byte[] { 0x78 }, EncodeIntData(120));
        Assert.Equal(1, HtsmsgCodec.EncodedIntLength(120));
    }

    [Fact]
    public void Int_ExactWireBytes_MinusTwo()
    {
        Assert.Equal(
            new byte[] { 0xFE, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF },
            EncodeIntData(-2));
        Assert.Equal(8, HtsmsgCodec.EncodedIntLength(-2));
    }

    // ---- 2. the old 8-year-old interior-zero bug -------------------------

    [Fact]
    public void Int_65536_KeepsInteriorZero_RegressionForOldBug()
    {
        byte[] data = EncodeIntData(65536);

        // The old encoder stripped interior zeros too and produced [00 01] == 256.
        Assert.NotEqual(new byte[] { 0x00, 0x01 }, data);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x01 }, data);
        Assert.Equal(65536L, RoundTripInt(65536));
    }

    [Fact]
    public void Int_Fuzz_AllSmallPlusRandom()
    {
        for (long x = 0; x <= 200_000; x++)
        {
            Assert.Equal(x, RoundTripInt(x));
        }

        var rng = new Random(1234567);
        Span<byte> buf = stackalloc byte[8];
        for (int i = 0; i < 200_000; i++)
        {
            rng.NextBytes(buf);
            long x = BitConverter.ToInt64(buf);
            Assert.Equal(x, RoundTripInt(x));
        }
    }

    // ---- 3. duplicate field names ----------------------------------------

    [Fact]
    public void DuplicateFieldNames_Survive_RoundTrip()
    {
        var msg = new HtspMessage()
            .Add("meta", new byte[] { 0x11, 0x90 })
            .Add("meta", new byte[] { 0xAB, 0xCD, 0xEF })
            .Add("subscriptionId", 1L);

        byte[] frame = HtsmsgCodec.Serialize(msg);
        HtspMessage decoded = HtsmsgCodec.Deserialize(frame.AsSpan(4));

        byte[][] metas = decoded.GetAll("meta").Cast<byte[]>().ToArray();
        Assert.Equal(2, metas.Length);
        Assert.Equal(new byte[] { 0x11, 0x90 }, metas[0]);
        Assert.Equal(new byte[] { 0xAB, 0xCD, 0xEF }, metas[1]);
        Assert.Equal(1L, decoded.GetInt("subscriptionId"));
    }

    // ---- 4. nested maps and lists ----------------------------------------

    [Fact]
    public void NestedMapsAndLists_RoundTrip()
    {
        var stream0 = new HtspMessage().Add("index", 1L).Add("type", "H264").Add("width", 1920L);
        var stream1 = new HtspMessage().Add("index", 2L).Add("type", "EAC3").Add("channels", 6L);
        var sourceInfo = new HtspMessage().Add("mux", "MUX 1").Add("service", "Channel");

        var msg = new HtspMessage()
            .Add("method", "subscriptionStart")
            .Add("streams", new List<object> { stream0, stream1 })
            .Add("sourceinfo", sourceInfo)
            .Add("caps", new List<object> { "x", "yy", "zzz" })
            .Add("flags", true)
            .Add("off", false)
            .Add("subscriptionId", 7L);

        byte[] frame = HtsmsgCodec.Serialize(msg);
        HtspMessage d = HtsmsgCodec.Deserialize(frame.AsSpan(4));

        Assert.Equal("subscriptionStart", d.GetString("method"));
        Assert.Equal(7L, d.GetInt("subscriptionId"));
        Assert.True(d.GetBool("flags"));
        Assert.False(d.GetBool("off"));

        var streams = d.GetMapList("streams");
        Assert.Equal(2, streams.Count);
        Assert.Equal(1L, streams[0].GetInt("index"));
        Assert.Equal("H264", streams[0].GetString("type"));
        Assert.Equal(1920L, streams[0].GetInt("width"));
        Assert.Equal("EAC3", streams[1].GetString("type"));
        Assert.Equal(6L, streams[1].GetInt("channels"));

        Assert.Equal("MUX 1", d.GetMap("sourceinfo")!.GetString("mux"));
        Assert.Equal("Channel", d.GetMap("sourceinfo")!.GetString("service"));

        Assert.True(d.TryGet("caps", out List<object>? caps));
        Assert.Equal(new object[] { "x", "yy", "zzz" }, caps!.ToArray());
    }

    [Fact]
    public void BoolFalse_EncodesZeroLength()
    {
        byte[] frame = HtsmsgCodec.Serialize(new HtspMessage().Add("b", false));
        int dataLen = (frame[6] << 24) | (frame[7] << 16) | (frame[8] << 8) | frame[9];
        Assert.Equal(0, dataLen);

        byte[] tframe = HtsmsgCodec.Serialize(new HtspMessage().Add("b", true));
        int tDataLen = (tframe[6] << 24) | (tframe[7] << 16) | (tframe[8] << 8) | tframe[9];
        Assert.Equal(1, tDataLen);
    }

    // ---- 5. decode REAL captured bytes -----------------------------------

    private static string FixturePath(string name)
        => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    private static JsonElement LoadExpected()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(FixturePath("expected.json")));
        return doc.RootElement.Clone();
    }

    [Fact]
    public void Fixture_Hello_DecodesToPythonsValues()
    {
        byte[] frame = File.ReadAllBytes(FixturePath("hello.frame"));
        var seq = new ReadOnlySequence<byte>(frame);
        Assert.True(HtsmsgCodec.TryReadFrame(ref seq, out HtspMessage? msg));
        Assert.NotNull(msg);
        Assert.Equal(0, seq.Length); // whole frame consumed

        CompareFields(msg!.Fields, LoadExpected().GetProperty("hello"));
    }

    [Fact]
    public void Fixture_SubscriptionStart_DecodesToPythonsValues()
    {
        byte[] frame = File.ReadAllBytes(FixturePath("subscriptionStart.frame"));
        var seq = new ReadOnlySequence<byte>(frame);
        Assert.True(HtsmsgCodec.TryReadFrame(ref seq, out HtspMessage? msg));
        Assert.NotNull(msg);

        CompareFields(msg!.Fields, LoadExpected().GetProperty("subscriptionStart"));
    }

    [Fact]
    public void Fixture_SubscriptionStart_RoundTripsBitExact()
    {
        // Decode a real frame, re-encode it, and require identical bytes.
        byte[] frame = File.ReadAllBytes(FixturePath("subscriptionStart.frame"));
        HtspMessage decoded = HtsmsgCodec.Deserialize(frame.AsSpan(4));
        byte[] reencoded = HtsmsgCodec.Serialize(decoded);
        Assert.Equal(frame, reencoded);
    }

    [Fact]
    public void Fixture_ReadFrame_AcrossSegments()
    {
        // Split the real frame across three buffer segments to exercise the multi-segment path.
        byte[] frame = File.ReadAllBytes(FixturePath("subscriptionStart.frame"));
        var seq = ThreeSegments(frame);
        Assert.True(HtsmsgCodec.TryReadFrame(ref seq, out HtspMessage? msg));
        Assert.NotNull(msg);
        CompareFields(msg!.Fields, LoadExpected().GetProperty("subscriptionStart"));
    }

    private static void CompareFields(IReadOnlyList<KeyValuePair<string, object>> fields, JsonElement expected)
    {
        Assert.Equal(expected.GetArrayLength(), fields.Count);
        int i = 0;
        foreach (JsonElement pair in expected.EnumerateArray())
        {
            string name = pair[0].GetString()!;
            Assert.Equal(name, fields[i].Key);
            CompareValue(fields[i].Value, pair[1]);
            i++;
        }
    }

    private static void CompareValue(object actual, JsonElement val)
    {
        string tag = val[0].GetString()!;
        JsonElement payload = val[1];
        switch (tag)
        {
            case "I":
                Assert.Equal(payload.GetString(), ((long)actual).ToString(CultureInfo.InvariantCulture));
                break;
            case "S":
                Assert.Equal(payload.GetString(), (string)actual);
                break;
            case "B":
                Assert.Equal(payload.GetString(), Convert.ToHexString((byte[])actual).ToLowerInvariant());
                break;
            case "b":
                Assert.Equal(payload.GetBoolean(), (bool)actual);
                break;
            case "M":
                CompareFields(((HtspMessage)actual).Fields, payload);
                break;
            case "L":
                var list = (List<object>)actual;
                Assert.Equal(payload.GetArrayLength(), list.Count);
                int j = 0;
                foreach (JsonElement e in payload.EnumerateArray())
                {
                    CompareValue(list[j++], e);
                }

                break;
            default:
                throw new InvalidOperationException($"unexpected tag {tag}");
        }
    }

    // ---- 6. malformed / hostile input ------------------------------------

    [Fact]
    public void Malformed_TruncatedFieldHeader_Throws()
    {
        Assert.Throws<HtspProtocolException>(() => HtsmsgCodec.Deserialize(new byte[] { 3, 0, 0 }));
    }

    [Fact]
    public void Malformed_DataLenRunsOffEnd_Throws()
    {
        // str field, namelen 0, datalen 255, but no data present.
        byte[] body = { TypeStrTag, 0, 0x00, 0x00, 0x00, 0xFF };
        Assert.Throws<HtspProtocolException>(() => HtsmsgCodec.Deserialize(body));
    }

    [Fact]
    public void Malformed_AbsurdDataLen_DoesNotAllocate_Throws()
    {
        // datalen 0xFFFFFFFF with no data: must throw, never attempt a 4 GB allocation.
        byte[] body = { TypeBinTag, 0, 0xFF, 0xFF, 0xFF, 0xFF };
        Assert.Throws<HtspProtocolException>(() => HtsmsgCodec.Deserialize(body));
    }

    [Fact]
    public void Malformed_TruncatedName_Throws()
    {
        // namelen 5 but no name bytes follow.
        byte[] body = { TypeStrTag, 5, 0, 0, 0, 0 };
        Assert.Throws<HtspProtocolException>(() => HtsmsgCodec.Deserialize(body));
    }

    [Fact]
    public void Malformed_UnknownTypeTag_Throws()
    {
        byte[] body = Field(99, "x", Array.Empty<byte>());
        Assert.Throws<HtspProtocolException>(() => HtsmsgCodec.Deserialize(body));
    }

    [Fact]
    public void Malformed_DblTypeTag_Throws()
    {
        // Type 6 (Dbl) is never legal on this wire.
        byte[] body = Field(6, "x", new byte[8]);
        Assert.Throws<HtspProtocolException>(() => HtsmsgCodec.Deserialize(body));
    }

    [Fact]
    public void Malformed_S64TooLong_Throws()
    {
        byte[] body = Field(TypeIntTag, "v", new byte[9]);
        Assert.Throws<HtspProtocolException>(() => HtsmsgCodec.Deserialize(body));
    }

    [Fact]
    public void TryReadFrame_OversizedLength_Throws()
    {
        // Length prefix well past the 32 MiB cap; must reject without buffering.
        byte[] buf = { 0x7F, 0xFF, 0xFF, 0xFF, 0x00 };
        var seq = new ReadOnlySequence<byte>(buf);
        Assert.Throws<HtspProtocolException>(() =>
        {
            var local = seq;
            HtsmsgCodec.TryReadFrame(ref local, out _);
        });
    }

    [Fact]
    public void TryReadFrame_Incomplete_ReturnsFalseAndLeavesBuffer()
    {
        // Claims 10 body bytes but only 3 present.
        byte[] buf = { 0x00, 0x00, 0x00, 0x0A, 1, 2, 3 };
        var seq = new ReadOnlySequence<byte>(buf);
        Assert.False(HtsmsgCodec.TryReadFrame(ref seq, out HtspMessage? msg));
        Assert.Null(msg);
        Assert.Equal(7, seq.Length); // untouched
    }

    [Fact]
    public void TryReadFrame_ConsumesOneFrameLeavesRemainder()
    {
        byte[] first = HtsmsgCodec.Serialize(new HtspMessage().Add("a", 1L));
        byte[] second = HtsmsgCodec.Serialize(new HtspMessage().Add("b", 2L));
        byte[] both = first.Concat(second).ToArray();

        var seq = new ReadOnlySequence<byte>(both);
        Assert.True(HtsmsgCodec.TryReadFrame(ref seq, out HtspMessage? m1));
        Assert.Equal(1L, m1!.GetInt("a"));
        Assert.Equal(second.Length, seq.Length);

        Assert.True(HtsmsgCodec.TryReadFrame(ref seq, out HtspMessage? m2));
        Assert.Equal(2L, m2!.GetInt("b"));
        Assert.Equal(0, seq.Length);

        Assert.False(HtsmsgCodec.TryReadFrame(ref seq, out _));
    }

    private const byte TypeIntTag = 2;
    private const byte TypeStrTag = 3;
    private const byte TypeBinTag = 4;

    private static ReadOnlySequence<byte> ThreeSegments(byte[] data)
    {
        int a = data.Length / 3;
        int b = 2 * data.Length / 3;
        var s1 = new Seg(data.AsMemory(0, a));
        var s2 = s1.Append(data.AsMemory(a, b - a));
        var s3 = s2.Append(data.AsMemory(b));
        return new ReadOnlySequence<byte>(s1, 0, s3, s3.Memory.Length);
    }

    private sealed class Seg : ReadOnlySequenceSegment<byte>
    {
        public Seg(ReadOnlyMemory<byte> memory)
        {
            Memory = memory;
        }

        public Seg Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Seg(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }
}
