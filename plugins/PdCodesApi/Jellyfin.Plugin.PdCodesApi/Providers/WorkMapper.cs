using System;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.PdCodesApi.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
// SetProviderId is an extension method from MediaBrowser.Model.Entities.
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.PdCodesApi.Providers;

/// <summary>
/// Copies a v5 work onto a Jellyfin item.
/// </summary>
/// <remarks>
/// One place, so that "which v5 field feeds which Jellyfin field" is auditable. Every
/// mapping here is a normalized v5 field. Nothing reads <c>meta</c>: it is raw and
/// per-source, so <c>meta.score</c> is a RANK from MAL and a SCORE from TMDB, and a
/// single mapping of it would be upside down for half the catalog.
/// </remarks>
public static class WorkMapper
{
    /// <summary>
    /// Applies a work to an item.
    /// </summary>
    /// <typeparam name="T">The Jellyfin item type.</typeparam>
    /// <param name="result">The metadata result being built.</param>
    /// <param name="work">The resolved v5 work.</param>
    /// <param name="isMovieShaped">True for a Jellyfin Movie, false for a Series.</param>
    /// <param name="country">The configured country, used to pick a certification.</param>
    /// <param name="certainIdentification">
    /// Whether we are sure this is the right work. False for every title match.
    /// </param>
    public static void Apply<T>(
        MetadataResult<T> result,
        Work work,
        bool isMovieShaped,
        string? country,
        bool certainIdentification)
        where T : BaseItem
    {
        var item = result.Item;

        item.Name = work.Title;
        item.Overview = work.Synopsis;
        item.ProductionYear = work.Year;

        // score is documented as NORMALIZED - always "bigger is better", for every
        // source. This is the reason the plugin never touches meta.score.
        item.CommunityRating = work.Score;

        // OriginalTitle: the Japanese/native title when the catalog has one. Picked
        // from titles[] rather than from title, because title has already been rendered
        // in the negotiated language and is therefore usually NOT the original.
        item.OriginalTitle = PickOriginalTitle(work);

        if (work.Genres is not null)
        {
            foreach (var genre in work.Genres.Where(g => !string.IsNullOrWhiteSpace(g)))
            {
                item.AddGenre(genre);
            }
        }

        // Keywords go to Tags, not Genres. v5 keeps them separate on purpose - music
        // has keywords and no genres - and folding them together would put "based on a
        // manga" into a user's genre filter.
        if (work.Keywords is not null)
        {
            var tags = work.Keywords.Where(k => !string.IsNullOrWhiteSpace(k)).ToArray();
            if (tags.Length > 0)
            {
                item.Tags = (item.Tags ?? Array.Empty<string>()).Concat(tags).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }
        }

        if (work.Studios is not null)
        {
            foreach (var studio in work.Studios.Where(s => !string.IsNullOrWhiteSpace(s)))
            {
                item.AddStudio(studio);
            }
        }

        item.OfficialRating = PickCertification(work, country);
        item.PremiereDate = PickPremiereDate(work, country);

        // The ULID is the stable key for every future scan.
        if (!string.IsNullOrWhiteSpace(work.Id))
        {
            item.SetProviderId(PdCodesIds.WorkIdKey, work.Id);
        }

        // Third-party ids are written back ONLY when we are sure this is the right work.
        //
        // Two independent gates, and both are needed:
        //   1. Only `external_ids` is ever read - `uncertain_external_ids` is a title
        //      match and is never persisted under any setting.
        //   2. Even a confirmed id map is withheld when the IDENTIFICATION itself was a
        //      guess. Otherwise a fuzzy name match here writes an authoritative-looking
        //      IMDb id onto the item, and every other provider on the server - TMDB,
        //      OMDb, anything - then trusts it and pulls the wrong film's data. The
        //      guess would outlive this plugin's involvement entirely.
        //
        // The ULID above is still written, because it is OUR key: a wrong one is
        // correctable by re-identifying the item and misleads nothing else.
        if (certainIdentification)
        {
            foreach (var pair in PdCodesIds.ToJellyfinProviderIds(work, isMovieShaped))
            {
                item.SetProviderId(pair.Key, pair.Value);
            }
        }

        // Work.uncertain is deliberately NOT acted on here. It means "some field on this
        // work is a guess", which is a different claim from "this is the wrong work" -
        // gating on it would drop good metadata for a whole catalog whose German titles
        // happen to be inferred. Identification certainty is the parameter above.

        // ResultLanguage must say which language the strings above are actually IN, not
        // which one we asked for. The API tells us in `language`; using our request
        // instead would mislabel every fallback.
        result.ResultLanguage = work.Language;
        result.Provider = PdCodesIds.ProviderName;
        result.HasMetadata = true;
    }

    /// <summary>
    /// Picks the native-language title from <c>titles[]</c>.
    /// </summary>
    private static string? PickOriginalTitle(Work work)
    {
        if (work.Titles is null)
        {
            return null;
        }

        // "ja" before "ja-romaji": OriginalTitle means the original, and a romanization
        // is a transliteration of it, not it. "und" (undetermined) is a real ISO 639-2
        // code that MAL synonyms arrive under and is deliberately NOT treated as native.
        foreach (var lang in new[] { "ja", "ja-romaji" })
        {
            var hit = work.Titles.FirstOrDefault(t =>
                string.Equals(t.Lang, lang, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(t.Value));
            if (hit is not null)
            {
                return hit.Value;
            }
        }

        return null;
    }

    /// <summary>
    /// Picks an age rating, preferring the configured country.
    /// </summary>
    private static string? PickCertification(Work work, string? country)
    {
        if (work.Certifications is null || work.Certifications.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(country))
        {
            var local = work.Certifications.FirstOrDefault(c =>
                string.Equals(c.Country, country, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(c.Rating));
            if (local is not null)
            {
                return local.Rating;
            }
        }

        // No fallback to "some other country's rating". A German "FSK 16" shown as a US
        // rating is worse than no rating: parental-control rules act on this string.
        return null;
    }

    /// <summary>
    /// Picks a release date, preferring the configured country.
    /// </summary>
    private static DateTime? PickPremiereDate(Work work, string? country)
    {
        if (work.Releases is null || work.Releases.Count == 0)
        {
            return null;
        }

        Release? chosen = null;
        if (!string.IsNullOrWhiteSpace(country))
        {
            chosen = work.Releases.FirstOrDefault(r =>
                string.Equals(r.Country, country, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(r.Date));
        }

        // Here a fallback IS correct - unlike a certification, "the earliest known
        // release date" is a meaningful answer even from another country.
        chosen ??= work.Releases
            .Where(r => !string.IsNullOrWhiteSpace(r.Date))
            .OrderBy(r => r.Date, StringComparer.Ordinal)
            .FirstOrDefault();

        return ParseDate(chosen?.Date);
    }

    /// <summary>
    /// Parses a v5 date string.
    /// </summary>
    /// <param name="value">An ISO-8601 date or date-time, or null.</param>
    /// <returns>The parsed value, or null.</returns>
    /// <remarks>
    /// InvariantCulture and RoundtripKind, always. Parsing an ISO date under the
    /// server's locale is how "03/04/2009" becomes March in one deployment and April
    /// in another, with no error either time.
    /// </remarks>
    public static DateTime? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return parsed;
        }

        return null;
    }
}
