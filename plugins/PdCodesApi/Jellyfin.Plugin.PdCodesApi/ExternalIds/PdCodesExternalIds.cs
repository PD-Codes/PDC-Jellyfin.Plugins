using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
// IHasProviderIds is in MediaBrowser.Model.Entities; ExternalIdMediaType is in
// MediaBrowser.Model.Providers (file path MediaBrowser.Model/Providers/ExternalIdMediaType.cs
// in the v10.10.6 tree). Both usings are required - they are different namespaces.
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using JellyfinEpisode = MediaBrowser.Controller.Entities.TV.Episode;

namespace Jellyfin.Plugin.PdCodesApi.ExternalIds;

// Verified against Jellyfin 10.10.6 (MediaBrowser.Controller/Providers/IExternalId.cs):
//   string ProviderName { get; }
//   string Key { get; }
//   ExternalIdMediaType? Type { get; }
//   [Obsolete("Obsolete in 10.10, to be removed in 10.11")] string? UrlFormatString { get; }
//   bool Supports(IHasProviderIds item);
//
// UrlFormatString is obsolete but still on the interface in 10.10, so it must be
// implemented or the class does not satisfy the contract. It returns null - the base URL
// is a per-installation setting, not a compile-time constant, and there is no honest
// format string that works for every deployment. #pragma is used rather than deleting
// the member, because deleting it is a compile error and suppressing it project-wide
// would hide the same warning somewhere it matters.
//
// One implementation per media type: ExternalIdMediaType is what lets the web client
// label the id correctly, and a single shared implementation would have to return null
// for it, which the interface documents as "no specific media type" - a different claim.

/// <summary>Exposes the v5 ULID of a Movie in the Jellyfin UI.</summary>
public class PdCodesMovieExternalId : IExternalId
{
    /// <inheritdoc />
    public string ProviderName => PdCodesIds.ProviderName;

    /// <inheritdoc />
    public string Key => PdCodesIds.WorkIdKey;

    /// <inheritdoc />
    public ExternalIdMediaType? Type => ExternalIdMediaType.Movie;

    /// <inheritdoc />
#pragma warning disable CS0618 // Obsolete in 10.10, still required by the interface.
    public string? UrlFormatString => null;
#pragma warning restore CS0618

    /// <inheritdoc />
    public bool Supports(IHasProviderIds item) => item is Movie;
}

/// <summary>Exposes the v5 ULID of a Series in the Jellyfin UI.</summary>
public class PdCodesSeriesExternalId : IExternalId
{
    /// <inheritdoc />
    public string ProviderName => PdCodesIds.ProviderName;

    /// <inheritdoc />
    public string Key => PdCodesIds.WorkIdKey;

    /// <inheritdoc />
    public ExternalIdMediaType? Type => ExternalIdMediaType.Series;

    /// <inheritdoc />
#pragma warning disable CS0618
    public string? UrlFormatString => null;
#pragma warning restore CS0618

    /// <inheritdoc />
    public bool Supports(IHasProviderIds item) => item is Series;
}

/// <summary>Exposes an episode's v5 reference ("S02E01") in the Jellyfin UI.</summary>
/// <remarks>
/// The KEY differs from the two above: an episode has no ULID of its own in v5, so what
/// is shown is the reference under its parent work. Reusing WorkIdKey here would put a
/// series ULID on an episode and make every consumer of that key ambiguous.
/// </remarks>
public class PdCodesEpisodeExternalId : IExternalId
{
    /// <inheritdoc />
    public string ProviderName => PdCodesIds.ProviderName;

    /// <inheritdoc />
    public string Key => PdCodesIds.EpisodeRefKey;

    /// <inheritdoc />
    public ExternalIdMediaType? Type => ExternalIdMediaType.Episode;

    /// <inheritdoc />
#pragma warning disable CS0618
    public string? UrlFormatString => null;
#pragma warning restore CS0618

    /// <inheritdoc />
    public bool Supports(IHasProviderIds item) => item is JellyfinEpisode;
}
