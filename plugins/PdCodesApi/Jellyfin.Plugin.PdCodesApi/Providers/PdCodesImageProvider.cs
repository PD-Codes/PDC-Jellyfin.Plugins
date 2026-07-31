using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PdCodesApi.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PdCodesApi.Providers;

/// <summary>
/// Posters, backdrops and logos for Movies and Series.
/// </summary>
/// <remarks>
/// Verified against Jellyfin 10.10.6
/// (MediaBrowser.Controller/Providers/IRemoteImageProvider.cs, and
/// MediaBrowser.Providers/Plugins/Omdb/OmdbImageProvider.cs as a live example):
///   IRemoteImageProvider : IImageProvider
///     IEnumerable&lt;ImageType&gt; GetSupportedImages(BaseItem item)
///     Task&lt;IEnumerable&lt;RemoteImageInfo&gt;&gt; GetImages(BaseItem item, CancellationToken)
///     Task&lt;HttpResponseMessage&gt; GetImageResponse(string url, CancellationToken)
///   plus, from IImageProvider: string Name { get; } and bool Supports(BaseItem item).
/// </remarks>
public class PdCodesImageProvider : IRemoteImageProvider, IHasOrder
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PdCodesImageProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PdCodesImageProvider"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Injected by the host.</param>
    /// <param name="logger">Injected by the host.</param>
    public PdCodesImageProvider(
        IHttpClientFactory httpClientFactory,
        ILogger<PdCodesImageProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => PdCodesIds.ProviderName;

    /// <inheritdoc />
    public int Order => 10;

    /// <inheritdoc />
    public bool Supports(BaseItem item) => item is Movie || item is Series;

    /// <inheritdoc />
    /// <remarks>
    /// Exactly the three v5 image types, and nothing else. Advertising Banner or Thumb
    /// here would make Jellyfin ask for them on every scan and get an empty list back
    /// forever - a provider that is always consulted and never answers.
    /// </remarks>
    public IEnumerable<ImageType> GetSupportedImages(BaseItem item) => new[]
    {
        ImageType.Primary,   // v5 "poster"
        ImageType.Backdrop,  // v5 "backdrop"
        ImageType.Logo,      // v5 "logo"
    };

    /// <inheritdoc />
    public async Task<IEnumerable<RemoteImageInfo>> GetImages(
        BaseItem item,
        CancellationToken cancellationToken)
    {
        if (!PdCodesApiClient.IsConfigured)
        {
            return Array.Empty<RemoteImageInfo>();
        }

        // Images only ever come from an already-identified item. There is no fallback
        // name search here, on purpose: an image provider that guesses puts a poster
        // for a different show onto a correctly-identified item, and the user has no
        // way to tell that the two decisions were made by different code.
        var ulid = item.GetProviderId(PdCodesIds.WorkIdKey);
        if (string.IsNullOrWhiteSpace(ulid) || !PdCodesIds.LooksLikeUlid(ulid))
        {
            return Array.Empty<RemoteImageInfo>();
        }

        var isMovieShaped = item is Movie;
        var candidateTypes = isMovieShaped ? PdCodesIds.MovieTypes() : PdCodesIds.SeriesTypes();
        var client = new PdCodesApiClient(_httpClientFactory, _logger);

        try
        {
            foreach (var type in candidateTypes)
            {
                // One request per item. The work payload already carries images[], so
                // there is no separate image endpoint to call and no reason to page.
                var work = await client
                    .GetWorkAsync(type, ulid, item.PreferredMetadataLanguage, cancellationToken)
                    .ConfigureAwait(false);

                if (work is null)
                {
                    // 404 under this type - try the other one.
                    continue;
                }

                // The ULID resolved, so THIS is the work, whether or not it has images.
                // Falling through to the next candidate type here would be a bug: the same
                // ULID could resolve under both "anime" and "tv", and we would end up
                // showing a different work's posters on an already-identified item.
                if (work.Images is null)
                {
                    return Array.Empty<RemoteImageInfo>();
                }

                return work.Images
                    .Select(MapImage)
                    .Where(i => i is not null)
                    .Select(i => i!)
                    .ToList();
            }

            return Array.Empty<RemoteImageInfo>();
        }
        catch (PdCodesApiException ex)
        {
            _logger.LogError(ex, "PD-Codes API v5 request failed fetching images for '{Name}'.", item.Name);
            return Array.Empty<RemoteImageInfo>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Could not reach the PD-Codes API fetching images for '{Name}'.", item.Name);
            return Array.Empty<RemoteImageInfo>();
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "PD-Codes API timed out fetching images for '{Name}'.", item.Name);
            return Array.Empty<RemoteImageInfo>();
        }
    }

    /// <inheritdoc />
    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(MediaBrowser.Common.Net.NamedClient.Default);
        return client.GetAsync(new Uri(url), cancellationToken);
    }

    /// <summary>
    /// Maps one v5 image onto a Jellyfin RemoteImageInfo.
    /// </summary>
    /// <remarks>
    /// An unrecognized <c>type</c> returns null and the image is dropped. That is the
    /// one place a silent drop is right: mapping an unknown type onto Primary would
    /// put whatever v5 adds next - a character art sheet, a season poster - in front of
    /// the user as the item's main poster.
    /// </remarks>
    private static RemoteImageInfo? MapImage(WorkImage image)
    {
        if (string.IsNullOrWhiteSpace(image.Url))
        {
            return null;
        }

        ImageType mapped;
        switch (image.Type?.ToUpperInvariant())
        {
            case "POSTER":
                // Jellyfin's Primary IS the poster for a Movie/Series. There is no
                // separate "Poster" member in ImageType (verified: the enum is Primary,
                // Art, Backdrop, Banner, Logo, Thumb, Disc, Box, Screenshot, Menu,
                // Chapter, BoxRear, Profile).
                mapped = ImageType.Primary;
                break;
            case "BACKDROP":
                mapped = ImageType.Backdrop;
                break;
            case "LOGO":
                mapped = ImageType.Logo;
                break;
            default:
                return null;
        }

        return new RemoteImageInfo
        {
            ProviderName = PdCodesIds.ProviderName,
            Url = image.Url,
            Type = mapped,
            Width = image.Width,
            Height = image.Height,
            Language = image.Lang,

            // v5's `vote` is the source's own image vote. It is surfaced as
            // CommunityRating because that is what Jellyfin sorts the image picker by;
            // it is NOT the work's score and must never be copied onto the item.
            CommunityRating = image.Vote,
        };
    }
}
