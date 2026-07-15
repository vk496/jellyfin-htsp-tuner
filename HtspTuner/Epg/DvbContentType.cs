namespace HtspTuner.Epg;

/// <summary>
/// The genre and classification flags derived from a DVB <c>content_descriptor</c> nibble pair.
/// </summary>
/// <param name="Category">The coarse, category-level genre (high nibble), or null when undefined.</param>
/// <param name="SubCategory">The finer, sub-category genre (low nibble), or null when unlisted.</param>
/// <param name="IsMovie">Whether the programme is a film.</param>
/// <param name="IsNews">Whether the programme is news or current affairs.</param>
/// <param name="IsSports">Whether the programme is sport.</param>
/// <param name="IsKids">Whether the programme is aimed at children.</param>
/// <param name="IsSeries">Whether the programme is an episodic show.</param>
/// <param name="IsEducational">Whether the programme is educational or factual.</param>
internal readonly record struct DvbGenre(
    string? Category,
    string? SubCategory,
    bool IsMovie,
    bool IsNews,
    bool IsSports,
    bool IsKids,
    bool IsSeries,
    bool IsEducational)
{
    /// <summary>Gets the genres to expose, most specific first, without duplicates or nulls.</summary>
    public IReadOnlyList<string> Genres
    {
        get
        {
            if (Category is null)
            {
                return Array.Empty<string>();
            }

            if (SubCategory is null || string.Equals(SubCategory, Category, StringComparison.Ordinal))
            {
                return new[] { Category };
            }

            return new[] { Category, SubCategory };
        }
    }
}

/// <summary>
/// Maps a DVB EIT <c>content_descriptor</c> byte to a Jellyfin genre and classification flags.
/// </summary>
/// <remarks>
/// The byte splits into two nibbles (ETSI EN 300 468 table 28): the high nibble is the category
/// and the low nibble the sub-category. Mapping is done <em>by nibble</em> — the category always
/// yields a genre and the classification flags, and the sub-category only refines the genre when it
/// is one Tvheadend actually populates. An unlisted sub-category therefore still gets the right genre
/// and the right <c>IsMovie</c>/<c>IsNews</c>/… flag, instead of falling through to nothing as the
/// previous plugin did.
/// </remarks>
internal static class DvbContentType
{
    /// <summary>
    /// Maps a raw DVB content-type byte to its genre and flags.
    /// </summary>
    /// <param name="contentType">The raw <c>content_descriptor</c> byte, high nibble = category.</param>
    /// <returns>The resolved genre and classification flags.</returns>
    public static DvbGenre Map(int contentType)
    {
        var category = (contentType >> 4) & 0xF;
        var sub = contentType & 0xF;

        return category switch
        {
            0x1 => new DvbGenre("Movie", MovieSub(sub), IsMovie: true, false, false, false, IsSeries: true, false),
            0x2 => new DvbGenre("News", NewsSub(sub), false, IsNews: true, false, false, false, false),
            0x3 => new DvbGenre("Show", ShowSub(sub), false, false, false, false, IsSeries: true, false),
            0x4 => new DvbGenre("Sports", SportsSub(sub), false, false, IsSports: true, false, false, false),
            0x5 => new DvbGenre("Children", ChildrenSub(sub), false, false, false, IsKids: true, false, IsEducational: sub is 0x5),
            0x6 => new DvbGenre("Music", MusicSub(sub), false, false, false, false, false, false),
            0x7 => new DvbGenre("Arts", ArtsSub(sub), false, false, false, false, false, false),
            0x8 => new DvbGenre("Social", SocialSub(sub), false, false, false, false, false, IsEducational: true),
            0x9 => new DvbGenre("Education", EducationSub(sub), false, false, false, false, false, IsEducational: true),
            0xA => new DvbGenre("Leisure", LeisureSub(sub), false, false, false, false, false, false),
            0xB => new DvbGenre("Special", null, false, false, false, false, false, false),
            _ => default,
        };
    }

    private static string? MovieSub(int s) => s switch
    {
        0x1 => "Detective / Thriller",
        0x2 => "Adventure / Western / War",
        0x3 => "Science Fiction / Fantasy / Horror",
        0x4 => "Comedy",
        0x5 => "Soap / Melodrama / Folkloric",
        0x6 => "Romance",
        0x7 => "Serious / Classical / Religious / Historical",
        0x8 => "Adult",
        _ => null,
    };

    private static string? NewsSub(int s) => s switch
    {
        0x1 => "News / Weather Report",
        0x2 => "News Magazine",
        0x3 => "Documentary",
        0x4 => "Discussion / Interview / Debate",
        _ => null,
    };

    private static string? ShowSub(int s) => s switch
    {
        0x1 => "Game Show / Quiz / Contest",
        0x2 => "Variety Show",
        0x3 => "Talk Show",
        _ => null,
    };

    private static string? SportsSub(int s) => s switch
    {
        0x1 => "Special Event",
        0x2 => "Sports Magazine",
        0x3 => "Football / Soccer",
        0x4 => "Tennis / Squash",
        0x5 => "Team Sports",
        0x6 => "Athletics",
        0x7 => "Motor Sport",
        0x8 => "Water Sport",
        0x9 => "Winter Sports",
        0xA => "Equestrian",
        0xB => "Martial Sports",
        _ => null,
    };

    private static string? ChildrenSub(int s) => s switch
    {
        0x1 => "Pre-school",
        0x2 => "Entertainment (6 to 14)",
        0x3 => "Entertainment (10 to 16)",
        0x4 => "Informational / Educational / Schools",
        0x5 => "Cartoons / Puppets",
        _ => null,
    };

    private static string? MusicSub(int s) => s switch
    {
        0x1 => "Rock / Pop",
        0x2 => "Serious / Classical Music",
        0x3 => "Folk / Traditional Music",
        0x4 => "Jazz",
        0x5 => "Musical / Opera",
        0x6 => "Ballet",
        _ => null,
    };

    private static string? ArtsSub(int s) => s switch
    {
        0x1 => "Performing Arts",
        0x2 => "Fine Arts",
        0x3 => "Religion",
        0x4 => "Popular Culture / Traditional Arts",
        0x5 => "Literature",
        0x6 => "Film / Cinema",
        0x7 => "Experimental Film / Video",
        0x8 => "Broadcasting / Press",
        _ => null,
    };

    private static string? SocialSub(int s) => s switch
    {
        0x1 => "Magazines / Reports / Documentary",
        0x2 => "Economics / Social Advisory",
        0x3 => "Remarkable People",
        _ => null,
    };

    private static string? EducationSub(int s) => s switch
    {
        0x1 => "Nature / Animals / Environment",
        0x2 => "Technology / Natural Sciences",
        0x3 => "Medicine / Physiology / Psychology",
        0x4 => "Foreign Countries / Expeditions",
        0x5 => "Social / Spiritual Sciences",
        0x6 => "Further Education",
        0x7 => "Languages",
        _ => null,
    };

    private static string? LeisureSub(int s) => s switch
    {
        0x1 => "Tourism / Travel",
        0x2 => "Handicraft",
        0x3 => "Motoring",
        0x4 => "Fitness & Health",
        0x5 => "Cooking",
        0x6 => "Advertisement / Shopping",
        0x7 => "Gardening",
        _ => null,
    };
}
