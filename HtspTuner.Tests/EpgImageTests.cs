using HtspTuner.Epg;
using HtspTuner.Htsp;
using Xunit;

namespace HtspTuner.Tests;

/// <summary>
/// Tvheadend reports programme artwork as an <c>imagecache/&lt;id&gt;</c> server path, not a URL. Passing it
/// to Jellyfin verbatim meant no EPG image ever loaded and every guide refresh burned time failing to fetch
/// them ("Unable to convert any images to local"). These pin the rewrite, including the cases that must NOT
/// be rewritten and the ones that must not reach <c>fileOpen</c>.
/// </summary>
public class EpgImageTests
{
    private const string Base = "http://127.0.0.1:8096/HtspTuner/Image/338905175f2f/";

    private static HtspEvent Event(string? image) => new()
    {
        Id = 1,
        ChannelId = 2,
        Start = new DateTime(2026, 7, 16, 20, 0, 0, DateTimeKind.Utc),
        Stop = new DateTime(2026, 7, 16, 21, 0, 0, DateTimeKind.Utc),
        Title = "River Monsters",
        Image = image,
    };

    private static string? UrlFor(string? image)
        => EpgMapper.ToProgram("htsp_338905175f2f_1698006", Event(image), Base).ImageUrl;

    [Fact]
    public void Imagecache_path_becomes_a_url_our_endpoint_can_serve()
    {
        // The real shape Tvheadend sends -- 26% of events on a live server carry one of these.
        Assert.Equal(Base + "1105842058", UrlFor("imagecache/1105842058"));
    }

    [Fact]
    public void Real_urls_are_passed_through_untouched()
    {
        const string url = "https://example.com/artwork/river-monsters.jpg";
        Assert.Equal(url, UrlFor(url));
    }

    [Fact]
    public void No_image_stays_null()
    {
        Assert.Null(UrlFor(null));
        Assert.Null(UrlFor(string.Empty));
    }

    // fileOpen takes an arbitrary server-side path, so anything that isn't a plain numeric id must be
    // dropped rather than forwarded -- otherwise the endpoint becomes a file-read proxy for the whole host.
    [Theory]
    [InlineData("imagecache/../../etc/passwd")]
    [InlineData("imagecache/12; rm -rf /")]
    [InlineData("/etc/shadow")]
    [InlineData("imagecache/")]
    [InlineData("dvr/42")]
    public void Non_numeric_image_paths_are_refused(string image)
    {
        Assert.Null(UrlFor(image));
    }
}
