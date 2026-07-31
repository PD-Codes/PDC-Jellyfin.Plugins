using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PdCodesApi.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PdCodesApi.Api;

/// <summary>
/// Thin typed client over the v5 surface.
/// </summary>
/// <remarks>
/// Verified against Jellyfin 10.10.6: plugins take <see cref="IHttpClientFactory"/> by
/// constructor injection (see MediaBrowser.Providers/Plugins/Omdb/OmdbImageProvider.cs,
/// whose ctor is <c>(IHttpClientFactory, IFileSystem, IServerConfigurationManager)</c>).
/// The old <c>IHttpClient</c> abstraction was removed in 10.8 and referencing it will not
/// even resolve against the 10.10 package.
/// </remarks>
public class PdCodesApiClient
{
    // Requests go through MediaBrowser.Common.Net.NamedClient.Default, which is the named
    // client Jellyfin registers with its own User-Agent and handler policy. Using an
    // unnamed client would work but would present the plugin to the API as a bare
    // HttpClient, which is unhelpful in the API's own access logs.
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;

    private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        // The models carry explicit [JsonPropertyName] for every member, so no naming
        // policy is configured: an implicit policy would quietly bind a field we never
        // verified against the contract.
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="PdCodesApiClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Injected by the host.</param>
    /// <param name="logger">Injected by the host.</param>
    public PdCodesApiClient(IHttpClientFactory httpClientFactory, ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private static PluginConfiguration Config =>
        Plugin.Instance?.Configuration
        ?? throw new InvalidOperationException(
            "PD-Codes API plugin instance is not available; the plugin did not initialize.");

    /// <summary>
    /// Gets a value indicating whether a base URL has been configured.
    /// </summary>
    public static bool IsConfigured => !string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration.ApiBaseUrl);

    /// <summary>
    /// Fetches one work by its v5 ULID.
    /// </summary>
    /// <param name="type">v5 type segment: anime, movie, tv, manga or album.</param>
    /// <param name="ulid">The 26-character ULID.</param>
    /// <param name="language">Preferred language for title/synopsis, may be null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The work, or null when the API answers 404.</returns>
    public async Task<Work?> GetWorkAsync(
        string type,
        string ulid,
        string? language,
        CancellationToken cancellationToken)
    {
        var url = BuildUrl($"{type}/{Uri.EscapeDataString(ulid)}", WithCountry());
        try
        {
            var envelope = await GetJsonAsync<ItemEnvelope<Work>>(url, language, cancellationToken)
                .ConfigureAwait(false);
            return envelope?.Data;
        }
        catch (PdCodesApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // 404 here is meaningful and final: the ULID we stored no longer exists,
            // or something wrote a MAL id into the PD-Codes provider id field. Both are
            // worth a warning; neither is worth a retry or a partial item.
            _logger.LogWarning(
                "PD-Codes API v5 has no {Type} with id {Ulid}. If that id looks numeric it is a MAL id, not a v5 ULID.",
                type,
                ulid);
            return null;
        }
    }

    /// <summary>
    /// Resolves a work from an id Jellyfin already holds.
    /// </summary>
    /// <param name="source">A v5 source key. Must be namespaced where the id space is
    /// split: <c>tmdb_movie</c>/<c>tmdb_tv</c>, <c>tvdb_series</c>/<c>tvdb_movie</c>.
    /// The bare <c>tmdb</c>/<c>tvdb</c> sources are retired and answer 400.</param>
    /// <param name="id">The provider id.</param>
    /// <param name="type">v5 type to disambiguate with, or null. Supplying it is what
    /// prevents the 409 that a MAL id (anime AND manga) or a TMDB number produces.</param>
    /// <param name="language">Preferred language, may be null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The lookup envelope, or null when nothing matched or the match was ambiguous.</returns>
    public async Task<LookupEnvelope?> LookupAsync(
        string source,
        string id,
        string? type,
        string? language,
        CancellationToken cancellationToken)
    {
        var query = WithCountry();
        if (!string.IsNullOrEmpty(type))
        {
            query["type"] = type;
        }

        var url = BuildUrl(
            $"lookup/{Uri.EscapeDataString(source)}/{Uri.EscapeDataString(id)}",
            query);

        try
        {
            return await GetJsonAsync<LookupEnvelope>(url, language, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PdCodesApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // The catalog simply does not hold this id. Normal; not an error.
            return null;
        }
        catch (PdCodesApiException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            // 409 on lookup means MORE THAN ONE work carries this id, and the body
            // carries candidates[]. We refuse to choose. Picking the first candidate
            // would bind the library item to a coin flip and then persist it as fact,
            // which is exactly the failure the API is going out of its way to prevent.
            _logger.LogWarning(
                "PD-Codes API v5 lookup {Source}/{Id} is ambiguous (409) - more than one work carries that id. "
                + "Not guessing. Pass a narrower type or identify this item manually. Body: {Body}",
                source,
                id,
                ex.Body);
            return null;
        }
        catch (PdCodesApiException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
        {
            // 400 on lookup is almost always a retired source key ("tmdb", "tvdb").
            // That is a bug in this plugin, not a data condition, so it is logged at
            // error and the body (which contains directions) is included verbatim.
            _logger.LogError(
                "PD-Codes API v5 rejected lookup source {Source} (400). The bare tmdb/tvdb sources are retired; "
                + "use tmdb_movie/tmdb_tv and tvdb_series/tvdb_movie. Body: {Body}",
                source,
                ex.Body);
            return null;
        }
    }

    /// <summary>
    /// Name search, returning ids only.
    /// </summary>
    /// <param name="name">The name to look up.</param>
    /// <param name="type">v5 type to restrict to, or null for all media.</param>
    /// <param name="language">Preferred language, may be null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Hits, possibly empty. Never null.</returns>
    /// <remarks>
    /// <c>/v5/search-name</c> rather than <c>/v5/search</c>: identification only needs
    /// the id, and search-name is documented as the small payload for exactly that. The
    /// full work is then fetched once, for the one candidate the caller keeps.
    /// </remarks>
    public async Task<IReadOnlyList<SearchNameHit>> SearchNameAsync(
        string name,
        string? type,
        string? language,
        CancellationToken cancellationToken)
    {
        var query = new Dictionary<string, string> { ["q"] = name };
        if (!string.IsNullOrEmpty(type))
        {
            query["type"] = type;
        }

        var url = BuildUrl("search-name", query);

        // SearchNameEnvelope, NOT ListEnvelope. /v5/search-name answers
        // { query, count, data[] } with no pagination block. Deserializing it as the
        // standard list envelope appears to work - data[] still binds - which is exactly
        // why that mistake survives a smoke test while pagination reads as absent.
        var envelope = await GetJsonAsync<SearchNameEnvelope>(url, language, cancellationToken)
            .ConfigureAwait(false);
        return envelope?.Data ?? Array.Empty<SearchNameHit>();
    }

    /// <summary>
    /// Full search, returning whole works. Used for GetSearchResults, where the UI wants
    /// a year and a poster next to each candidate.
    /// </summary>
    /// <param name="name">The query string.</param>
    /// <param name="type">v5 type to restrict to, or null.</param>
    /// <param name="language">Preferred language, may be null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Hits, possibly empty. Never null.</returns>
    public async Task<IReadOnlyList<Work>> SearchAsync(
        string name,
        string? type,
        string? language,
        CancellationToken cancellationToken)
    {
        var query = WithCountry();
        query["q"] = name;
        if (!string.IsNullOrEmpty(type))
        {
            query["type"] = type;
        }

        var url = BuildUrl("search", query);
        try
        {
            var envelope = await GetJsonAsync<ListEnvelope<Work>>(url, language, cancellationToken)
                .ConfigureAwait(false);
            return envelope?.Data ?? Array.Empty<Work>();
        }
        catch (PdCodesApiException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
        {
            // A filter that is meaningless for the requested medium is a 400 by design
            // (e.g. genre over albums). Report it; do not present an empty result set as
            // "nothing matched", which is a different and false statement.
            _logger.LogError(
                "PD-Codes API v5 rejected the search request (400): {Body}",
                ex.Body);
            return Array.Empty<Work>();
        }
    }

    /// <summary>
    /// Fetches one episode by reference.
    /// </summary>
    /// <param name="type">v5 type of the parent work: anime or tv.</param>
    /// <param name="ulid">Parent work ULID.</param>
    /// <param name="episodeRef">"S02E01", "E62" or "SP3".</param>
    /// <param name="language">Preferred language, may be null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The episode, or null when it is not held.</returns>
    /// <remarks>
    /// A convenience wrapper over <see cref="GetEpisodeEnvelopeAsync"/> for the callers
    /// that only want the episode itself (the image provider). Callers that need the
    /// work's <c>numbering</c> block - the absolute-numbering fallback does - must use
    /// the envelope form, because the block is the only thing that can confirm a hit on
    /// an alternative reference is not a coincidence.
    /// </remarks>
    public async Task<Episode?> GetEpisodeAsync(
        string type,
        string ulid,
        string episodeRef,
        string? language,
        CancellationToken cancellationToken)
        => (await GetEpisodeEnvelopeAsync(type, ulid, episodeRef, language, cancellationToken)
            .ConfigureAwait(false))?.Data;

    /// <summary>
    /// Fetches one episode by reference, returning the whole envelope.
    /// </summary>
    /// <param name="type">v5 type of the parent work: anime or tv.</param>
    /// <param name="ulid">Parent work ULID.</param>
    /// <param name="episodeRef">"S02E01", "E62" or "SP3".</param>
    /// <param name="language">Preferred language, may be null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The envelope (episode plus the work's numbering block), or null when it
    /// is not held, was never merged, the reference was rejected as malformed, or the
    /// medium has no episode list.</returns>
    public async Task<EpisodeEnvelope?> GetEpisodeEnvelopeAsync(
        string type,
        string ulid,
        string episodeRef,
        string? language,
        CancellationToken cancellationToken)
    {
        var url = BuildUrl(
            $"{type}/{Uri.EscapeDataString(ulid)}/episodes/{Uri.EscapeDataString(episodeRef)}",
            new Dictionary<string, string>());

        try
        {
            var envelope = await GetJsonAsync<EpisodeEnvelope>(url, language, cancellationToken)
                .ConfigureAwait(false);
            return envelope;
        }
        catch (PdCodesApiException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
        {
            // 400 = the API could not read the REFERENCE itself. That is a different
            // statement from "the work has no such episode": it means the string is not a
            // reference at all, which in practice comes from a hand-typed id in Jellyfin's
            // identify dialog. Naming the reference is the whole value of this line - the
            // caller would otherwise abort the item with a generic "request failed" and
            // nothing pointing at the field the operator has to correct.
            _logger.LogWarning(
                "PD-Codes API v5 rejected the episode reference {Ref} for {Type} {Ulid} (400) - the reference itself "
                + "is malformed, not merely absent. Expected forms are S02E01, SP3 and E62. Body: {Body}",
                episodeRef,
                type,
                ulid,
                ex.Body);
            return null;
        }
        catch (PdCodesApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // 404 is AMBIGUOUS on this route and the plugin cannot resolve it: the work may
            // have no episode index at all, or it may be fully merged and simply not have
            // this reference among its episodes. Debug, not Information, and worded to say
            // so - the old line told the operator to run ingest:episodes, which on a
            // 1,100-episode series is 1,100 instructions to ingest something already
            // ingested. The unambiguous case is the 409 below, and that one keeps its
            // Warning because it names a fix that is actually the fix.
            _logger.LogDebug(
                "PD-Codes API v5 answered 404 for {Type} {Ulid} episode {Ref}: either that work has no episode "
                + "index, or that reference is not one of its episodes.",
                type,
                ulid,
                episodeRef);
            return null;
        }
        catch (PdCodesApiException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            // 409 = the episodes ARE on disk but were never merged, and the body names
            // the command to run. This is emphatically NOT "no episodes": reporting it
            // as absence would hide a one-command fix behind a permanently empty season.
            _logger.LogWarning(
                "PD-Codes API v5 has unmerged episodes for {Type} {Ulid} (409). The data exists but no merged "
                + "index does. Body (names the command to run): {Body}",
                type,
                ulid,
                ex.Body);
            return null;
        }
        catch (PdCodesApiException ex) when (ex.StatusCode == HttpStatusCode.NotImplemented)
        {
            // manga answers a chapters-specific 501. A Jellyfin Episode should never
            // reach here, so this is a wiring bug worth an error rather than a shrug.
            _logger.LogError(
                "PD-Codes API v5 answered 501 for episodes of {Type} {Ulid}; that medium has no episode list. Body: {Body}",
                type,
                ulid,
                ex.Body);
            return null;
        }
    }

    /// <summary>
    /// Performs the request, maps failures onto <see cref="PdCodesApiException"/> and deserializes.
    /// </summary>
    private async Task<T?> GetJsonAsync<T>(
        string url,
        string? language,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(MediaBrowser.Common.Net.NamedClient.Default);
        client.Timeout = TimeSpan.FromSeconds(Math.Max(5, Config.RequestTimeoutSeconds));

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // Accept-Language rather than ?lang=. Both work, but the API sends
        // "Vary: Accept-Language", so the header is what any cache in front of it keys
        // on. Passing the language in the query string instead would make one cache
        // entry per URL that is nonetheless served to every language - a German browser
        // filling the cache and the next English request getting German titles is a
        // failure this deployment has actually had.
        var effectiveLanguage = ResolveLanguage(language);
        if (!string.IsNullOrEmpty(effectiveLanguage))
        {
            request.Headers.AcceptLanguage.Add(
                new StringWithQualityHeaderValue(effectiveLanguage));
        }

        using var response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string? body = null;
            try
            {
                body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (body.Length > 1000)
                {
                    body = body.Substring(0, 1000);
                }
            }
            catch (HttpRequestException)
            {
                // The status code is the information we need; a body we could not read
                // must not turn a clean 404 into an unhandled exception.
            }

            if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                // 503 from /v5/status semantics: the pipeline is degraded. Distinguish it
                // in the log so an operator does not read a transient outage as "the
                // catalog does not have my show".
                _logger.LogWarning(
                    "PD-Codes API v5 is degraded (503) for {Url}. Check GET /v5/status. Body: {Body}",
                    url,
                    body);
            }

            throw new PdCodesApiException(response.StatusCode, url, body);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            return await JsonSerializer.DeserializeAsync<T>(stream, _jsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            // A 200 whose body does not match the contract. Re-thrown as a PdCodesApiException
            // so it lands in the providers' existing handling and produces NO metadata,
            // rather than escaping GetMetadata and aborting the whole library scan on one
            // bad document. The status is carried through as OK because that is what the
            // server actually said - claiming a 500 here would misdirect the reader.
            _logger.LogError(
                ex,
                "PD-Codes API v5 returned a body for {Url} that does not match the v5 contract.",
                url);
            throw new PdCodesApiException(response.StatusCode, url, ex.Message);
        }
    }

    /// <summary>
    /// Decides which language to ask for.
    /// </summary>
    /// <remarks>
    /// The configured override wins when set; otherwise Jellyfin's per-library
    /// MetadataLanguage is used, which is the more correct default because a server
    /// can host libraries in different languages.
    /// </remarks>
    private static string? ResolveLanguage(string? requestLanguage)
    {
        var configured = Config.PreferredLanguage;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }

        return string.IsNullOrWhiteSpace(requestLanguage) ? null : requestLanguage.Trim();
    }

    /// <summary>
    /// Seeds a query dictionary with the configured country, if any.
    /// </summary>
    private static Dictionary<string, string> WithCountry()
    {
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        var country = Config.Country;
        if (!string.IsNullOrWhiteSpace(country))
        {
            query["country"] = country.Trim();
        }

        return query;
    }

    /// <summary>
    /// Joins the configured base URL, the "/v5" prefix, a path and a query.
    /// </summary>
    private static string BuildUrl(string path, Dictionary<string, string> query)
    {
        var baseUrl = Config.ApiBaseUrl?.Trim();
        if (string.IsNullOrEmpty(baseUrl))
        {
            // Fail loudly. Every provider checks IsConfigured first, so reaching here
            // means a code path skipped that check - a bug, not a user misconfiguration.
            throw new InvalidOperationException(
                "PD-Codes API base URL is not configured. Set it in the plugin settings.");
        }

        baseUrl = baseUrl.TrimEnd('/');

        var builder = new StringBuilder(baseUrl);
        builder.Append("/v5/").Append(path);

        var first = true;
        foreach (var pair in query)
        {
            builder.Append(first ? '?' : '&');
            first = false;
            builder.Append(Uri.EscapeDataString(pair.Key))
                   .Append('=')
                   .Append(Uri.EscapeDataString(pair.Value));
        }

        return builder.ToString();
    }
}
