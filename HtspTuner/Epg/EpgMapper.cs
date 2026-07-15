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
    /// <returns>The program.</returns>
    public static ProgramInfo ToProgram(string channelId, HtspEvent e)
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
            ImageUrl = e.Image,
            SeriesId = e.SeriesLinkId,
        };
    }
}
