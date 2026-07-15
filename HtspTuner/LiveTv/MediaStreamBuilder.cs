using HtspTuner.Htsp;
using HtspTuner.Mpeg;
using MediaBrowser.Model.Entities;

namespace HtspTuner.LiveTv;

/// <summary>
/// Builds Jellyfin <see cref="MediaStream"/>s from the muxer's stream table, so Jellyfin knows the exact
/// codecs, languages and layout up front and never has to probe the live stream.
/// </summary>
internal static class MediaStreamBuilder
{
    /// <summary>Builds the media streams for a muxed channel.</summary>
    /// <param name="tsStreams">The streams the muxer emits, in PMT order.</param>
    /// <returns>The Jellyfin media streams; indexes match the emitted TS order.</returns>
    public static List<MediaStream> Build(IReadOnlyList<TsStreamInfo> tsStreams)
    {
        var result = new List<MediaStream>(tsStreams.Count);
        var firstAudio = true;

        foreach (var ts in tsStreams)
        {
            var s = ts.Source;
            var stream = new MediaStream
            {
                // Index is the position in the emitted TS, which is what ffmpeg will report.
                Index = ts.TsIndex,
                Codec = CodecName(s.Codec),
                Language = s.Language,
                Type = MediaType(s),
            };

            if (s.IsVideo)
            {
                stream.Width = s.Width;
                stream.Height = s.Height;
                if (s is { AspectNum: > 0, AspectDen: > 0 })
                {
                    stream.AspectRatio = $"{s.AspectNum}:{s.AspectDen}";
                }

                stream.BitRate = EstimateVideoBitrate(s);

                // fps = 90000 / frame-duration (HTSP sends the frame duration in 90 kHz ticks, the same
                // formula Kodi's pvr.hts uses). This is the picture rate — all HTSP gives us. Interlacing,
                // colour and bit depth are not on the wire and are filled in later by MergeProbe.
                if (s.Duration is > 0)
                {
                    var fps = 90000f / s.Duration.Value;
                    stream.AverageFrameRate = fps;
                    stream.RealFrameRate = fps;
                }
            }
            else if (s.IsAudio)
            {
                stream.Channels = s.Channels;
                stream.SampleRate = s.SampleRate;
                stream.IsDefault = firstAudio;
                firstAudio = false;
                stream.IsHearingImpaired = s.AudioType == 2;
            }

            result.Add(stream);
        }

        return result;
    }

    /// <summary>
    /// Overlays the fields only a bitstream probe can know — interlacing, bit depth, colour/HDR, profile,
    /// level, ref-frames and real bit rates — onto streams already built from HTSP metadata. HTSP's
    /// <c>subscriptionStart</c> carries none of these (Kodi's pvr.hts leaves them to the client decoder for
    /// the same reason), so Jellyfin can only make a correct copy-vs-transcode-vs-deinterlace decision once
    /// we probe our own muxed output and report them. Video is matched first-to-first (there is one) and
    /// audio pairwise in TS order; both lists come from the same TS we produced, so the order aligns.
    /// </summary>
    /// <param name="ours">The streams built from HTSP; mutated in place.</param>
    /// <param name="probed">The streams ffprobe found in our muxed TS.</param>
    public static void MergeProbe(IReadOnlyList<MediaStream> ours, IReadOnlyList<MediaStream> probed)
    {
        var ourVideo = ours.FirstOrDefault(s => s.Type == MediaStreamType.Video);
        var probedVideo = probed.FirstOrDefault(s => s.Type == MediaStreamType.Video);
        if (ourVideo is not null && probedVideo is not null)
        {
            ourVideo.IsInterlaced = probedVideo.IsInterlaced; // the combing fix
            ourVideo.BitDepth = probedVideo.BitDepth ?? ourVideo.BitDepth;
            ourVideo.PixelFormat = probedVideo.PixelFormat ?? ourVideo.PixelFormat;
            ourVideo.Profile = probedVideo.Profile ?? ourVideo.Profile;
            ourVideo.Level = probedVideo.Level ?? ourVideo.Level;
            ourVideo.RefFrames = probedVideo.RefFrames ?? ourVideo.RefFrames;
            ourVideo.ColorSpace = probedVideo.ColorSpace ?? ourVideo.ColorSpace;
            ourVideo.ColorPrimaries = probedVideo.ColorPrimaries ?? ourVideo.ColorPrimaries;
            ourVideo.ColorTransfer = probedVideo.ColorTransfer ?? ourVideo.ColorTransfer; // drives HDR/SDR VideoRange
            ourVideo.ColorRange = probedVideo.ColorRange ?? ourVideo.ColorRange;
            if (probedVideo.BitRate is > 0)
            {
                ourVideo.BitRate = probedVideo.BitRate; // real rate beats the width*height estimate
            }

            if (probedVideo.AverageFrameRate is > 0)
            {
                ourVideo.AverageFrameRate = probedVideo.AverageFrameRate;
            }

            if (probedVideo.RealFrameRate is > 0)
            {
                ourVideo.RealFrameRate = probedVideo.RealFrameRate;
            }

            // Leave IsAVC null: false blocks stream copy, and the server nulls a false itself.
        }

        var ourAudio = ours.Where(s => s.Type == MediaStreamType.Audio).ToList();
        var probedAudio = probed.Where(s => s.Type == MediaStreamType.Audio).ToList();
        for (var i = 0; i < ourAudio.Count && i < probedAudio.Count; i++)
        {
            var o = ourAudio[i];
            var p = probedAudio[i];
            if (p.BitRate is > 0)
            {
                o.BitRate = p.BitRate;
            }

            o.ChannelLayout ??= p.ChannelLayout;
            o.Profile ??= p.Profile;
        }
    }

    // A realistic per-resolution estimate. Without it, Jellyfin assumes a huge default (~20 Mbit/s) for a
    // live source, decides it exceeds the client's bitrate cap, and re-encodes+downscales the video — a
    // software encode that barely keeps up at HD and falls behind at 4K, which is what causes the stutter.
    // A sane estimate lets Jellyfin stream-copy the (browser-compatible) video on a LAN and only transcode
    // the audio. It is not exact; live TV has no advertised bitrate, but it is far closer than the default.
    private static int? EstimateVideoBitrate(HtspStream s)
    {
        if (s.Width is not > 0 || s.Height is not > 0)
        {
            return null;
        }

        var fps = s.Duration is > 0 ? 90000.0 / s.Duration.Value : 25.0;
        var bitsPerPixel = s.Codec switch
        {
            HtspCodec.Hevc => 0.06,
            HtspCodec.Mpeg2Video => 0.16,
            _ => 0.10, // H.264 and friends
        };

        var estimate = s.Width.Value * s.Height.Value * fps * bitsPerPixel;
        return (int)Math.Clamp(estimate, 1_000_000, 25_000_000);
    }

    private static MediaStreamType MediaType(HtspStream s)
    {
        if (s.IsVideo)
        {
            return MediaStreamType.Video;
        }

        return s.IsAudio ? MediaStreamType.Audio : MediaStreamType.Subtitle;
    }

    private static string CodecName(HtspCodec codec) => codec switch
    {
        HtspCodec.H264 => "h264",
        HtspCodec.Hevc => "hevc",
        HtspCodec.Mpeg2Video => "mpeg2video",
        HtspCodec.Mpeg2Audio => "mp2",
        HtspCodec.Aac => "aac",
        HtspCodec.AacLatm => "aac_latm",
        HtspCodec.Ac3 => "ac3",
        HtspCodec.Eac3 => "eac3",
        HtspCodec.Vorbis => "vorbis",
        HtspCodec.DvbSub => "dvb_subtitle",
        HtspCodec.Teletext => "dvb_teletext",
        _ => "data",
    };
}
