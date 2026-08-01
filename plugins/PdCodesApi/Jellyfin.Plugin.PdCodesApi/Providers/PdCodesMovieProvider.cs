using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PdCodesApi.Api;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
// SetProviderId / GetProviderId are extension methods on IHasProviderIds and live in
// MediaBrowser.Model.Entities.ProviderIdsExtensions - easy using to forget, and the
// error it produces ("no definition for SetProviderId") points at the wrong thing.
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PdCodesApi.Providers;

/// <summary>
/// Metadata for Jellyfin Movies.
/// </summary>
/// <remarks>
/// Verified against Jellyfin 10.10.6
/// (MediaBrowser.Controller/Providers/IRemoteMetadataProvider.cs):
///   IRemoteMetadataProvider&lt;TItemType, TLookupInfoType&gt;
///     : IMetadataProvider&lt;TItemType&gt;, IRemoteSearchProvider&lt;TLookupInfoType&gt;
///   Task&lt;MetadataResult&lt;TItemType&gt;&gt; GetMetadata(TLookupInfoType, CancellationToken)
///   Task&lt;IEnumerable&lt;RemoteSearchResult&gt;&gt; GetSearchResults(TLookupInfoType, CancellationToken)
///   plus string Name { get; } from IMetadataProvider.
/// Jellyfin discovers implementations by scanning plugin assemblies, so no explicit
/// DI registration is required or wanted.
///
/// A Jellyfin Movie maps to v5 "movie" OR v5 "anime" - see PdCodesIds.MovieTypes().
/// </remarks>
public class PdCodesMovieProvider : IRemoteMetadataProvider<Movie, MovieInfo>, IHasOrder
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PdCodesMovieProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PdCodesMovieProvider"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Injected by the host. IHttpClient was removed in
    /// 10.8; IHttpClientFactory is the only supported abstraction in 10.10.</param>
    /// <param name="logger">Injected by the host.</param>
    public PdCodesMovieProvider(
        IHttpClientFactory httpClientFactory,
        ILogger<PdCodesMovieProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => PdCodesIds.ProviderName;

    /// <summary>
    /// Gets the provider order.
    /// </summary>
    /// <remarks>
    /// A high number means "run after the built-ins". This plugin talks to a private
    /// instance whose coverage is unknown to us; letting it pre-empt TMDB by default
    /// would degrade a working library on installation. The user can reorder it in the
    /// library settings if they want it first.
    /// </remarks>
    public int Order => 10;

    /// <inheritdoc />
    public async Task<MetadataResult<Movie>> GetMetadata(
        MovieInfo info,
        CancellationToken cancellationToken)
    {
        var result = new MetadataResult<Movie> { HasMetadata = false };

        if (!PdCodesApiClient.IsConfigured)
        {
            // Not an exception: an unconfigured plugin is a normal state right after
            // installation, and throwing here would fail the whole library scan. But it
            // is logged every time, because a silently inert provider is indistinguish-
            // able from one whose API has nothing.
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
                .ResolveAsync(info, PdCodesIds.MovieTypes(), isMovieShaped: true, allowNameSearch: true, cancellationToken)
                .ConfigureAwait(false);

            if (resolved is null)
            {
                return result;
            }

            result.Item = new Movie();
            result.QueriedById = info.ProviderIds is not null
                && info.ProviderIds.ContainsKey(PdCodesIds.WorkIdKey);

            // HasMetadata is set inside Apply, as its LAST action, and Apply performs no
            // I/O. That ordering is what guarantees a mid-scan network failure cannot
            // hand Jellyfin a half-populated item marked as good: every failing path
            // returns before this line is reached.
            WorkMapper.Apply(
                result,
                resolved.Value.Work,
                isMovieShaped: true,
                Plugin.Instance?.Configuration.Country,
                resolved.Value.Certain);

            return result;
        }
        catch (PdCodesApiException ex)
        {
            // Anything the client did not already classify. Return the EMPTY result, not
            // a partially populated one: a half-filled item is written to the library
            // and looks identified, so the failure never surfaces to the user.
            _logger.LogError(ex, "PD-Codes API v5 request failed while identifying movie '{Name}'.", info.Name);
            return new MetadataResult<Movie> { HasMetadata = false };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Could not reach the PD-Codes API while identifying movie '{Name}'.", info.Name);
            return new MetadataResult<Movie> { HasMetadata = false };
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient surfaces a timeout as TaskCanceledException. The guard tells a
            // timeout apart from the user actually cancelling the scan.
            _logger.LogError(ex, "PD-Codes API timed out while identifying movie '{Name}'.", info.Name);
            return new MetadataResult<Movie> { HasMetadata = false };
        }
    }

    /// <inheritdoc />
    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(
        MovieInfo searchInfo,
        CancellationToken cancellationToken)
    {
        if (!PdCodesApiClient.IsConfigured || string.IsNullOrWhiteSpace(searchInfo.Name))
        {
            return Array.Empty<RemoteSearchResult>();
        }

        var client = new PdCodesApiClient(_httpClientFactory, _logger);
        var results = new List<RemoteSearchResult>();

        // /v5/search here rather than /v5/search-name: the manual identify screen shows a
        // poster and a year next to each candidate, and search-name returns ids only.
        // This is the one place the larger payload earns its cost.
        //
        // Each type is tried and caught SEPARATELY, on purpose. MovieTypes() tries both
        // "movie" and "anime", and the two are unrelated queries against unrelated parts
        // of the catalog - one being slow, timing out or erroring says nothing about the
        // other. A single try/catch around the whole loop discarded whatever the FIRST
        // type had already found the moment the SECOND type failed: a real, fast hit for
        // "anime" was thrown away because "movie" timed out a moment later, and Jellyfin's
        // Identify dialog showed nothing at all for a title the API actually had. Catching
        // per type keeps a slow or failing type from erasing a good result from another.
        foreach (var type in PdCodesIds.MovieTypes())
        {
            try
            {
                var hits = await client
                    .SearchAsync(searchInfo.Name, type, searchInfo.MetadataLanguage, cancellationToken)
                    .ConfigureAwait(false);
                results.AddRange(hits.Select(w => ToSearchResult(w, isMovieShaped: true)));
            }
            catch (PdCodesApiException ex)
            {
                _logger.LogError(ex, "PD-Codes API v5 search ({Type}) failed for '{Name}'.", type, searchInfo.Name);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "Could not reach the PD-Codes API searching ({Type}) for '{Name}'.", type, searchInfo.Name);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                // HttpClient surfaces a timeout as TaskCanceledException. Uncaught, this
                // used to propagate all the way to Jellyfin's ProviderManager, which logs
                // "failed to retrieve search results" and reports the WHOLE provider as
                // having nothing - even when another type in this same loop had already
                // found the work. The guard tells a timeout apart from the user actually
                // cancelling the search.
                _logger.LogError(ex, "PD-Codes API timed out searching ({Type}) for '{Name}'.", type, searchInfo.Name);
            }
        }

        return results;
    }

    /// <inheritdoc />
    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        // IRemoteMetadataProvider requires this (it inherits it from IRemoteSearchProvider)
        // so the server can fetch the thumbnail shown in the identify dialog.
        var client = _httpClientFactory.CreateClient(MediaBrowser.Common.Net.NamedClient.Default);
        return client.GetAsync(new Uri(url), cancellationToken);
    }

    /// <summary>
    /// Builds a search result from a work.
    /// </summary>
    /// <param name="work">The work.</param>
    /// <param name="isMovieShaped">True for a Jellyfin Movie.</param>
    /// <returns>The search result.</returns>
    internal static RemoteSearchResult ToSearchResult(Work work, bool isMovieShaped)
    {
        var result = new RemoteSearchResult
        {
            Name = work.Title,
            Overview = work.Synopsis,
            ProductionYear = work.Year,
            SearchProviderName = PdCodesIds.ProviderName,
            ImageUrl = work.Images?
                .FirstOrDefault(i => string.Equals(i.Type, "poster", StringComparison.OrdinalIgnoreCase))?
                .Url,
        };

        if (!string.IsNullOrWhiteSpace(work.Id))
        {
            result.SetProviderId(PdCodesIds.WorkIdKey, work.Id);
        }

        foreach (var pair in PdCodesIds.ToJellyfinProviderIds(work, isMovieShaped))
        {
            result.SetProviderId(pair.Key, pair.Value);
        }

        return result;
    }
}
