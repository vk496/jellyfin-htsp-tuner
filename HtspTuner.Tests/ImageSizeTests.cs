using HtspTuner.LiveTv;
using Xunit;

namespace HtspTuner.Tests;

/// <summary>
/// The disc drawn behind a channel logo is a circle around the logo's corner, so its radius follows the
/// logo's diagonal — which means the logo's real dimensions, not just the width we scale it to. Getting
/// this wrong draws a disc that does not match the logo it is meant to sit behind.
/// </summary>
public class ImageSizeTests : IDisposable
{
    private readonly List<string> _files = [];

    [Fact]
    public void ReadsPng()
    {
        // 8-byte signature, then a length+type+IHDR whose first two fields are the dimensions.
        var png = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13 };
        png.AddRange("IHDR"u8.ToArray());
        png.AddRange([0, 0, 0x01, 0xBF]); // 447
        png.AddRange([0, 0, 0x01, 0x89]); // 393
        png.AddRange(new byte[16]);

        Assert.Equal((447, 393), ImageSize.Read(Write(png.ToArray(), ".png")));
    }

    [Fact]
    public void ReadsGif()
    {
        var gif = new List<byte>();
        gif.AddRange("GIF89a"u8.ToArray());
        gif.AddRange([0xDC, 0x00]); // 220, little endian
        gif.AddRange([0x84, 0x00]); // 132
        gif.AddRange(new byte[24]);

        Assert.Equal((220, 132), ImageSize.Read(Write(gif.ToArray(), ".gif")));
    }

    [Fact]
    public void ReadsJpegPastAnEarlierSegment()
    {
        // The size lives in a start-of-frame segment whose position depends on what precedes it, so the
        // chain has to be walked rather than read at a fixed offset. Here an APP0 comes first.
        var jpeg = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 };
        jpeg.AddRange(new byte[14]);                                   // APP0 payload
        jpeg.AddRange([0xFF, 0xC0, 0x00, 0x11, 0x08, 0x02, 0x1C, 0x03, 0xC0]); // SOF0: 540 high, 960 wide
        jpeg.AddRange(new byte[16]);

        Assert.Equal((960, 540), ImageSize.Read(Write(jpeg.ToArray(), ".jpg")));
    }

    [Fact]
    public void SkipsARestartMarkerWithNoPayload()
    {
        // 0xD0-0xD7 carry no length field; treating them as though they did walks off into the pixel data.
        var jpeg = new List<byte> { 0xFF, 0xD8, 0xFF, 0xD0 };
        jpeg.AddRange([0xFF, 0xC2, 0x00, 0x11, 0x08, 0x00, 0x64, 0x00, 0xC8]); // SOF2: 100 high, 200 wide
        jpeg.AddRange(new byte[16]);

        Assert.Equal((200, 100), ImageSize.Read(Write(jpeg.ToArray(), ".jpg")));
    }

    [Fact]
    public void ReturnsNullForSomethingElse()
    {
        // An SVG channel icon, for instance. The caller draws the logo bare rather than on a guessed disc.
        Assert.Null(ImageSize.Read(Write("<svg xmlns='http://www.w3.org/2000/svg' width='64'></svg>"u8.ToArray(), ".svg")));
    }

    [Fact]
    public void ReturnsNullForAMissingFile()
        => Assert.Null(ImageSize.Read(Path.Combine(Path.GetTempPath(), "htsp-not-here-" + Guid.NewGuid().ToString("N"))));

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var f in _files)
        {
            File.Delete(f);
        }
    }

    private string Write(byte[] bytes, string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), "htsp-size-" + Guid.NewGuid().ToString("N") + extension);
        File.WriteAllBytes(path, bytes);
        _files.Add(path);
        return path;
    }
}
