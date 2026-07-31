using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.PdCodesApi.Configuration;

/// <summary>
/// User-editable settings, persisted by Jellyfin as XML next to the plugin.
/// Verified against Jellyfin 10.10.6: BasePluginConfiguration lives in
/// MediaBrowser.Model.Plugins and is deserialized by IXmlSerializer, so every
/// member must be a public settable property of a simple type.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the base URL of the PD-Codes API deployment, e.g.
    /// "https://media.example.org/jikan". The plugin appends "/v5" itself.
    /// </summary>
    /// <remarks>
    /// Deliberately EMPTY by default. There is no public instance of this API, and
    /// shipping a default would point every installation at whichever stranger's
    /// server happened to be in the source at build time. An empty value makes the
    /// providers report "not configured" and return nothing, which is a visible
    /// failure rather than silent traffic to somebody else's host.
    /// </remarks>
    public string ApiBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the preferred metadata language as a BCP-47 / ISO code ("de", "en", "ja").
    /// Empty means "use the language Jellyfin asks for on each request".
    /// </summary>
    /// <remarks>
    /// This is an OVERRIDE, not the normal path. Jellyfin passes the library's
    /// configured language in ItemLookupInfo.MetadataLanguage per request; honoring
    /// that is more correct than a global setting, so this is only consulted when set.
    /// </remarks>
    public string PreferredLanguage { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the ISO 3166-1 alpha-2 country used to select the availability subset
    /// (?country=). Empty means "let the API pick".
    /// </summary>
    public string Country { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether title-matched (uncertain) external ids may be
    /// used to identify a work.
    /// </summary>
    /// <remarks>
    /// Default OFF, on purpose. The v5 contract states plainly that
    /// <c>uncertain_external_ids</c> are matched by TITLE and are guesses. Accepting them
    /// means a library item can be bound at full confidence to a different work with a
    /// similar name, and Jellyfin will then persist that id and never re-ask. The failure
    /// is invisible: the item looks fully identified, it is just the wrong show.
    /// </remarks>
    public bool AcceptUncertainMatches { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a Jellyfin Movie should be looked for under the
    /// v5 "anime" type before "movie".
    /// </summary>
    /// <remarks>
    /// v5 has no separate Jellyfin item type for anime: an anime film is v5 type "anime",
    /// and so is an anime series. A Jellyfin Movie therefore maps to v5 "movie" OR "anime",
    /// and a Series to v5 "tv" OR "anime". The plugin tries both; this only decides the
    /// order, which matters for a name search where both media could plausibly answer.
    /// </remarks>
    public bool PreferAnimeForMovies { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a Jellyfin Series should be looked for under the
    /// v5 "anime" type before "tv".
    /// </summary>
    public bool PreferAnimeForSeries { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether a missed "SxxEyy" episode reference on an
    /// ANIME work may be retried as the absolute reference "E{n}".
    /// </summary>
    /// <remarks>
    /// Anime released with absolutely-numbered files ("Show - 62.mkv") is normally filed
    /// by Jellyfin under Season 1, so the plugin asks for "S01E62". Under TMDB numbering a
    /// season does not restart at 1 (One Piece season 2 is episodes 62-77), so "S01E62" and
    /// "E62" are DIFFERENT episodes, and on such a work the seasonal form is usually simply
    /// absent - the episode gets no metadata and nothing says why.
    ///
    /// When this is on, that miss is retried once as "E{n}". The retry's answer is only
    /// ACCEPTED when the API's own <c>numbering.continuous</c> for that work is false; an
    /// absent numbering block or a work that declares continuous numbering means the answer
    /// is refused and logged. That is what keeps this from laundering a guess into a stored
    /// provider id: a continuous work would have answered the seasonal reference in the
    /// first place, so a hit on the absolute form after a miss on the seasonal one is a
    /// contradiction, not a resolution.
    ///
    /// Cost, stated exactly: ONE extra request per episode that missed under BOTH candidate
    /// types, and only on anime items filed under Season 1. The retry runs once, after the
    /// type loop - not per candidate type - so a live-action series never pays it. It is paid
    /// once per episode rather than once per scan, because the reference that produced the
    /// episode ("E62", the ABSOLUTE one, not the seasonal one the API answers with) is stored
    /// on the item, and a stored reference is re-fetchable from the numbers Jellyfin holds.
    /// </remarks>
    public bool AbsoluteNumberingFallback { get; set; } = true;

    /// <summary>
    /// Gets or sets the per-request timeout in seconds.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 30;
}
