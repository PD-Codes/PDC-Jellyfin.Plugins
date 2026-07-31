using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PdCodesApi.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;
using JellyfinEpisode = MediaBrowser.Controller.Entities.TV.Episode;

namespace Jellyfin.Plugin.PdCodesApi.Providers;

/// <summary>
/// Episode stills.
/// </summary>
/// <remarks>
/// Split from PdCodesImageProvider rather than folded into it because the two have
/// different supported-image sets and different resolution paths. One class advertising
/// Backdrop for a Movie and Primary-only for an Episode would have to branch on item
/// type in three separate methods and could get them out of step.
/// </remarks>
public class PdCodesEpisodeImageProvider : IRemoteImageProvider, IHasOrder
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PdCodesEpisodeImageProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PdCodesEpisodeImageProvider"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Injected by the host.</param>
    /// <param name="logger">Injected by the host.</param>
    public PdCodesEpisodeImageProvider(
        IHttpClientFactory httpClientFactory,
        ILogger<PdCodesEpisodeImageProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => PdCodesIds.ProviderName;

    /// <inheritdoc />
    public int Order => 10;

    /// <inheritdoc />
    public bool Supports(BaseItem item) => item is JellyfinEpisode;

    /// <inheritdoc />
    /// <remarks>
    /// Primary only. In Jellyfin an episode's still IS its Primary image; there is no
    /// Backdrop on an episode, and ImageType.Screenshot is marked obsolete in 10.10.
    /// </remarks>
    public IEnumerable<ImageType> GetSupportedImages(BaseItem item) => new[] { ImageType.Primary };

    /// <inheritdoc />
    public async Task<IEnumerable<RemoteImageInfo>> GetImages(
        BaseItem item,
        CancellationToken cancellationToken)
    {
        if (!PdCodesApiClient.IsConfigured || item is not JellyfinEpisode episodeItem)
        {
            return Array.Empty<RemoteImageInfo>();
        }

        // An episode has no id of its own in v5; it is addressed as {ref} under its
        // parent work. Both halves must already be known - the parent's ULID and this
        // episode's number - or there is nothing to ask for.
        var series = episodeItem.Series;
        var seriesUlid = series?.GetProviderId(PdCodesIds.WorkIdKey);
        if (string.IsNullOrWhiteSpace(seriesUlid) || !PdCodesIds.LooksLikeUlid(seriesUlid))
        {
            return Array.Empty<RemoteImageInfo>();
        }

        // Prefer the reference the metadata provider stored - it is the one that actually
        // fetched this episode, and on a work whose seasons do not restart numbering at 1
        // it is the absolute form "E62" while the numbers would rebuild "S01E62".
        //
        // But only while it still describes this file. The same stored reference can be
        // stale after a renumber or a hand edit in the identify dialog, and a still from the
        // wrong episode is a spoiler presented as fact. PdCodesEpisodeProvider owns that
        // check so the two providers cannot disagree about which reference is usable.
        var storedRef = episodeItem.GetProviderId(PdCodesIds.EpisodeRefKey)?.Trim();
        string episodeRef;

        if (!string.IsNullOrWhiteSpace(storedRef)
            && (!episodeItem.IndexNumber.HasValue
                || PdCodesEpisodeProvider.StoredRefMatchesItem(
                    storedRef,
                    episodeItem.ParentIndexNumber,
                    episodeItem.IndexNumber.Value)))
        {
            // With no IndexNumber there is nothing to contradict the stored reference and
            // nothing to rebuild one from either, so it is used as-is or not at all.
            episodeRef = storedRef;
        }
        else
        {
            if (!episodeItem.IndexNumber.HasValue)
            {
                return Array.Empty<RemoteImageInfo>();
            }

            if (!string.IsNullOrWhiteSpace(storedRef))
            {
                _logger.LogInformation(
                    "Discarding the stored PD-Codes episode reference {StoredRef} on '{Name}' while fetching stills: "
                    + "it does not describe this file's current numbers (season {Season}, episode {Number}).",
                    storedRef,
                    episodeItem.Name,
                    episodeItem.ParentIndexNumber,
                    episodeItem.IndexNumber.Value);
            }

            episodeRef = PdCodesIds.BuildEpisodeRef(
                episodeItem.ParentIndexNumber,
                episodeItem.IndexNumber.Value);
        }

        var client = new PdCodesApiClient(_httpClientFactory, _logger);

        try
        {
            foreach (var type in PdCodesIds.SeriesTypes())
            {
                var episode = await client
                    .GetEpisodeAsync(type, seriesUlid, episodeRef, item.PreferredMetadataLanguage, cancellationToken)
                    .ConfigureAwait(false);

                if (episode is null)
                {
                    continue;
                }

                // The same structural guard the metadata provider applies. A still is a
                // weaker claim than a plot summary, but a still from the wrong episode is
                // still a spoiler shown as fact, and this path can reach the API with a
                // REBUILT reference that no previous request confirmed.
                //
                // Validate on the axis THE REFERENCE is on, not on Jellyfin's - exactly as
                // the metadata provider does. A stored "E62" is absolute; Jellyfin files
                // that episode under Season 1 while the API answers it from season 2, so
                // passing ParentIndexNumber here would refuse every still for an episode
                // the metadata provider had just identified, with a warning that reads
                // like an API fault.
                if (episodeItem.IndexNumber.HasValue
                    && !PdCodesEpisodeProvider.AgreesWithRequest(
                        episode,
                        PdCodesIds.IsAbsoluteRef(episodeRef) ? null : episodeItem.ParentIndexNumber,
                        episodeItem.IndexNumber.Value,
                        episodeRef,
                        _logger))
                {
                    return Array.Empty<RemoteImageInfo>();
                }

                var results = new List<RemoteImageInfo>();

                if (episode.Images is not null)
                {
                    results.AddRange(episode.Images
                        .Where(i => !string.IsNullOrWhiteSpace(i.Url))
                        .Select(i => new RemoteImageInfo
                        {
                            ProviderName = PdCodesIds.ProviderName,
                            Url = i.Url,
                            ThumbnailUrl = i.PreviewUrl,
                            Type = ImageType.Primary,
                            Width = i.Width,
                            Height = i.Height,
                        }));
                }

                // `image` is the primary still and may or may not also appear in
                // `images[]`. Add it only if it is not already there - a duplicate URL
                // shows the user the same still twice in the picker.
                if (!string.IsNullOrWhiteSpace(episode.Image)
                    && !results.Any(r => string.Equals(r.Url, episode.Image, StringComparison.Ordinal)))
                {
                    results.Insert(0, new RemoteImageInfo
                    {
                        ProviderName = PdCodesIds.ProviderName,
                        Url = episode.Image,
                        Type = ImageType.Primary,
                    });
                }

                return results;
            }

            return Array.Empty<RemoteImageInfo>();
        }
        catch (PdCodesApiException ex)
        {
            _logger.LogError(ex, "PD-Codes API v5 request failed fetching stills for episode {Ref}.", episodeRef);
            return Array.Empty<RemoteImageInfo>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Could not reach the PD-Codes API fetching stills for episode {Ref}.", episodeRef);
            return Array.Empty<RemoteImageInfo>();
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "PD-Codes API timed out fetching stills for episode {Ref}.", episodeRef);
            return Array.Empty<RemoteImageInfo>();
        }
    }

    /// <inheritdoc />
    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(MediaBrowser.Common.Net.NamedClient.Default);
        return client.GetAsync(new Uri(url), cancellationToken);
    }
}
