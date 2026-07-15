using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using HtspTuner.Htsp;
using HtspTuner.Mpeg;
using Xunit;

namespace HtspTuner.Tests;

/// <summary>
/// Drives the real golden HTSP captures through <see cref="TsMuxer"/> and checks with ffprobe that the
/// emitted MPEG-TS contains exactly the streams Tvheadend declared. This is the muxer's acceptance test.
/// </summary>
public class TsMuxerTests
{
    public static IEnumerable<object[]> Golden()
    {
        var dir = GoldenRoot();
        foreach (var d in Directory.GetDirectories(dir))
        {
            yield return new object[] { Path.GetFileName(d), d };
        }
    }

    [Theory]
    [MemberData(nameof(Golden))]
    public void Muxes_to_streams_ffprobe_can_read(string name, string dir)
    {
        Assert.True(HasFfprobe(), "ffprobe must be installed to verify the muxer");

        var start = LoadStart(Path.Combine(dir, "start.json"));
        var muxer = new TsMuxer(start);
        var expected = muxer.Streams
            .Select(s => FfprobeName(s.Source.Codec))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        var tsPath = Path.Combine(Path.GetTempPath(), $"htsp_{name}.ts");
        var writer = new ArrayBufferWriter<byte>();
        muxer.WriteHeaders(writer);
        foreach (var pkt in LoadPackets(Path.Combine(dir, "packets.jsonl"), Path.Combine(dir, "payloads.bin")))
        {
            muxer.WritePacket(pkt, writer);
        }

        File.WriteAllBytes(tsPath, writer.WrittenSpan.ToArray());

        var actual = Ffprobe(tsPath)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            expected.SequenceEqual(actual),
            $"{name}: expected [{string.Join(",", expected)}] got [{string.Join(",", actual)}]");
    }

    private static HtspSubscriptionStart LoadStart(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var streams = new List<HtspStream>();
        foreach (var s in doc.RootElement.GetProperty("streams").EnumerateArray())
        {
            long? Get(string k) => s.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number
                ? v.GetInt64() : null;
            string? Str(string k) => s.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;

            var type = Str("type") ?? string.Empty;
            streams.Add(new HtspStream
            {
                Index = (int)(Get("index") ?? 0),
                Codec = HtspParsing.ParseCodec(type),
                RawType = type,
                Language = Str("language"),
                Width = (int?)Get("width"),
                Height = (int?)Get("height"),
                Duration = (int?)Get("duration"),
                Channels = (int?)Get("channels"),
                SampleRate = HtspParsing.SampleRateFromIndex(Get("rate")),
                CompositionId = (int?)Get("composition_id"),
                AncillaryId = (int?)Get("ancillary_id"),
                AudioType = (int)(Get("audio_type") ?? 0),
            });
        }

        return new HtspSubscriptionStart { SubscriptionId = 1, Streams = streams };
    }

    private static IEnumerable<HtspMuxPacket> LoadPackets(string jsonl, string bin)
    {
        var payloads = File.ReadAllBytes(bin);
        foreach (var line in File.ReadLines(jsonl))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            long? Num(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number
                ? v.GetInt64() : null;

            var off = (int)(Num("off") ?? 0);
            var len = (int)(Num("len") ?? 0);
            var ft = Num("frametype");

            yield return new HtspMuxPacket
            {
                StreamIndex = (int)(Num("stream") ?? -1),
                Payload = payloads.AsSpan(off, len).ToArray(),
                Pts = Num("pts"),
                Dts = Num("dts"),
                Duration = Num("duration") ?? 0,
                FrameType = ft.HasValue ? (char)ft.Value : null,
            };
        }
    }

    private static string FfprobeName(HtspCodec codec) => codec switch
    {
        HtspCodec.H264 => "h264",
        HtspCodec.Hevc => "hevc",
        HtspCodec.Mpeg2Video => "mpeg2video",
        HtspCodec.Mpeg2Audio => "mp2",
        HtspCodec.Aac => "aac",
        HtspCodec.Ac3 => "ac3",
        HtspCodec.Eac3 => "eac3",
        HtspCodec.DvbSub => "dvb_subtitle",
        HtspCodec.Teletext => "dvb_teletext",
        _ => "?",
    };

    private static List<string> Ffprobe(string tsPath)
    {
        var psi = new ProcessStartInfo("ffprobe",
            $"-v error -show_streams -of json \"{tsPath}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var json = p.StandardOutput.ReadToEnd();
        p.WaitForExit();

        using var doc = JsonDocument.Parse(json);
        var names = new List<string>();
        foreach (var s in doc.RootElement.GetProperty("streams").EnumerateArray())
        {
            names.Add(s.GetProperty("codec_name").GetString() ?? "?");
        }

        return names;
    }

    private static bool HasFfprobe()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("ffprobe", "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            p!.WaitForExit();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string GoldenRoot()
    {
        var d = AppContext.BaseDirectory;
        while (d is not null)
        {
            var candidate = Path.Combine(d, "tests", "golden");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            d = Path.GetDirectoryName(d);
        }

        throw new DirectoryNotFoundException("tests/golden not found");
    }
}
