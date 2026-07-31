using System;
using System.Collections.Generic;
using System.Globalization;

namespace Jellyfin.Plugin.PdCodesApi;

/// <summary>
/// Provider-id keys and the mapping between Jellyfin's id space and v5's.
/// </summary>
public static class PdCodesIds
{
    /// <summary>
    /// The key under which the v5 ULID is stored in <c>BaseItem.ProviderIds</c>.
    /// </summary>
    /// <remarks>
    /// Storing the ULID (not a MAL/TMDB id) is what makes a re-scan stable: it is the
    /// canonical v5 id, it survives a work gaining or losing a third-party id, and it
    /// is the only id that needs no type to disambiguate it. Changing this string
    /// orphans every already-identified item, so it is a constant and not a setting.
    /// </remarks>
    public const string WorkIdKey = "PdCodesApi";

    /// <summary>
    /// The key under which an episode's v5 reference ("S02E01", "E62", "SP3") is stored.
    /// </summary>
    public const string EpisodeRefKey = "PdCodesApiEpisode";

    /// <summary>The display name used everywhere the provider identifies itself.</summary>
    public const string ProviderName = "PD-Codes API";

    // --- Jellyfin's own provider-id keys ------------------------------------------------
    // Verified against Jellyfin 10.10.6, MediaBrowser.Model/Entities/MetadataProvider.cs:
    // the enum members are Imdb, Tmdb, Tvdb (among others) and Jellyfin persists
    // ProviderIds keyed by the enum member NAME, so these strings are exact.

    /// <summary>Jellyfin's IMDb key.</summary>
    public const string JellyfinImdb = "Imdb";

    /// <summary>
    /// Jellyfin's TMDB key. Note that Jellyfin uses ONE key for both movies and series.
    /// </summary>
    public const string JellyfinTmdb = "Tmdb";

    /// <summary>Jellyfin's TheTVDB key, likewise unnamespaced.</summary>
    public const string JellyfinTvdb = "Tvdb";

    /// <summary>
    /// MyAnimeList key as written by the common third-party anime plugins.
    /// </summary>
    /// <remarks>
    /// Not part of Jellyfin core's MetadataProvider enum - it comes from Shokofin /
    /// the AniList plugin. If those plugins are absent the key is simply never present
    /// and this lookup is skipped, which is why an unrecognized key is harmless here
    /// while a wrong v5 source key would not be.
    /// </remarks>
    public const string JellyfinMyAnimeList = "MyAnimeList";

    /// <summary>AniList key as written by the common third-party anime plugins.</summary>
    public const string JellyfinAniList = "AniList";

    // --- v5 source keys -----------------------------------------------------------------

    /// <summary>v5 source key for TMDB films. There is no bare "tmdb"; it answers 400.</summary>
    public const string V5TmdbMovie = "tmdb_movie";

    /// <summary>v5 source key for TMDB series.</summary>
    public const string V5TmdbTv = "tmdb_tv";

    /// <summary>v5 source key for TheTVDB series. TheTVDB indexes anime as SERIES.</summary>
    public const string V5TvdbSeries = "tvdb_series";

    /// <summary>v5 source key for TheTVDB movies.</summary>
    public const string V5TvdbMovie = "tvdb_movie";

    /// <summary>v5 source key for IMDb.</summary>
    public const string V5Imdb = "imdb";

    /// <summary>v5 source key for MyAnimeList.</summary>
    public const string V5Mal = "mal";

    /// <summary>v5 source key for AniList.</summary>
    public const string V5AniList = "anilist";

    // --- v5 type segments ---------------------------------------------------------------

    /// <summary>v5 type for anime, of any runtime - film or series.</summary>
    public const string TypeAnime = "anime";

    /// <summary>v5 type for a live-action / western film.</summary>
    public const string TypeMovie = "movie";

    /// <summary>v5 type for a live-action / western series.</summary>
    public const string TypeTv = "tv";

    /// <summary>
    /// Returns the v5 types a Jellyfin Movie may be, in the configured order.
    /// </summary>
    /// <remarks>
    /// THIS IS THE MAPPING THE WHOLE PLUGIN TURNS ON, so it is stated once, here.
    ///
    /// v5 does not have an "anime" that is parallel to Jellyfin's Movie/Series split.
    /// "anime" is a MEDIUM in v5 and it contains both films (Spirited Away) and series
    /// (Death Note). Jellyfin splits by SHAPE, not by medium: Spirited Away is a Movie
    /// and Death Note is a Series, and neither is a distinct Jellyfin item type.
    ///
    /// So the relation is many-to-many at the type level:
    ///   Jellyfin Movie  -> v5 "movie" OR v5 "anime"
    ///   Jellyfin Series -> v5 "tv"    OR v5 "anime"
    ///
    /// We must therefore try both, and we must pass ?type= to every lookup, because an
    /// unscoped lookup by MAL or TMDB id is exactly the query that returns 409 (or, on
    /// an older instance, the wrong medium's work at full confidence).
    /// </remarks>
    /// <returns>The candidate v5 types, most likely first.</returns>
    public static IReadOnlyList<string> MovieTypes()
    {
        var preferAnime = Plugin.Instance?.Configuration.PreferAnimeForMovies ?? false;
        return preferAnime
            ? new[] { TypeAnime, TypeMovie }
            : new[] { TypeMovie, TypeAnime };
    }

    /// <summary>
    /// Returns the v5 types a Jellyfin Series may be, in the configured order.
    /// </summary>
    /// <returns>The candidate v5 types, most likely first.</returns>
    public static IReadOnlyList<string> SeriesTypes()
    {
        var preferAnime = Plugin.Instance?.Configuration.PreferAnimeForSeries ?? true;
        return preferAnime
            ? new[] { TypeAnime, TypeTv }
            : new[] { TypeTv, TypeAnime };
    }

    /// <summary>
    /// Translates ids Jellyfin already holds into v5 (source, id) lookup pairs.
    /// </summary>
    /// <param name="providerIds">The item's existing provider ids.</param>
    /// <param name="isMovieShaped">True for a Jellyfin Movie, false for a Series. This
    /// is the ONLY thing that decides the TMDB and TheTVDB namespace.</param>
    /// <returns>Lookup pairs in descending order of trustworthiness.</returns>
    /// <remarks>
    /// TMDB movie 79744 is an Italian comedy; TMDB TV 79744 is The Rookie. TheTVDB
    /// series 121361 is Game of Thrones; TheTVDB movie 121361 is a German comedy. Both
    /// numbers are real in both spaces. Jellyfin stores them under one key each, so the
    /// namespace has to be recovered from the ITEM's shape - there is no other signal.
    /// Getting it wrong does not error: it resolves, confidently, to a different work.
    ///
    /// Ordering: IMDb first because its ids are globally unique across media and cannot
    /// be mis-namespaced at all. Then the namespaced TMDB/TVDB ids. MAL and AniList last
    /// because those id spaces are shared between anime and manga - the lookup is always
    /// type-scoped by the caller, but a manga collision is still the likeliest 409.
    /// </remarks>
    public static IReadOnlyList<(string Source, string Id)> ToLookupPairs(
        IReadOnlyDictionary<string, string>? providerIds,
        bool isMovieShaped)
    {
        var pairs = new List<(string, string)>();
        if (providerIds is null)
        {
            return pairs;
        }

        void Add(string jellyfinKey, string v5Source)
        {
            if (providerIds.TryGetValue(jellyfinKey, out var value)
                && !string.IsNullOrWhiteSpace(value))
            {
                pairs.Add((v5Source, value.Trim()));
            }
        }

        Add(JellyfinImdb, V5Imdb);
        Add(JellyfinTmdb, isMovieShaped ? V5TmdbMovie : V5TmdbTv);
        Add(JellyfinTvdb, isMovieShaped ? V5TvdbMovie : V5TvdbSeries);
        Add(JellyfinMyAnimeList, V5Mal);
        Add(JellyfinAniList, V5AniList);

        return pairs;
    }

    /// <summary>
    /// Copies v5 external ids back onto a Jellyfin item, using the same namespacing.
    /// </summary>
    /// <param name="work">The resolved work.</param>
    /// <param name="isMovieShaped">True for a Jellyfin Movie, false for a Series.</param>
    /// <returns>Jellyfin provider-id key/value pairs, safe to write.</returns>
    /// <remarks>
    /// Only <c>external_ids</c> is read here - never <c>uncertain_external_ids</c>.
    /// Those are title matches, and writing one onto a Jellyfin item makes a guess
    /// permanent: the next scan sees a populated id and never re-asks.
    /// Reading the namespace back is the mirror of the read path: a film only ever
    /// receives tmdb_movie, a series only ever tmdb_tv.
    /// </remarks>
    public static IEnumerable<KeyValuePair<string, string>> ToJellyfinProviderIds(
        Api.Work work,
        bool isMovieShaped)
    {
        if (work.ExternalIds is null)
        {
            yield break;
        }

        foreach (var pair in Translate(work.ExternalIds, isMovieShaped))
        {
            yield return pair;
        }
    }

    private static IEnumerable<KeyValuePair<string, string>> Translate(
        IReadOnlyDictionary<string, string> externalIds,
        bool isMovieShaped)
    {
        if (externalIds.TryGetValue(V5Imdb, out var imdb) && !string.IsNullOrWhiteSpace(imdb))
        {
            yield return new KeyValuePair<string, string>(JellyfinImdb, imdb);
        }

        var tmdbSource = isMovieShaped ? V5TmdbMovie : V5TmdbTv;
        if (externalIds.TryGetValue(tmdbSource, out var tmdb) && !string.IsNullOrWhiteSpace(tmdb))
        {
            yield return new KeyValuePair<string, string>(JellyfinTmdb, tmdb);
        }

        var tvdbSource = isMovieShaped ? V5TvdbMovie : V5TvdbSeries;
        if (externalIds.TryGetValue(tvdbSource, out var tvdb) && !string.IsNullOrWhiteSpace(tvdb))
        {
            yield return new KeyValuePair<string, string>(JellyfinTvdb, tvdb);
        }

        if (externalIds.TryGetValue(V5Mal, out var mal) && !string.IsNullOrWhiteSpace(mal))
        {
            yield return new KeyValuePair<string, string>(JellyfinMyAnimeList, mal);
        }

        if (externalIds.TryGetValue(V5AniList, out var anilist) && !string.IsNullOrWhiteSpace(anilist))
        {
            yield return new KeyValuePair<string, string>(JellyfinAniList, anilist);
        }
    }

    /// <summary>
    /// Checks that a string looks like a v5 ULID rather than a numeric third-party id.
    /// </summary>
    /// <param name="value">Candidate id.</param>
    /// <returns>True when it is plausibly a ULID.</returns>
    /// <remarks>
    /// Guard, not validation. "/v5/anime/5114" is a MAL id and answers 404 with
    /// directions; catching that here turns a confusing 404 in the log into a precise
    /// message about the wrong id being stored under our key.
    /// </remarks>
    public static bool LooksLikeUlid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 26)
        {
            return false;
        }

        foreach (var c in value)
        {
            var isCrockford = (c >= '0' && c <= '9')
                || (c >= 'A' && c <= 'Z')
                || (c >= 'a' && c <= 'z');
            if (!isCrockford)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Builds the v5 episode reference for a Jellyfin season/episode number pair.
    /// </summary>
    /// <param name="seasonNumber">Jellyfin ParentIndexNumber, or null.</param>
    /// <param name="episodeNumber">Jellyfin IndexNumber.</param>
    /// <returns>"SP3", "S02E01" or "E62".</returns>
    /// <remarks>
    /// Jellyfin's season 0 is the specials folder by universal convention, and v5
    /// spells specials "SP{n}". A season 0 episode sent as "S00E03" would be a
    /// different (and almost certainly absent) reference.
    ///
    /// When Jellyfin has no season number at all we fall back to the absolute form
    /// "E{n}". That is the honest reading: we know which episode of the whole run it
    /// is and nothing about seasons. We do NOT invent season 1 - TMDB does not always
    /// restart numbering at 1 per season (One Piece season 2 is episodes 62-77), so
    /// "S01E62" and "E62" are different episodes on exactly the works where it matters.
    /// </remarks>
    public static string BuildEpisodeRef(int? seasonNumber, int episodeNumber)
    {
        if (seasonNumber == 0)
        {
            return string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"SP{episodeNumber}");
        }

        if (seasonNumber.HasValue)
        {
            return string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"S{seasonNumber.Value:D2}E{episodeNumber:D2}");
        }

        return BuildAbsoluteEpisodeRef(episodeNumber);
    }

    /// <summary>
    /// Builds the absolute ("whole run") v5 episode reference "E{n}".
    /// </summary>
    /// <param name="episodeNumber">The episode's number counted across the entire run.</param>
    /// <returns>"E62".</returns>
    /// <remarks>
    /// The format lives here and nowhere else. <see cref="BuildEpisodeRef"/> calls it for
    /// its no-season branch and the absolute-numbering fallback calls it directly, so the
    /// two cannot drift apart - a fallback that built "E062" while the no-season path built
    /// "E62" would miss on exactly the works the fallback exists for, and would look like
    /// an absent episode rather than a formatting bug.
    /// </remarks>
    public static string BuildAbsoluteEpisodeRef(int episodeNumber)
        => string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"E{episodeNumber}");

    /// <summary>
    /// Checks whether a reference is the absolute form "E{n}".
    /// </summary>
    /// <param name="episodeRef">A v5 episode reference, or null.</param>
    /// <returns>True when it is "E" followed by digits and nothing else.</returns>
    /// <remarks>
    /// Case-sensitive on the leading "E", and it must be exactly what
    /// <see cref="BuildAbsoluteEpisodeRef"/> emits - this is a test for "the reference is
    /// already on the absolute axis", not a lenient parse. Two callers depend on that:
    /// the fallback refuses to retry a reference that is already absolute (there would be
    /// nothing else to try), and the stored-reference path validates on the axis THE
    /// REFERENCE is on rather than on Jellyfin's season number. Note that "S02E01" also
    /// contains an 'E' and must NOT be seen as absolute, which is why the check is anchored
    /// at position 0 and requires digits for the entire remainder.
    ///
    /// Digits are compared by range rather than with char.IsDigit, which is true for the
    /// Unicode decimal digits of every script (Arabic-Indic, Devanagari, ...) - none of
    /// which int.Parse would read the same way, and none of which this API ever emits.
    /// </remarks>
    public static bool IsAbsoluteRef(string? episodeRef)
    {
        if (string.IsNullOrEmpty(episodeRef) || episodeRef.Length < 2 || episodeRef[0] != 'E')
        {
            return false;
        }

        for (var i = 1; i < episodeRef.Length; i++)
        {
            var c = episodeRef[i];
            if (c < '0' || c > '9')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Reads back a reference that <see cref="BuildEpisodeRef"/> could have produced.
    /// </summary>
    /// <param name="episodeRef">A v5 episode reference, or null.</param>
    /// <param name="season">On success: 0 for "SP{n}", the season number for "S{nn}E{nn}",
    /// and null for the absolute form "E{n}" - which carries no season at all.</param>
    /// <param name="number">On success: the episode number the reference addresses.</param>
    /// <returns>True only for the three forms this plugin emits.</returns>
    /// <remarks>
    /// This is the INVERSE of <see cref="BuildEpisodeRef"/> and nothing more. It exists so
    /// that a reference read back out of Jellyfin's database can be checked against the
    /// numbers the item carries TODAY - see the stored-reference handling in
    /// PdCodesEpisodeProvider for why that matters. A lenient parse would defeat the point:
    /// the question being asked is "is this string one we wrote, and does it still describe
    /// this file", and a string we did not write cannot be answered for.
    ///
    /// Strictness, each clause of it deliberate:
    /// - ASCII digits only, by range. char.IsDigit is true for the decimal digits of every
    ///   Unicode script (Arabic-Indic, Devanagari, ...), none of which this API emits and
    ///   none of which int.Parse reads back the same way.
    /// - NumberStyles.None with the invariant culture, so no sign, no whitespace, no
    ///   thousands separator is accepted, and an id far past int.MaxValue fails rather than
    ///   wrapping into a plausible-looking small number.
    /// - The whole string must be consumed. "S02E01x" is not a reference we wrote.
    /// - Case-sensitive: "s02e01" is not what BuildEpisodeRef emits, and treating it as
    ///   equivalent would be a claim about the server's parser that we have not verified.
    /// </remarks>
    public static bool TryParseEpisodeRef(string? episodeRef, out int? season, out int number)
    {
        season = null;
        number = 0;

        if (string.IsNullOrEmpty(episodeRef))
        {
            return false;
        }

        // "E{n}" - the absolute axis. IsAbsoluteRef is the single definition of that form,
        // so this branch cannot drift away from the check the providers use.
        if (episodeRef[0] == 'E')
        {
            if (!IsAbsoluteRef(episodeRef) || !TryReadDigits(episodeRef, 1, episodeRef.Length, out number))
            {
                return false;
            }

            season = null;
            return true;
        }

        if (episodeRef[0] != 'S')
        {
            return false;
        }

        // "SP{n}" - a special. v5 spells specials this way and Jellyfin files them under
        // season 0, which is the pairing BuildEpisodeRef relies on in the other direction.
        if (episodeRef.Length > 2 && episodeRef[1] == 'P')
        {
            if (!TryReadDigits(episodeRef, 2, episodeRef.Length, out number))
            {
                return false;
            }

            season = 0;
            return true;
        }

        // "S{nn}E{nn}". The 'E' must be preceded by at least one digit and followed by at
        // least one; searching from index 1 also means a second 'E' anywhere falls out of
        // the digit checks below rather than being silently tolerated.
        var separator = episodeRef.IndexOf('E', 1);
        if (separator < 2 || separator == episodeRef.Length - 1)
        {
            return false;
        }

        if (!TryReadDigits(episodeRef, 1, separator, out var parsedSeason)
            || !TryReadDigits(episodeRef, separator + 1, episodeRef.Length, out number))
        {
            return false;
        }

        season = parsedSeason;
        return true;
    }

    /// <summary>
    /// Parses the half-open range [start, end) of a reference as an unsigned ASCII integer.
    /// </summary>
    private static bool TryReadDigits(string value, int start, int end, out int parsed)
    {
        parsed = 0;

        if (end <= start)
        {
            return false;
        }

        for (var i = start; i < end; i++)
        {
            var c = value[i];
            if (c < '0' || c > '9')
            {
                return false;
            }
        }

        return int.TryParse(
            value.AsSpan(start, end - start),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out parsed);
    }
}
