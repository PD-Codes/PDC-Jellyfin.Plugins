using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PdCodesApi.Api;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;
using JellyfinEpisode = MediaBrowser.Controller.Entities.TV.Episode;

namespace Jellyfin.Plugin.PdCodesApi.Providers;

/// <summary>
/// Metadata for individual episodes.
/// </summary>
/// <remarks>
/// Verified against Jellyfin 10.10.6: EpisodeInfo extends ItemLookupInfo and adds
/// SeriesProviderIds, SeasonProviderIds, IndexNumberEnd, IsMissingEpisode and
/// SeriesDisplayOrder. SeriesProviderIds is the load-bearing one here - an episode has
/// no id of its own, so the only way to reach /v5/{type}/{ulid}/episodes/{ref} is
/// through the ids of its PARENT SERIES.
///
/// There is no Season provider: v5 has no season entity, only episodes carrying a
/// season number. Claiming to provide Season metadata and then returning nothing on
/// every call would be a provider that looks installed and does nothing.
/// </remarks>
public class PdCodesEpisodeProvider : IRemoteMetadataProvider<JellyfinEpisode, EpisodeInfo>, IHasOrder
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PdCodesEpisodeProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PdCodesEpisodeProvider"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Injected by the host.</param>
    /// <param name="logger">Injected by the host.</param>
    public PdCodesEpisodeProvider(
        IHttpClientFactory httpClientFactory,
        ILogger<PdCodesEpisodeProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => PdCodesIds.ProviderName;

    /// <inheritdoc />
    public int Order => 10;

    /// <inheritdoc />
    public async Task<MetadataResult<JellyfinEpisode>> GetMetadata(
        EpisodeInfo info,
        CancellationToken cancellationToken)
    {
        var result = new MetadataResult<JellyfinEpisode> { HasMetadata = false };

        if (!PdCodesApiClient.IsConfigured)
        {
            return result;
        }

        if (!info.IndexNumber.HasValue)
        {
            // Without an episode number there is no reference to build. Bail rather than
            // fetching the whole episode list and trying to match on title.
            return result;
        }

        // The parent series must already carry our ULID. We do NOT re-identify the
        // series from here: doing so would run a name search per episode, which is both
        // a request storm and a chance for episode 7 of a season to bind to a different
        // work than episode 6 did.
        if (info.SeriesProviderIds is null
            || !info.SeriesProviderIds.TryGetValue(PdCodesIds.WorkIdKey, out var seriesUlid)
            || string.IsNullOrWhiteSpace(seriesUlid))
        {
            return result;
        }

        if (!PdCodesIds.LooksLikeUlid(seriesUlid))
        {
            _logger.LogError(
                "Parent series carries a {Key} id of '{Value}', which is not a ULID. Skipping episode metadata.",
                PdCodesIds.WorkIdKey,
                seriesUlid);
            return result;
        }

        var builtRef = PdCodesIds.BuildEpisodeRef(info.ParentIndexNumber, info.IndexNumber.Value);

        // The PRIMARY reference. A reference an earlier scan stored wins over one we build -
        // it is what stops the absolute fallback below from paying its extra request on
        // every single scan of the same file - but ONLY while it still describes this file.
        var primaryRef = builtRef;
        var storedRef = StoredEpisodeRef(info);

        if (storedRef is not null)
        {
            if (StoredRefMatchesItem(storedRef, info.ParentIndexNumber, info.IndexNumber.Value))
            {
                primaryRef = storedRef;
            }
            else
            {
                _logger.LogInformation(
                    "Discarding the stored PD-Codes episode reference {StoredRef} on '{Name}' of series {Ulid}: it "
                    + "does not describe this file's current numbers (season {Season}, episode {Number}). Rebuilding "
                    + "the reference from those numbers instead.",
                    storedRef,
                    info.Name,
                    seriesUlid,
                    info.ParentIndexNumber,
                    info.IndexNumber.Value);
            }
        }

        // Validate on the axis THE REFERENCE is on, not on Jellyfin's. A stored "E62" is an
        // absolute reference and must be checked as one even though Jellyfin files that file
        // under season 1; checking it against ParentIndexNumber=1 would make the reference
        // fail its own validation on every scan, forever, silently, on exactly the works the
        // fallback exists to serve. For every non-absolute reference - "S01E62", "SP3" - this
        // is Jellyfin's season number, i.e. unchanged behaviour.
        var primarySeason = PdCodesIds.IsAbsoluteRef(primaryRef) ? null : info.ParentIndexNumber;

        var fallbackEnabled = Plugin.Instance?.Configuration.AbsoluteNumberingFallback ?? true;

        // Season 1 ONLY, deliberately. See the remarks on TryAbsoluteFallbackAsync.
        var fallbackEligible = fallbackEnabled
            && info.ParentIndexNumber == 1
            && !PdCodesIds.IsAbsoluteRef(primaryRef);

        var client = new PdCodesApiClient(_httpClientFactory, _logger);

        try
        {
            // Both candidate types are tried, for the same reason as everywhere else: an
            // anime series and a live-action series are both Jellyfin Series, and only
            // the v5 type segment tells them apart in the URL.
            foreach (var type in PdCodesIds.SeriesTypes())
            {
                var envelope = await client
                    .GetEpisodeEnvelopeAsync(type, seriesUlid, primaryRef, info.MetadataLanguage, cancellationToken)
                    .ConfigureAwait(false);

                var episode = envelope?.Data;

                // Verify the episode we got back is the one we asked for. See
                // AgreesWithRequest() for why this is not paranoia.
                if (episode is not null
                    && AgreesWithRequest(episode, primarySeason, info.IndexNumber.Value, primaryRef, _logger))
                {
                    Apply(result, episode, info, seriesUlid, primaryRef, _logger);
                    return result;
                }
            }

            // The fallback runs ONCE, here, after every candidate type has missed - not
            // inside the loop under the anime candidate. Inside the loop, every live-action
            // Series in the library would pay an extra "anime" request per episode before
            // "tv" - the type that will actually answer - had even been tried. It is scoped
            // to the anime type because that is the only medium with this filing convention.
            if (fallbackEligible)
            {
                var absoluteRef = PdCodesIds.BuildAbsoluteEpisodeRef(info.IndexNumber.Value);

                var fallbackEpisode = await TryAbsoluteFallbackAsync(
                    client,
                    PdCodesIds.TypeAnime,
                    seriesUlid,
                    primaryRef,
                    absoluteRef,
                    info.IndexNumber.Value,
                    info.MetadataLanguage,
                    cancellationToken)
                    .ConfigureAwait(false);

                if (fallbackEpisode is not null)
                {
                    Apply(result, fallbackEpisode, info, seriesUlid, absoluteRef, _logger);
                    return result;
                }
            }

            return result;
        }
        catch (PdCodesApiException ex)
        {
            _logger.LogError(
                ex,
                "PD-Codes API v5 request failed for episode {Ref} of series {Ulid}.",
                primaryRef,
                seriesUlid);
            return new MetadataResult<JellyfinEpisode> { HasMetadata = false };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Could not reach the PD-Codes API for episode {Ref}.", primaryRef);
            return new MetadataResult<JellyfinEpisode> { HasMetadata = false };
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "PD-Codes API timed out for episode {Ref}.", primaryRef);
            return new MetadataResult<JellyfinEpisode> { HasMetadata = false };
        }
    }

    /// <summary>
    /// Returns the episode reference stored on the item by an earlier scan, or null.
    /// </summary>
    private static string? StoredEpisodeRef(EpisodeInfo info)
    {
        if (info.ProviderIds is not null
            && info.ProviderIds.TryGetValue(PdCodesIds.EpisodeRefKey, out var stored)
            && !string.IsNullOrWhiteSpace(stored))
        {
            return stored.Trim();
        }

        return null;
    }

    /// <summary>
    /// Checks that a reference read out of the database still describes the item's numbers.
    /// </summary>
    /// <param name="storedRef">The reference stored by an earlier scan.</param>
    /// <param name="parentIndexNumber">The item's season number as Jellyfin holds it now.</param>
    /// <param name="indexNumber">The item's episode number as Jellyfin holds it now.</param>
    /// <returns>True when the stored reference may still be used.</returns>
    /// <remarks>
    /// WHY THIS EXISTS: a stored reference wins outright over a built one, and
    /// <see cref="AgreesWithRequest"/> deliberately does NOT compare episode numbers - which
    /// is correct for a reference we just built from those very numbers, and wrong for one
    /// that came out of the database. Without this check a user who renumbers a file, renames
    /// it, or hand-edits the id in the identify dialog keeps the OLD reference forever and is
    /// served episode 1's synopsis on episode 5, at full confidence, with nothing logged.
    /// Before the reference was stored at all it was rebuilt from the numbers on every scan,
    /// so a renumber self-corrected; this is what restores that property.
    ///
    /// A reference that does not parse is likewise refused: it is not a string this plugin
    /// wrote, so nothing is known about what it addresses.
    ///
    /// The season clause is asymmetric on purpose. A parsed season must equal
    /// ParentIndexNumber ("SP3" requires season 0, "S02E01" requires season 2), but a parsed
    /// ABSOLUTE reference carries no season and is consistent whenever the number matches -
    /// that is the whole point of it. Jellyfin files absolutely-numbered anime under season 1,
    /// so requiring agreement there would reject the fallback's own stored form every time.
    ///
    /// Consequence, stated because it looks like dead code otherwise: once a stored SEASONAL
    /// reference has passed this check it is necessarily character-identical to the one
    /// BuildEpisodeRef would produce from the same numbers. The stored-reference path
    /// therefore only ever changes behaviour for the absolute form - which is exactly the
    /// case it was added for, and is intended.
    /// </remarks>
    internal static bool StoredRefMatchesItem(string? storedRef, int? parentIndexNumber, int indexNumber)
    {
        if (!PdCodesIds.TryParseEpisodeRef(storedRef, out var season, out var number))
        {
            return false;
        }

        if (number != indexNumber)
        {
            return false;
        }

        return !season.HasValue || season.Value == parentIndexNumber;
    }

    /// <summary>
    /// Retries a missed seasonal reference as the absolute reference "E{n}".
    /// </summary>
    /// <returns>The episode, only when the retry could be PROVEN correct; otherwise null.</returns>
    /// <remarks>
    /// WHY THIS EXISTS: anime released with absolutely-numbered files is normally filed by
    /// Jellyfin under Season 1, so this plugin asks for "S01E62". Under TMDB numbering a
    /// season does not restart at 1 - One Piece season 2 is episodes 62-77 - so on such a
    /// work "S01E62" and "E62" are different episodes and the seasonal form is simply
    /// absent. Without the retry the file gets no metadata and nothing says why.
    ///
    /// WHY SEASON 1 ONLY: season 1 (and "no season at all", which BuildEpisodeRef already
    /// answers with the absolute form) is where Jellyfin puts absolutely-numbered files. A
    /// file the user deliberately placed in Season 3 that misses is far more likely to be a
    /// genuine gap in the catalog than a numbering-convention mismatch, and re-reading its
    /// number as an absolute one would attach a confidently wrong episode - the wrong plot
    /// summary, spoiling the show, reported as a bug against nothing. Widening this is not
    /// a small change.
    ///
    /// WHY THE numbering CHECK IS NOT OPTIONAL: a work whose numbering is continuous would
    /// have answered the seasonal reference in the first place. A miss on "S01E62" followed
    /// by a hit on "E62" on a work that declares continuous numbering is a CONTRADICTION,
    /// and a contradiction must not be resolved by picking the side that produced an answer.
    /// An absent numbering block is treated the same way: unproven, therefore refused. This
    /// is the whole difference between a fallback and a guess.
    ///
    /// WHY THE CALLER RUNS THIS ONCE, AFTER THE TYPE LOOP: the retry costs a request, and the
    /// condition it answers ("this work numbers its episodes straight through") is a property
    /// of the work, not of which candidate type is being tried. Called from inside the loop it
    /// would fire under the "anime" candidate for every live-action series in the library,
    /// before "tv" - the type that will actually answer - had been asked at all.
    /// </remarks>
    private async Task<Api.Episode?> TryAbsoluteFallbackAsync(
        PdCodesApiClient client,
        string type,
        string seriesUlid,
        string primaryRef,
        string absoluteRef,
        int episodeNumber,
        string? metadataLanguage,
        CancellationToken cancellationToken)
    {
        var envelope = await client
            .GetEpisodeEnvelopeAsync(type, seriesUlid, absoluteRef, metadataLanguage, cancellationToken)
            .ConfigureAwait(false);

        var episode = envelope?.Data;
        if (episode is null)
        {
            return null;
        }

        if (envelope!.Numbering is not { Continuous: false })
        {
            _logger.LogWarning(
                "PD-Codes API v5 did not answer {PrimaryRef} for {Ulid} but did answer {AbsoluteRef}, while "
                + "reporting that work's numbering as continuous or not reporting it at all. Refusing the "
                + "fallback: on a continuously numbered work those two references address the same episode, "
                + "so this is a contradiction and not something to resolve by picking a side.",
                primaryRef,
                seriesUlid,
                absoluteRef);
            return null;
        }

        // Takes episodeNumber as a plain int, not the EpisodeInfo it came from: the caller
        // only reaches this method after its own IndexNumber.HasValue check, but that
        // guarantee does not cross a method boundary, and re-deriving ".Value" in here from
        // a nullable field the compiler cannot see was proven would be exactly the kind of
        // "this can't happen" this project's CLAUDE.md says to make structurally impossible
        // instead of asserting. Taking the already-unwrapped int does that: there is no
        // nullable value in this method left to be wrong about.
        if (!AgreesWithRequest(episode, requestedSeason: null, episodeNumber, absoluteRef, _logger))
        {
            return null;
        }

        _logger.LogInformation(
            "PD-Codes API v5 episode metadata for {Ulid} came from the absolute-numbering fallback: "
            + "{PrimaryRef} missed, {AbsoluteRef} answered and the work's numbering is non-continuous.",
            seriesUlid,
            primaryRef,
            absoluteRef);

        return episode;
    }

    /// <summary>
    /// Fills the metadata result from an episode that has already been accepted.
    /// </summary>
    private static void Apply(
        MetadataResult<JellyfinEpisode> result,
        Api.Episode episode,
        EpisodeInfo info,
        string seriesUlid,
        string usedRef,
        ILogger logger)
    {
        if (episode.IsUnaligned)
        {
            // The merger could not line this episode up across its sources. The
            // data is still the right episode - `ref` addressed it - but a reader
            // chasing an odd title deserves the trail.
            logger.LogDebug(
                "PD-Codes API v5 episode {Ref} of {Ulid} is flagged unaligned across its sources.",
                episode.Ref,
                seriesUlid);
        }

        result.Item = new JellyfinEpisode
        {
            Name = episode.Title,
            Overview = episode.Synopsis,
            IndexNumber = info.IndexNumber,
            ParentIndexNumber = info.ParentIndexNumber,
        };

        result.Item.PremiereDate = WorkMapper.ParseDate(episode.Aired);
        result.Item.ProductionYear = result.Item.PremiereDate?.Year;

        if (episode.Duration.HasValue && episode.Duration.Value > 0)
        {
            // v5's duration is in SECONDS - the contract says so explicitly, and
            // notes TMDB's minutes are converted upstream. Jellyfin wants ticks.
            // Treating this as minutes would give every episode a 24-hour runtime.
            result.Item.RunTimeTicks = TimeSpan
                .FromSeconds(episode.Duration.Value)
                .Ticks;
        }

        // v5's episode_absolute is deliberately NOT written anywhere. Verified
        // against Jellyfin 10.10.6: MediaBrowser.Controller/Entities/TV/Episode.cs
        // declares AirsBeforeSeasonNumber, AirsAfterSeasonNumber,
        // AirsBeforeEpisodeNumber, IndexNumberEnd and AiredSeasonNumber - there is
        // no AbsoluteEpisodeNumber property to put it in, and overwriting
        // IndexNumber with it would renumber the user's library.

        if (!string.IsNullOrWhiteSpace(usedRef))
        {
            // Store the reference WE USED, not the one the API answered with
            // (episode.Ref), even though the latter is tempting.
            //
            // The stored reference has exactly one job: to be re-fetchable NEXT SCAN,
            // GIVEN THE NUMBERS JELLYFIN HOLDS FOR THIS FILE. The API builds its own
            // `ref` on the SEASON axis whenever the episode has a season and a position
            // - One Piece episode 62 comes back as "S02E01" even when it was fetched as
            // "E62". Those two axes diverge precisely when numbering is non-continuous,
            // which is precisely the case the absolute fallback exists for.
            //
            // Storing the API's ref there would mean the next scan replays "S02E01"
            // against Jellyfin's Season 1, gets refused by AgreesWithRequest, and pays
            // the fallback's extra request again - forever, not once. Worse, if the
            // operator later turns the fallback off, those episodes lose their metadata
            // entirely, because the only reference on the item is one that cannot be
            // reached from the numbers Jellyfin has.
            result.Item.SetProviderId(PdCodesIds.EpisodeRefKey, usedRef);
        }

        result.ResultLanguage = episode.Language;
        result.Provider = PdCodesIds.ProviderName;
        result.HasMetadata = true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Empty on purpose. Jellyfin's identify dialog for an episode expects a list of
    /// candidate episodes; v5 addresses an episode by an exact reference under a known
    /// parent, so there is nothing to search. Returning fabricated candidates would put
    /// a chooser in front of the user with nothing real behind it.
    /// </remarks>
    public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(
        EpisodeInfo searchInfo,
        CancellationToken cancellationToken)
        => Task.FromResult<IEnumerable<RemoteSearchResult>>(Array.Empty<RemoteSearchResult>());

    /// <inheritdoc />
    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(MediaBrowser.Common.Net.NamedClient.Default);
        return client.GetAsync(new Uri(url), cancellationToken);
    }

    /// <summary>
    /// Checks that the episode the API returned is structurally the one that was requested.
    /// </summary>
    /// <remarks>
    /// WHY THIS EXISTS, since it looks redundant: the episode is addressed by an exact
    /// reference, so the server decides which episode "S02E01" or "E62" means and this
    /// plugin never matches numbers against a field itself. That is the correct division
    /// of labor - the alternative, fetching the episode LIST and matching Jellyfin's
    /// season/episode against a field, is a trap. Under TMDB numbering a season does not
    /// always restart at 1 (One Piece season 2 is episodes 62-77), so matching Jellyfin's
    /// S02E01 against <c>episode_in_season</c> and against <c>episode</c> select DIFFERENT
    /// episodes, and which one is right depends on <c>numbering.continuous</c> for that
    /// work. Picking either field unconditionally is wrong for half the catalog, silently.
    ///
    /// What this guard adds is a check that the answer came back on the axis we asked on:
    /// if we requested a special we must get a special, and if we requested a numbered
    /// season we must not get one. A mismatch means the reference was interpreted on a
    /// different axis than intended, and the result is rejected rather than attached -
    /// a missing episode description is recoverable; the wrong episode's plot summary
    /// spoiling a show is not, and nobody would report it as a bug against this plugin.
    ///
    /// Deliberately NOT checked: that <c>episode</c> equals the requested number. Which
    /// field an "SxxEyy" reference resolves against is the server's business and varies
    /// with <c>numbering.continuous</c>; asserting it here would reject correct answers
    /// on exactly the non-continuous works this care is for.
    ///
    /// That omission is right for a reference this plugin JUST BUILT from the item's numbers,
    /// and wrong for one read back out of the database, which may predate a renumber or a
    /// hand edit. <see cref="StoredRefMatchesItem"/> is the check that covers the second case,
    /// before the request is made rather than after - do not "fix" it by adding a number
    /// comparison here.
    /// </remarks>
    internal static bool AgreesWithRequest(
        Api.Episode episode,
        int? requestedSeason,
        int requestedNumber,
        string episodeRef,
        ILogger logger)
    {
        var requestedSpecial = requestedSeason == 0;

        if (requestedSpecial != episode.IsSpecial)
        {
            logger.LogWarning(
                "PD-Codes API v5 answered reference {Ref} with an episode whose is_special={Actual}, but "
                + "{Expected} was requested. Refusing it rather than attaching a possibly unrelated "
                + "episode.",
                episodeRef,
                episode.IsSpecial,
                requestedSpecial);
            return false;
        }

        // A numbered season was requested and the answer carries a different one.
        if (!requestedSpecial
            && requestedSeason.HasValue
            && episode.Season.HasValue
            && episode.Season.Value != requestedSeason.Value)
        {
            logger.LogWarning(
                "PD-Codes API v5 answered reference {Ref} with an episode from season {Actual}. Refusing it.",
                episodeRef,
                episode.Season.Value);
            return false;
        }

        // An absolute reference "E{n}" was sent (Jellyfin had no season number). The
        // answer must be that absolute episode, on whichever field the work carries it.
        if (!requestedSpecial && !requestedSeason.HasValue)
        {
            var absolute = episode.EpisodeAbsolute ?? episode.EpisodeNumber;
            if (absolute.HasValue && absolute.Value != requestedNumber)
            {
                logger.LogWarning(
                    "PD-Codes API v5 answered absolute reference {Ref} with episode {Actual}. Refusing it.",
                    episodeRef,
                    absolute.Value);
                return false;
            }
        }

        return true;
    }
}
