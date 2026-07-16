using System.Globalization;
using HtspTuner.Htsp;
using MediaBrowser.Controller.LiveTv;

namespace HtspTuner.Epg;

/// <summary>Maps a Tvheadend EPG event to a Jellyfin <see cref="ProgramInfo"/>.</summary>
internal static class EpgMapper
{
    /// <summary>Builds a program for a channel from a cached HTSP event.</summary>
    /// <param name="channelId">The Jellyfin channel id this program belongs to.</param>
    /// <param name="e">The HTSP event.</param>
    /// <param name="imageBaseUrl">
    /// Base URL of the plugin's image endpoint for this server, with a trailing slash. An image-cache id
    /// is appended to it.
    /// </param>
    /// <returns>The program.</returns>
    public static ProgramInfo ToProgram(string channelId, HtspEvent e, string imageBaseUrl)
    {
        var genre = DvbContentType.Map(e.ContentType);
        return new ProgramInfo
        {
            Id = e.Id.ToString(CultureInfo.InvariantCulture),
            ChannelId = channelId,
            Name = e.Title ?? string.Empty,
            Overview = e.Description,
            EpisodeTitle = e.Subtitle,
            StartDate = e.Start,
            EndDate = e.Stop,
            Genres = genre.Genres.ToList(),
            IsMovie = genre.IsMovie,
            IsNews = genre.IsNews,
            IsSports = genre.IsSports,
            IsKids = genre.IsKids,
            IsSeries = genre.IsSeries || e.Subtitle is not null || e.EpisodeNumber is not null,
            IsRepeat = e.IsRepeat,
            SeasonNumber = e.SeasonNumber,
            EpisodeNumber = e.EpisodeNumber,
            ProductionYear = e.CopyrightYear,
            OriginalAirDate = e.FirstAired,
            OfficialRating = e.AgeRating is { } age and > 0 ? age.ToString(CultureInfo.InvariantCulture) : null,
            CommunityRating = e.StarRating is { } star and > 0 ? star : null,
            ImageUrl = ImageUrl(e.Image, imageBaseUrl),
            SeriesId = e.SeriesLinkId,
        };
    }

    // Tvheadend reports programme artwork as "imagecache/<id>" -- a path on the server, not a URL. Handing
    // that to Jellyfin verbatim is why EPG images never appeared: it stores the string, then fails to fetch
    // it ("Unable to convert any images to local") for every programme that has one, on every guide refresh.
    // Point it at our own endpoint instead, which reads the bytes over the HTSP connection we already hold
    // -- the same trick channel icons use, and it keeps the plugin free of any HTTP dependency on Tvheadend.
    // A real http(s) URL (some EPG sources give one) is passed straight through for Jellyfin to fetch.
    private static string? ImageUrl(string? image, string imageBaseUrl)
    {
        if (string.IsNullOrEmpty(image))
        {
            return null;
        }

        if (image.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return image;
        }

        // Only the exact imagecache/<digits> form is fetchable, prefix included: the endpoint takes an id
        // and rebuilds the path itself, so a crafted value can never become an arbitrary server-side file
        // read via fileOpen. Matching on the last segment alone would happily turn "dvr/42" into an image.
        const string Prefix = "imagecache/";
        if (!image.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var id = image[Prefix.Length..];
        return id.Length > 0 && id.All(char.IsAsciiDigit) ? imageBaseUrl + id : null;
    }
}
