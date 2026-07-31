using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PdCodesApi.Api;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PdCodesApi.Providers;

/// <summary>
/// Metadata for Jellyfin Series.
/// </summary>
/// <remarks>
/// Interface signatures verified against Jellyfin 10.10.6 - see PdCodesMovieProvider for
/// the citation; it is the same generic interface with Series/SeriesInfo substituted.
///
/// A Jellyfin Series maps to v5 "anime" OR v5 "tv". Anime is not a Jellyfin item type:
/// Death Note is a Series whose v5 medium happens to be "anime", and the v5 type is
/// what selects the URL segment, not what selects the Jellyfin class.
/// </remarks>
public class PdCodesSeriesProvider : IRemoteMetadataProvider<Series, SeriesInfo>, IHasOrder
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PdCodesSeriesProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PdCodesSeriesProvider"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Injected by the host.</param>
    /// <param name="logger">Injected by the host.</param>
    public PdCodesSeriesProvider(
        IHttpClientFactory httpClientFactory,
        ILogger<PdCodesSeriesProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => PdCodesIds.ProviderName;

    /// <inheritdoc />
    public int Order => 10;

    /// <inheritdoc />
    public async Task<MetadataResult<Series>> GetMetadata(
        SeriesInfo info,
        CancellationToken cancellationToken)
    {
        var result = new MetadataResult<Series> { HasMetadata = false };

        if (!PdCodesApiClient.IsConfigured)
        {
            _logger.LogWarning(
                "PD-Codes API base URL is not configured; skipping '{Name}'.",
                info.Name);
            return result;
        }

        var client = new PdCodesApiClient(_httpClientFactory, _logger);
        var resolver = new WorkResolver(client, _logger);

        try
        {
            var resolved = await resolver
                .ResolveAsync(info, PdCodesIds.SeriesTypes(), isMovieShaped: false, allowNameSearch: true, cancellationToken)
                .ConfigureAwait(false);

            if (resolved is null)
            {
                return result;
            }

            var work = resolved.Value.Work;
            result.Item = new Series();
            result.QueriedById = info.ProviderIds is not null
                && info.ProviderIds.ContainsKey(PdCodesIds.WorkIdKey);

            // See PdCodesMovieProvider: Apply does no I/O and sets HasMetadata last, so a
            // partially-populated result can never escape with HasMetadata = true.
            WorkMapper.Apply(
                result,
                work,
                isMovieShaped: false,
                Plugin.Instance?.Configuration.Country,
                resolved.Value.Certain);

            result.Item.Status = MapStatus(work);

            return result;
        }
        catch (PdCodesApiException ex)
        {
            _logger.LogError(ex, "PD-Codes API v5 request failed while identifying series '{Name}'.", info.Name);
            return new MetadataResult<Series> { HasMetadata = false };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Could not reach the PD-Codes API while identifying series '{Name}'.", info.Name);
            return new MetadataResult<Series> { HasMetadata = false };
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "PD-Codes API timed out while identifying series '{Name}'.", info.Name);
            return new MetadataResult<Series> { HasMetadata = false };
        }
    }

    /// <inheritdoc />
    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(
        SeriesInfo searchInfo,
        CancellationToken cancellationToken)
    {
        if (!PdCodesApiClient.IsConfigured || string.IsNullOrWhiteSpace(searchInfo.Name))
        {
            return Array.Empty<RemoteSearchResult>();
        }

        var client = new PdCodesApiClient(_httpClientFactory, _logger);
        var results = new List<RemoteSearchResult>();

        try
        {
            foreach (var type in PdCodesIds.SeriesTypes())
            {
                var hits = await client
                    .SearchAsync(searchInfo.Name, type, searchInfo.MetadataLanguage, cancellationToken)
                    .ConfigureAwait(false);
                results.AddRange(hits.Select(w =>
                    PdCodesMovieProvider.ToSearchResult(w, isMovieShaped: false)));
            }
        }
        catch (PdCodesApiException ex)
        {
            _logger.LogError(ex, "PD-Codes API v5 search failed for '{Name}'.", searchInfo.Name);
            return Array.Empty<RemoteSearchResult>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Could not reach the PD-Codes API searching for '{Name}'.", searchInfo.Name);
            return Array.Empty<RemoteSearchResult>();
        }

        return results;
    }

    /// <inheritdoc />
    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(MediaBrowser.Common.Net.NamedClient.Default);
        return client.GetAsync(new Uri(url), cancellationToken);
    }

    /// <summary>
    /// Maps v5's normalized status onto Jellyfin's three-valued SeriesStatus.
    /// </summary>
    /// <remarks>
    /// Reads the NORMALIZED <c>status</c> / <c>is_ongoing</c> fields, never
    /// <c>meta.status</c>. The raw values are per-source strings - "Currently Airing"
    /// from MAL, "FINISHED" from AniList, "Ended" from TMDB - and a switch over them
    /// matches nothing for whichever sources it was not written against.
    ///
    /// Returns null rather than guessing when the status is unrecognized, because
    /// SeriesStatus has no "unknown" member and defaulting to Ended would stop Jellyfin
    /// from ever looking for new episodes of a running show.
    /// </remarks>
    private static SeriesStatus? MapStatus(Work work)
    {
        if (work.IsOngoing)
        {
            return SeriesStatus.Continuing;
        }

        return work.Status?.ToUpperInvariant() switch
        {
            "AIRING" => SeriesStatus.Continuing,
            "RELEASING" => SeriesStatus.Continuing,
            "FINISHED" => SeriesStatus.Ended,
            "COMPLETED" => SeriesStatus.Ended,
            "UPCOMING" => SeriesStatus.Unreleased,
            "NOT_YET_RELEASED" => SeriesStatus.Unreleased,
            _ => null,
        };
    }
}
