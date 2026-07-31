using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PdCodesApi.Api;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PdCodesApi.Providers;

/// <summary>
/// Turns whatever Jellyfin knows about an item into exactly one v5 work, or nothing.
/// </summary>
/// <remarks>
/// Shared by the metadata and image providers so the identification rules exist once.
/// The order is deliberate and is the whole point of the class:
///
///   1. Our own ULID, if a previous scan stored one. One request, no ambiguity.
///   2. An id Jellyfin already holds, through /v5/lookup with the NAMESPACED source
///      key and a ?type= scope. This is where a wrong namespace resolves silently to
///      a different work, so PdCodesIds owns that mapping and nothing else duplicates it.
///   3. A name search, last, because a name is the weakest evidence there is.
///
/// Every step either produces a work or produces nothing. There is no step that
/// produces a partially-identified item.
/// </remarks>
public sealed class WorkResolver
{
    private readonly PdCodesApiClient _client;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="WorkResolver"/> class.
    /// </summary>
    /// <param name="client">API client.</param>
    /// <param name="logger">Logger.</param>
    public WorkResolver(PdCodesApiClient client, ILogger logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <summary>
    /// Resolves the work behind a lookup info.
    /// </summary>
    /// <param name="info">Jellyfin's lookup info for the item.</param>
    /// <param name="candidateTypes">v5 types this Jellyfin item shape may map to.</param>
    /// <param name="isMovieShaped">True for a Jellyfin Movie, false for a Series.</param>
    /// <param name="allowNameSearch">Whether to fall back to a name search.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The work, the v5 type it was found under, and whether the IDENTIFICATION was
    /// confirmed rather than title-matched. Null when nothing could be resolved.
    /// </returns>
    /// <remarks>
    /// Certainty is returned rather than discarded because it governs a second decision
    /// further downstream: an identification we are not sure of must not have its
    /// external ids written into Jellyfin's ProviderIds, or a guess made here is
    /// published to every other metadata provider on the server as an established fact.
    /// </remarks>
    public async Task<ResolvedWork?> ResolveAsync(
        ItemLookupInfo info,
        IReadOnlyList<string> candidateTypes,
        bool isMovieShaped,
        bool allowNameSearch,
        CancellationToken cancellationToken)
    {
        // Step 1: our own id. Nothing else can be as reliable, and this is the path
        // every re-scan of an already-identified item takes - one request, total.
        if (info.ProviderIds is not null
            && info.ProviderIds.TryGetValue(PdCodesIds.WorkIdKey, out var storedId)
            && !string.IsNullOrWhiteSpace(storedId))
        {
            if (!PdCodesIds.LooksLikeUlid(storedId))
            {
                // Loud, because the item will otherwise stay unidentifiable forever and
                // the only visible symptom is a 404 against a URL nobody is looking at.
                _logger.LogError(
                    "Item '{Name}' has a {Key} provider id of '{Value}', which is not a 26-character "
                    + "ULID. v5 ids are ULIDs; a numeric value here is a MAL or TMDB id written into "
                    + "the wrong field. Clear it and re-identify the item.",
                    info.Name,
                    PdCodesIds.WorkIdKey,
                    storedId);
                return null;
            }

            foreach (var type in candidateTypes)
            {
                var work = await _client
                    .GetWorkAsync(type, storedId, info.MetadataLanguage, cancellationToken)
                    .ConfigureAwait(false);
                if (work is not null)
                {
                    // Certain: the ULID is our own primary key. It cannot be a title match.
                    return new ResolvedWork(work, work.Type ?? type, Certain: true);
                }
            }

            // A stored ULID that resolves under neither candidate type is a real
            // problem, not a reason to quietly fall through to a name search and
            // rebind the item to something else.
            _logger.LogWarning(
                "Stored PD-Codes id {Ulid} for '{Name}' resolved under none of [{Types}]. Not falling back "
                + "to a name search: that would silently rebind the item to a different work.",
                storedId,
                info.Name,
                string.Join(", ", candidateTypes));
            return null;
        }

        // Step 2: ids Jellyfin already holds.
        var pairs = PdCodesIds.ToLookupPairs(info.ProviderIds, isMovieShaped);
        foreach (var (source, id) in pairs)
        {
            foreach (var type in candidateTypes)
            {
                LookupEnvelope? envelope;
                try
                {
                    envelope = await _client
                        .LookupAsync(source, id, type, info.MetadataLanguage, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (PdCodesApiException ex)
                {
                    // 404/409/400 are already handled inside the client and come back as
                    // null. Anything that reaches here is unexpected - surface it and
                    // stop, rather than trying the next source against a broken API.
                    _logger.LogError(
                        ex,
                        "Unexpected PD-Codes API v5 failure resolving '{Name}' via {Source}/{Id}.",
                        info.Name,
                        source,
                        id);
                    return null;
                }

                var work = envelope?.Data;
                if (work is null)
                {
                    continue;
                }

                // Absent `matched` is treated as NOT certain. The client defaults
                // LookupMatch.Certain to false for the same reason: an envelope we could
                // not read must fail toward caution, not toward confidence.
                var certain = envelope?.Matched?.Certain ?? false;

                if (!certain
                    && !(Plugin.Instance?.Configuration.AcceptUncertainMatches ?? false))
                {
                    // The API told us plainly that this match is a guess. Honoring that
                    // is the entire value of the flag: accepting it writes a title match
                    // into ProviderIds, and the next scan will never question it.
                    _logger.LogInformation(
                        "PD-Codes API v5 matched '{Name}' via {Source}/{Id} but reported certain=false. "
                        + "Skipping. Enable 'Accept uncertain matches' in the plugin settings to use it.",
                        info.Name,
                        source,
                        id);
                    continue;
                }

                _logger.LogDebug(
                    "Resolved '{Name}' to {Type} {Ulid} via {Source}/{Id} (certain={Certain}).",
                    info.Name,
                    work.Type,
                    work.Id,
                    source,
                    id,
                    certain);
                return new ResolvedWork(work, work.Type ?? type, certain);
            }
        }

        if (!allowNameSearch || string.IsNullOrWhiteSpace(info.Name))
        {
            return null;
        }

        // Step 3: name search. Only ever reached for an item with no usable id.
        return await ResolveByNameAsync(info, candidateTypes, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Last-resort name search across the candidate types.
    /// </summary>
    /// <remarks>
    /// A name search result is a TITLE MATCH. That is the same kind of evidence as
    /// <c>uncertain_external_ids</c> and <c>tmdb_*_id_uncertain</c>, so it is gated by
    /// the same AcceptUncertainMatches setting - and it is reported to the caller as
    /// NOT certain even when the setting lets it through, so that its external ids are
    /// still never written back as fact.
    ///
    /// With the setting off, a hit is only accepted when the name matches EXACTLY
    /// (case- and whitespace-insensitively) and the year, if Jellyfin knows one, agrees.
    /// Without that rule the setting was a lie: it refused a guess the API was honest
    /// enough to label, while accepting a fuzzier guess this plugin made itself.
    /// </remarks>
    private async Task<ResolvedWork?> ResolveByNameAsync(
        ItemLookupInfo info,
        IReadOnlyList<string> candidateTypes,
        CancellationToken cancellationToken)
    {
        foreach (var type in candidateTypes)
        {
            IReadOnlyList<SearchNameHit> hits;
            try
            {
                hits = await _client
                    .SearchNameAsync(info.Name, type, info.MetadataLanguage, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (PdCodesApiException ex)
            {
                _logger.LogError(
                    ex,
                    "PD-Codes API v5 name search for '{Name}' ({Type}) failed.",
                    info.Name,
                    type);
                return null;
            }

            if (hits.Count == 0)
            {
                continue;
            }

            // When Jellyfin knows the year, require it to agree. A name search that
            // returns three remakes and picks the first is the classic wrong-match, and
            // the year is the one extra bit of evidence available for free.
            var chosen = hits[0];
            if (info.Year.HasValue)
            {
                var yearMatches = hits.Where(h => h.Year == info.Year.Value).ToList();
                if (yearMatches.Count == 0)
                {
                    _logger.LogInformation(
                        "PD-Codes API v5 name search for '{Name}' ({Type}) returned {Count} hits, none from "
                        + "{Year}. Not guessing.",
                        info.Name,
                        type,
                        hits.Count,
                        info.Year.Value);
                    continue;
                }

                if (yearMatches.Count > 1)
                {
                    _logger.LogInformation(
                        "PD-Codes API v5 name search for '{Name}' ({Type}) returned {Count} hits from {Year}. "
                        + "Ambiguous; not guessing. Identify the item manually.",
                        info.Name,
                        type,
                        yearMatches.Count,
                        info.Year.Value);
                    continue;
                }

                chosen = yearMatches[0];
            }
            else if (hits.Count > 1)
            {
                // No year to arbitrate with and more than one candidate. Refusing here
                // costs an unidentified item; choosing costs a wrong one that nobody
                // will notice until they play it.
                _logger.LogInformation(
                    "PD-Codes API v5 name search for '{Name}' ({Type}) returned {Count} hits and the item has "
                    + "no year to disambiguate with. Not guessing.",
                    info.Name,
                    type,
                    hits.Count);
                continue;
            }

            // internal_id, not id. It is the v5 ULID and the only field on a search-name
            // row that can address the work.
            if (string.IsNullOrWhiteSpace(chosen.InternalId))
            {
                continue;
            }

            var acceptUncertain = Plugin.Instance?.Configuration.AcceptUncertainMatches ?? false;
            if (!acceptUncertain && !NamesMatchExactly(info.Name, chosen.Name))
            {
                _logger.LogInformation(
                    "PD-Codes API v5 name search for '{Name}' ({Type}) best hit is '{Hit}', which is not an "
                    + "exact title match. Skipping. Enable 'Accept uncertain matches' to allow "
                    + "approximate name matches.",
                    info.Name,
                    type,
                    chosen.Name);
                continue;
            }

            if (!acceptUncertain && chosen.HasOnlyUncertainTmdb)
            {
                // The row's only TMDB evidence is itself a title match. Accepting it and
                // then writing that id back would launder a guess into a fact.
                _logger.LogInformation(
                    "PD-Codes API v5 name search hit '{Hit}' carries only a title-matched TMDB id. Skipping.",
                    chosen.Name);
                continue;
            }

            // Fetch under the type WE asked for, not the row's own `type`. The search was
            // already scoped by ?type=, so they should agree; if they do not, trusting the
            // payload over our own scope is how a manga row gets fetched as an anime.
            var work = await _client
                .GetWorkAsync(type, chosen.InternalId, info.MetadataLanguage, cancellationToken)
                .ConfigureAwait(false);
            if (work is not null)
            {
                // Certain: false, ALWAYS. This is a title match however strict the rule
                // that let it through, so its external ids must not be published.
                return new ResolvedWork(work, work.Type ?? type, Certain: false);
            }
        }

        return null;
    }

    /// <summary>
    /// Compares two titles for exact equality, ignoring case and surrounding whitespace.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT fuzzy. Any normalization beyond trimming and case - stripping
    /// punctuation, collapsing "&amp;" and "and", dropping a year suffix - turns this
    /// guard back into the approximate matcher it exists to prevent.
    /// </remarks>
    private static bool NamesMatchExactly(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return false;
        }

        return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// The outcome of identification: which work, under which v5 type, and how sure we are.
/// </summary>
/// <param name="Work">The resolved work.</param>
/// <param name="Type">The v5 type it was found under.</param>
/// <param name="Certain">
/// True only when the identification came from our own ULID or from a lookup the API
/// itself reported as <c>certain: true</c>. False for every title match.
/// </param>
public readonly record struct ResolvedWork(Work Work, string Type, bool Certain);
