using System.Buffers;
using HtspTuner.Htsp;

namespace HtspTuner.Mpeg;

/// <summary>
/// One elementary stream as it appears in the emitted MPEG-TS: its source, PID and PMT stream_type.
/// </summary>
/// <remarks><see cref="TsIndex"/> is the 0-based order in the PMT, which is the index Jellyfin's
/// ffmpeg assigns — the value <c>MediaStream.Index</c> must match, not Tvheadend's stream index.</remarks>
internal sealed class TsStreamInfo
{
    /// <summary>Gets the source HTSP stream.</summary>
    public required HtspStream Source { get; init; }

    /// <summary>Gets the transport PID.</summary>
    public required int Pid { get; init; }

    /// <summary>Gets the PMT stream_type.</summary>
    public required byte StreamType { get; init; }

    /// <summary>Gets the PES stream_id.</summary>
    public required byte StreamId { get; init; }

    /// <summary>Gets the 0-based position in the PMT / emitted TS.</summary>
    public required int TsIndex { get; init; }

    /// <summary>Gets or sets the running continuity counter for this PID.</summary>
    public int ContinuityCounter { get; set; }
}

/// <summary>
/// Remuxes HTSP elementary-stream packets into a single-program MPEG-TS that ffmpeg can consume.
/// </summary>
/// <remarks>
/// Timestamps arrive already in the 90 kHz TS timebase (the subscription uses <c>90khz=1</c>), so there
/// is no rescaling. PAT/PMT are emitted up front and before every video key frame so a decoder can lock
/// on quickly — part of keeping channel changes fast.
/// </remarks>
internal sealed class TsMuxer
{
    private const int PacketSize = 188;
    private const int PatPid = 0x0000;
    private const int PmtPid = 0x1000;
    private const int FirstEsPid = 0x0100;

    // MPEG-4 sampling-frequency table; Tvheadend's `rate` field is an index into this, not a value in Hz.
    private static readonly int[] _samplingFrequencies =
    {
        96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050,
        16000, 12000, 11025, 8000, 7350, 0, 0, 0,
    };

    private readonly List<TsStreamInfo> _streams = new();
    private readonly int _pcrPid;
    private int _patCc;
    private int _pmtCc;
    private int _sinceHeaders;

    /// <summary>Initializes a new instance of the <see cref="TsMuxer"/> class.</summary>
    /// <param name="start">The subscription's stream table.</param>
    public TsMuxer(HtspSubscriptionStart start)
    {
        int pid = FirstEsPid, tsIndex = 0, videoCount = 0, audioCount = 0;
        foreach (var s in start.Streams)
        {
            if (MapStreamType(s.Codec) is not { } streamType)
            {
                continue; // TEXTSUB / PCR / unknown carry no TS payload
            }

            _streams.Add(new TsStreamInfo
            {
                Source = s,
                Pid = pid++,
                StreamType = streamType,
                StreamId = StreamIdFor(s, ref videoCount, ref audioCount),
                TsIndex = tsIndex++,
            });
        }

        // PCR rides the first video stream, or the first audio stream on a radio channel (no video).
        var pcrStream = _streams.FirstOrDefault(s => s.Source.IsVideo)
                        ?? _streams.FirstOrDefault(s => s.Source.IsAudio)
                        ?? _streams.FirstOrDefault();
        _pcrPid = pcrStream?.Pid ?? 0x1FFF;
    }

    /// <summary>Gets the streams that are actually muxed, in PMT order.</summary>
    public IReadOnlyList<TsStreamInfo> Streams => _streams;

    /// <summary>Writes the PAT and PMT. Called at start and periodically thereafter.</summary>
    /// <param name="output">The destination.</param>
    public void WriteHeaders(IBufferWriter<byte> output)
    {
        WriteSection(output, PatPid, ref _patCc, BuildPat());
        WriteSection(output, PmtPid, ref _pmtCc, BuildPmt());
        _sinceHeaders = 0;
    }

    /// <summary>Muxes one HTSP packet into TS packets.</summary>
    /// <param name="packet">The elementary-stream packet.</param>
    /// <param name="output">The destination.</param>
    public void WritePacket(HtspMuxPacket packet, IBufferWriter<byte> output)
    {
        var s = _streams.FirstOrDefault(x => x.Source.Index == packet.StreamIndex);
        if (s is null)
        {
            return; // packet for a stream we dropped, or an unknown index
        }

        // Refresh the tables before a key frame (fast lock-on) and as a periodic fallback for audio-only.
        if ((packet.IsKeyFrame && _sinceHeaders > 0) || _sinceHeaders > 500)
        {
            WriteHeaders(output);
        }

        var payload = s.Source.Codec == HtspCodec.Aac ? AddAdtsIfNeeded(s.Source, packet.Payload) : packet.Payload;
        var pes = BuildPes(s, payload, packet.Pts, packet.Dts);
        long? pcr = s.Pid == _pcrPid ? packet.Dts ?? packet.Pts : null;
        EmitPes(output, s, pes, pcr, packet.IsKeyFrame);
        _sinceHeaders++;
    }

    private static byte? MapStreamType(HtspCodec codec) => codec switch
    {
        HtspCodec.H264 => 0x1B,
        HtspCodec.Hevc => 0x24,
        HtspCodec.Mpeg2Video => 0x02,
        HtspCodec.Mpeg2Audio => 0x03,
        HtspCodec.Aac => 0x0F,
        HtspCodec.AacLatm => 0x11,
        HtspCodec.Ac3 => 0x06,
        HtspCodec.Eac3 => 0x06,
        HtspCodec.Vorbis => 0x06,
        HtspCodec.DvbSub => 0x06,
        HtspCodec.Teletext => 0x06,
        _ => null,
    };

    private static byte StreamIdFor(HtspStream s, ref int videoCount, ref int audioCount) => s.Codec switch
    {
        HtspCodec.H264 or HtspCodec.Hevc or HtspCodec.Mpeg2Video => (byte)(0xE0 + Math.Min(videoCount++, 0x0F)),
        HtspCodec.Mpeg2Audio or HtspCodec.Aac or HtspCodec.AacLatm => (byte)(0xC0 + Math.Min(audioCount++, 0x1F)),
        _ => 0xBD, // private_stream_1: AC3/EAC3/DVB subtitle/teletext
    };

    // ---- PSI ----------------------------------------------------------------

    private static byte[] BuildPat()
    {
        // section body after section_length: tsid(2) flags(1) sec(1) last(1) program(4) crc(4) = 13
        Span<byte> b = stackalloc byte[8];
        b[0] = 0x00;                    // table_id
        b[1] = 0xB0; b[2] = 0x0D;       // syntax + length 13
        b[3] = 0x00; b[4] = 0x01;       // transport_stream_id
        b[5] = 0xC1;                    // version 0, current
        b[6] = 0x00; b[7] = 0x00;       // section / last section
        Span<byte> prog = stackalloc byte[4];
        prog[0] = 0x00; prog[1] = 0x01; // program_number 1
        prog[2] = (byte)(0xE0 | (PmtPid >> 8));
        prog[3] = (byte)(PmtPid & 0xFF);
        return Finish(b, prog);
    }

    private byte[] BuildPmt()
    {
        var body = new List<byte>
        {
            0x02,             // table_id
            0x00, 0x00,       // section_length placeholder
            0x00, 0x01,       // program_number
            0xC1,             // version 0, current
            0x00, 0x00,       // section / last section
            (byte)(0xE0 | (_pcrPid >> 8)), (byte)(_pcrPid & 0xFF),
            0xF0, 0x00,       // program_info_length 0
        };

        foreach (var s in _streams)
        {
            var desc = Descriptors(s.Source);
            body.Add(s.StreamType);
            body.Add((byte)(0xE0 | (s.Pid >> 8)));
            body.Add((byte)(s.Pid & 0xFF));
            body.Add((byte)(0xF0 | (desc.Length >> 8)));
            body.Add((byte)(desc.Length & 0xFF));
            body.AddRange(desc);
        }

        // section_length spans program_number .. CRC inclusive: (body after the 3-byte header) + 4 (CRC).
        var length = body.Count - 3 + 4;
        body[1] = (byte)(0xB0 | (length >> 8));
        body[2] = (byte)(length & 0xFF);
        var arr = body.ToArray();
        var crc = Crc32(arr);
        return arr.Concat(new[]
        {
            (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc,
        }).ToArray();
    }

    private static byte[] Finish(ReadOnlySpan<byte> header, ReadOnlySpan<byte> payload)
    {
        var body = new byte[header.Length + payload.Length];
        header.CopyTo(body);
        payload.CopyTo(body.AsSpan(header.Length));
        var crc = Crc32(body);
        return body.Concat(new[]
        {
            (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc,
        }).ToArray();
    }

    private byte[] Descriptors(HtspStream s)
    {
        var d = new List<byte>();
        var lang = LanguageBytes(s.Language);

        switch (s.Codec)
        {
            case HtspCodec.Ac3:
                d.AddRange(new byte[] { 0x6A, 0x00 }); // AC-3_descriptor (empty)
                break;
            case HtspCodec.Eac3:
                d.AddRange(new byte[] { 0x7A, 0x01, 0x00 }); // enhanced_AC-3_descriptor
                break;
            case HtspCodec.DvbSub:
                // subtitling_descriptor: lang(3) type(1) composition(2) ancillary(2)
                d.Add(0x59);
                d.Add(8);
                d.AddRange(lang);
                d.Add(0x10); // subtitling_type: normal
                d.Add((byte)((s.CompositionId ?? 0) >> 8));
                d.Add((byte)((s.CompositionId ?? 0) & 0xFF));
                d.Add((byte)((s.AncillaryId ?? 0) >> 8));
                d.Add((byte)((s.AncillaryId ?? 0) & 0xFF));
                break;
            case HtspCodec.Teletext:
                // teletext_descriptor: lang(3) type/magazine(1) page(1)
                d.Add(0x56);
                d.Add(5);
                d.AddRange(lang);
                d.Add(0x08); // teletext_type 1 (initial page), magazine 0
                d.Add(0x00);
                break;
        }

        if (s.IsAudio)
        {
            // ISO_639_language_descriptor: lang(3) + audio_type(1)
            d.Add(0x0A);
            d.Add(4);
            d.AddRange(lang);
            d.Add((byte)s.AudioType);
        }

        return d.ToArray();
    }

    private static byte[] LanguageBytes(string? lang)
    {
        var b = new byte[] { 0x75, 0x6E, 0x64 }; // "und"
        if (!string.IsNullOrEmpty(lang))
        {
            for (var i = 0; i < 3 && i < lang.Length; i++)
            {
                b[i] = (byte)lang[i];
            }
        }

        return b;
    }

    // ---- PES + TS packetisation --------------------------------------------

    private static byte[] BuildPes(TsStreamInfo s, ReadOnlySpan<byte> payload, long? pts, long? dts)
    {
        // DTS is only meaningful alongside PTS; collapse DTS==PTS to a single timestamp.
        var hasPts = pts.HasValue;
        var hasDts = dts.HasValue && pts.HasValue && dts.Value != pts.Value;
        var tsBytes = (hasPts ? 5 : 0) + (hasDts ? 5 : 0);

        var header = new byte[9 + tsBytes];
        header[0] = 0x00; header[1] = 0x00; header[2] = 0x01;
        header[3] = s.StreamId;

        // Length field: 0 = unbounded (video); otherwise flags(1)+hdrlen(1)+ts+payload, if it fits 16 bits.
        var pesLen = s.Source.IsVideo ? 0 : 3 + tsBytes + payload.Length;
        if (pesLen > 0xFFFF)
        {
            pesLen = 0;
        }

        header[4] = (byte)(pesLen >> 8);
        header[5] = (byte)(pesLen & 0xFF);
        // marker bits '10', plus data_alignment_indicator for video: each muxpkt is one access unit and
        // the PES starts on its boundary, so tell the decoder it can align on this PES.
        header[6] = s.Source.IsVideo ? (byte)0x84 : (byte)0x80;
        header[7] = (byte)((hasPts ? 0x80 : 0) | (hasDts ? 0x40 : 0));
        header[8] = (byte)tsBytes;

        if (hasPts)
        {
            WriteTimestamp(header.AsSpan(9), pts!.Value, hasDts ? 0x3 : 0x2);
        }

        if (hasDts)
        {
            WriteTimestamp(header.AsSpan(14), dts!.Value, 0x1);
        }

        var pes = new byte[header.Length + payload.Length];
        header.CopyTo(pes, 0);
        payload.CopyTo(pes.AsSpan(header.Length));
        return pes;
    }

    private static void WriteTimestamp(Span<byte> b, long ts, int guard)
    {
        ts &= 0x1FFFFFFFF; // 33 bits
        b[0] = (byte)((guard << 4) | (int)((ts >> 29) & 0x0E) | 0x01);
        b[1] = (byte)((ts >> 22) & 0xFF);
        b[2] = (byte)(((ts >> 14) & 0xFE) | 0x01);
        b[3] = (byte)((ts >> 7) & 0xFF);
        b[4] = (byte)(((ts << 1) & 0xFE) | 0x01);
    }

    private void EmitPes(IBufferWriter<byte> output, TsStreamInfo s, byte[] pes, long? pcrBase, bool keyFrame)
    {
        var off = 0;
        var first = true;
        while (off < pes.Length)
        {
            var span = output.GetSpan(PacketSize)[..PacketSize];
            span.Clear();
            span[0] = 0x47;
            var pid = s.Pid;
            span[1] = (byte)(((pid >> 8) & 0x1F) | (first ? 0x40 : 0));
            span[2] = (byte)(pid & 0xFF);

            var remaining = pes.Length - off;
            var wantPcr = first && pcrBase.HasValue;
            // random_access_indicator marks the packet that starts a key-frame access unit, so a player
            // tuning into the live stream knows where it may begin decoding. Without it, some decoders --
            // notably HEVC, where a CRA key frame is followed by undecodable RASL leading pictures -- never
            // find a start point and sit on a black screen. H.264's clean IDR is more forgiving.
            var wantRai = first && keyFrame;
            var needAf = wantPcr || wantRai || remaining < 184;

            int payloadLen;
            if (!needAf)
            {
                span[3] = (byte)(0x10 | (s.ContinuityCounter & 0x0F));
                payloadLen = 184;
                pes.AsSpan(off, payloadLen).CopyTo(span[4..]);
            }
            else
            {
                span[3] = (byte)(0x30 | (s.ContinuityCounter & 0x0F));
                var pcrBytes = wantPcr ? 6 : 0;
                var room = 183 - (1 + pcrBytes);       // payload room after af_length + flags + pcr
                payloadLen = Math.Min(remaining, room);
                var stuffing = room - payloadLen;
                span[4] = (byte)(1 + pcrBytes + stuffing); // adaptation_field_length
                span[5] = (byte)((wantPcr ? 0x10 : 0x00) | (wantRai ? 0x40 : 0x00)); // PCR + random-access flags
                var p = 6;
                if (wantPcr)
                {
                    WritePcr(span.Slice(p, 6), pcrBase!.Value);
                    p += 6;
                }

                for (var i = 0; i < stuffing; i++)
                {
                    span[p++] = 0xFF;
                }

                pes.AsSpan(off, payloadLen).CopyTo(span[p..]);
            }

            output.Advance(PacketSize);
            s.ContinuityCounter = (s.ContinuityCounter + 1) & 0x0F;
            off += payloadLen;
            first = false;
        }
    }

    private static void WritePcr(Span<byte> b, long base90k)
    {
        var pcrBase = base90k & 0x1FFFFFFFF; // 33 bits, extension left at 0
        b[0] = (byte)(pcrBase >> 25);
        b[1] = (byte)(pcrBase >> 17);
        b[2] = (byte)(pcrBase >> 9);
        b[3] = (byte)(pcrBase >> 1);
        b[4] = (byte)(((pcrBase & 0x1) << 7) | 0x7E); // 1 bit base + 6 reserved '1'
        b[5] = 0x00;
    }

    private void WriteSection(IBufferWriter<byte> output, int pid, ref int cc, byte[] section)
    {
        var span = output.GetSpan(PacketSize)[..PacketSize];
        span.Fill(0xFF);
        span[0] = 0x47;
        span[1] = (byte)(0x40 | ((pid >> 8) & 0x1F)); // PUSI
        span[2] = (byte)(pid & 0xFF);
        span[3] = (byte)(0x10 | (cc & 0x0F));
        span[4] = 0x00; // pointer_field
        section.CopyTo(span[5..]);
        output.Advance(PacketSize);
        cc = (cc + 1) & 0x0F;
    }

    private byte[] AddAdtsIfNeeded(HtspStream s, byte[] aac)
    {
        // Tvheadend sends raw AAC; stream_type 0x0F needs ADTS framing. Skip if already framed.
        if (aac.Length >= 2 && aac[0] == 0xFF && (aac[1] & 0xF0) == 0xF0)
        {
            return aac;
        }

        var sampleRate = s.SampleRate ?? _samplingFrequencies[3];
        var sri = Array.IndexOf(_samplingFrequencies, sampleRate);
        if (sri < 0)
        {
            sri = 3; // 48 kHz
        }

        var channels = Math.Clamp(s.Channels ?? 2, 1, 7);
        var frameLen = aac.Length + 7;
        var adts = new byte[frameLen];
        adts[0] = 0xFF;
        adts[1] = 0xF1;                                    // MPEG-4, no CRC
        adts[2] = (byte)((0x01 << 6) | (sri << 2) | (channels >> 2)); // profile AAC-LC
        adts[3] = (byte)(((channels & 0x3) << 6) | (frameLen >> 11));
        adts[4] = (byte)((frameLen >> 3) & 0xFF);
        adts[5] = (byte)(((frameLen & 0x7) << 5) | 0x1F);
        adts[6] = 0xFC;
        aac.CopyTo(adts, 7);
        return adts;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= (uint)b << 24;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 0x80000000u) != 0 ? (crc << 1) ^ 0x04C11DB7u : crc << 1;
            }
        }

        return crc;
    }
}
