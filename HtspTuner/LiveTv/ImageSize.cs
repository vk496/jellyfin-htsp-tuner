using System.Buffers.Binary;

namespace HtspTuner.LiveTv;

/// <summary>Reads an image's pixel dimensions out of its header.</summary>
/// <remarks>
/// Only needed to place a circle behind a channel logo: the radius follows the logo's diagonal, and
/// ffmpeg's scale filter works the height out internally without ever telling us. Reading the header is a
/// few bytes of file; the alternative is an ffprobe process per capture for two numbers.
/// </remarks>
internal static class ImageSize
{
    /// <summary>Reads the dimensions of a PNG, JPEG or GIF.</summary>
    /// <param name="path">The image file.</param>
    /// <returns>The dimensions, or null if the format is not one of those or the file is malformed.</returns>
    public static (int Width, int Height)? Read(string path)
    {
        try
        {
            // Each format needs a different amount to identify and measure, so read what is there and let
            // the branches check for themselves -- a fixed minimum would reject small but valid images.
            using var file = File.OpenRead(path);
            Span<byte> head = stackalloc byte[24];
            var n = file.Read(head);
            if (n < 4)
            {
                return null;
            }

            // PNG: an IHDR chunk always comes first, with the size at a fixed offset.
            ReadOnlySpan<byte> pngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
            if (n >= 24 && head[..8].SequenceEqual(pngSignature))
            {
                return (
                    BinaryPrimitives.ReadInt32BigEndian(head[16..20]),
                    BinaryPrimitives.ReadInt32BigEndian(head[20..24]));
            }

            // GIF: little-endian width and height right after the signature.
            if (n >= 10 && head[..3].SequenceEqual("GIF"u8))
            {
                return (
                    BinaryPrimitives.ReadUInt16LittleEndian(head[6..8]),
                    BinaryPrimitives.ReadUInt16LittleEndian(head[8..10]));
            }

            if (head[0] == 0xFF && head[1] == 0xD8)
            {
                return ReadJpeg(file);
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    // JPEG keeps its dimensions in a "start of frame" segment, whose position depends on what came before
    // it, so the segment chain has to be walked.
    private static (int Width, int Height)? ReadJpeg(FileStream file)
    {
        file.Position = 2;
        Span<byte> segment = stackalloc byte[9];

        while (true)
        {
            // Segments start with 0xFF; a run of them is padding.
            int marker;
            do
            {
                marker = file.ReadByte();
                if (marker < 0)
                {
                    return null;
                }
            }
            while (marker != 0xFF);

            do
            {
                marker = file.ReadByte();
                if (marker < 0)
                {
                    return null;
                }
            }
            while (marker == 0xFF);

            // Every SOFn carries the size except the four that are not frame headers at all.
            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC or 0xD8))
            {
                if (file.Read(segment[..7]) < 7)
                {
                    return null;
                }

                return (
                    BinaryPrimitives.ReadUInt16BigEndian(segment[5..7]),
                    BinaryPrimitives.ReadUInt16BigEndian(segment[3..5]));
            }

            if (marker is 0xD8 or 0xD9 or (>= 0xD0 and <= 0xD7))
            {
                continue; // no payload to skip
            }

            if (file.Read(segment[..2]) < 2)
            {
                return null;
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(segment[..2]);
            if (length < 2)
            {
                return null;
            }

            file.Position += length - 2;
        }
    }
}
