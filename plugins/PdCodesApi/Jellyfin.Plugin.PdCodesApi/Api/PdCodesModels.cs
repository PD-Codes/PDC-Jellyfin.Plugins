using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.PdCodesApi.Api;

// Every field below is taken from the verified v5 contract. Nothing is invented.
// Fields the plugin does not use are deliberately absent rather than mapped
// speculatively: an unused property that is wrong is indistinguishable from one
// that is right until something reads it.
//
// All reference-typed members are nullable. The v5 contract states that null/empty
// keys are OMITTED from the payload (see the credits section), so System.Text.Json
// will leave them at their default. Declaring them non-nullable would be a lie the
// compiler cannot check and a NullReferenceException at scan time.

/// <summary>Envelope for a single work: <c>{ "data": Work }</c>.</summary>
/// <typeparam name="T">The payload type carried in <c>data</c>.</typeparam>
public sealed class ItemEnvelope<T>
{
    /// <summary>Gets or sets the payload.</summary>
    [JsonPropertyName("data")]
    public T? Data { get; set; }
}

/// <summary>Envelope for the list endpoints and <c>/v5/search</c>.</summary>
/// <typeparam name="T">The element type carried in <c>data</c>.</typeparam>
public sealed class ListEnvelope<T>
{
    /// <summary>Gets or sets the page of results.</summary>
    [JsonPropertyName("data")]
    public IReadOnlyList<T>? Data { get; set; }

    /// <summary>Gets or sets the pagination block.</summary>
    [JsonPropertyName("pagination")]
    public Pagination? Pagination { get; set; }
}

/// <summary>Pagination block of the list envelope.</summary>
public sealed class Pagination
{
    [JsonPropertyName("has_next_page")]
    public bool HasNextPage { get; set; }

    [JsonPropertyName("current_page")]
    public int CurrentPage { get; set; }
}

/// <summary>The <c>/v5/lookup/{source}/{id}</c> envelope.</summary>
public sealed class LookupEnvelope
{
    [JsonPropertyName("data")]
    public Work? Data { get; set; }

    [JsonPropertyName("matched")]
    public LookupMatch? Matched { get; set; }
}

/// <summary>The <c>matched</c> block of a lookup response.</summary>
public sealed class LookupMatch
{
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the match is confirmed rather than guessed.
    /// </summary>
    /// <remarks>
    /// Defaults to false when absent, which is the safe direction: an unparsed
    /// response is treated as uncertain and (by default) rejected.
    /// </remarks>
    [JsonPropertyName("certain")]
    public bool Certain { get; set; }
}

/// <summary>A work. Only the fields the plugin consumes are modeled.</summary>
public sealed class Work
{
    /// <summary>Gets or sets the canonical v5 id: a 26-character ULID, not a MAL id.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>Gets or sets the v5 type: anime, manga, movie, tv or album.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("synopsis")]
    public string? Synopsis { get; set; }

    /// <summary>Gets or sets the language actually used to render <see cref="Title"/>.</summary>
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("titles")]
    public IReadOnlyList<TitleEntry>? Titles { get; set; }

    /// <remarks>
    /// See <see cref="MixedTypeStringDictionaryConverter"/>: the v5 contract sends some ids
    /// (mal, anilist, tmdb_tv, tvdb_series, ...) as JSON numbers and others (wikidata, imdb,
    /// animeplanet, ...) as JSON strings, in the SAME object. A plain
    /// <c>Dictionary&lt;string, string&gt;</c> throws on the first numeric value.
    /// </remarks>
    [JsonPropertyName("external_ids")]
    [JsonConverter(typeof(MixedTypeStringDictionaryConverter))]
    public Dictionary<string, string>? ExternalIds { get; set; }

    /// <summary>Gets or sets ids matched by title only. Guesses; never written back as fact.</summary>
    [JsonPropertyName("uncertain_external_ids")]
    [JsonConverter(typeof(MixedTypeStringDictionaryConverter))]
    public Dictionary<string, string>? UncertainExternalIds { get; set; }

    [JsonPropertyName("year")]
    public int? Year { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("is_ongoing")]
    public bool IsOngoing { get; set; }

    [JsonPropertyName("genres")]
    public IReadOnlyList<string>? Genres { get; set; }

    [JsonPropertyName("keywords")]
    public IReadOnlyList<string>? Keywords { get; set; }

    [JsonPropertyName("studios")]
    public IReadOnlyList<string>? Studios { get; set; }

    /// <summary>Gets or sets the NORMALIZED score. Bigger is better for every source.</summary>
    [JsonPropertyName("score")]
    public float? Score { get; set; }

    [JsonPropertyName("images")]
    public IReadOnlyList<WorkImage>? Images { get; set; }

    [JsonPropertyName("certifications")]
    public IReadOnlyList<Certification>? Certifications { get; set; }

    [JsonPropertyName("releases")]
    public IReadOnlyList<Release>? Releases { get; set; }

    /// <summary>Gets or sets a value indicating whether some field on this work is a guess.</summary>
    [JsonPropertyName("uncertain")]
    public bool Uncertain { get; set; }

    [JsonPropertyName("mature")]
    public bool Mature { get; set; }
}

/// <summary>One language-tagged title. <c>und</c> (undetermined) is a real value here.</summary>
public sealed class TitleEntry
{
    [JsonPropertyName("lang")]
    public string? Lang { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("value")]
    public string? Value { get; set; }
}

/// <summary>An image on a work. <c>type</c> is poster, backdrop or logo.</summary>
public sealed class WorkImage
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("lang")]
    public string? Lang { get; set; }

    [JsonPropertyName("width")]
    public int? Width { get; set; }

    [JsonPropertyName("height")]
    public int? Height { get; set; }

    [JsonPropertyName("vote")]
    public double? Vote { get; set; }
}

/// <summary>An age rating for one country.</summary>
public sealed class Certification
{
    [JsonPropertyName("country")]
    public string? Country { get; set; }

    [JsonPropertyName("rating")]
    public string? Rating { get; set; }
}

/// <summary>A release date.</summary>
public sealed class Release
{
    [JsonPropertyName("country")]
    public string? Country { get; set; }

    [JsonPropertyName("date")]
    public string? Date { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }
}

/// <summary>Envelope of <c>/v5/{type}/{id}/episodes/{ref}</c>.</summary>
/// <remarks>
/// The SINGLE-episode route carries a <c>numbering</c> block too, not just the list
/// route - the v5 contract shows <c>{ work, numbering, data }</c> for it. That is what
/// makes the absolute-numbering fallback verifiable rather than a guess: when a
/// <c>SxxEyy</c> reference misses and the bare <c>E{n}</c> form hits, the work's own
/// declared numbering is in the same response and can be required to agree before the
/// answer is accepted. Without it the fallback would be "try the other form and take
/// whatever comes back", which is precisely the shape of a silently wrong episode.
/// </remarks>
public sealed class EpisodeEnvelope
{
    [JsonPropertyName("numbering")]
    public EpisodeNumbering? Numbering { get; set; }

    [JsonPropertyName("data")]
    public Episode? Data { get; set; }
}

/// <summary>Envelope of the episode LIST endpoint.</summary>
public sealed class EpisodeListEnvelope
{
    [JsonPropertyName("numbering")]
    public EpisodeNumbering? Numbering { get; set; }

    [JsonPropertyName("data")]
    public IReadOnlyList<Episode>? Data { get; set; }

    [JsonPropertyName("pagination")]
    public Pagination? Pagination { get; set; }
}

/// <summary>The <c>numbering</c> block: says whether episode numbers run straight through.</summary>
public sealed class EpisodeNumbering
{
    /// <summary>
    /// Gets or sets whether episode numbers run straight through the whole run.
    /// </summary>
    /// <remarks>
    /// NULLABLE on purpose. As a plain <c>bool</c> a response carrying
    /// <c>"numbering": {}</c> - the block present but the field absent - would
    /// deserialize to <c>false</c>, and the absolute-numbering fallback accepts
    /// exactly <c>false</c> as its proof that the work is non-continuous. A missing
    /// field would then have read as a positive answer. Null makes "the API did not
    /// say" distinct from "the API said no", which is the only reading that lets the
    /// fallback refuse it.
    /// </remarks>
    [JsonPropertyName("continuous")]
    public bool? Continuous { get; set; }
}

/// <summary>One episode.</summary>
public sealed class Episode
{
    /// <summary>Gets or sets the reference: "S02E01", "E62" or "SP3".</summary>
    [JsonPropertyName("ref")]
    public string? Ref { get; set; }

    [JsonPropertyName("season")]
    public int? Season { get; set; }

    [JsonPropertyName("episode")]
    public int? EpisodeNumber { get; set; }

    [JsonPropertyName("episode_in_season")]
    public int? EpisodeInSeason { get; set; }

    [JsonPropertyName("episode_absolute")]
    public int? EpisodeAbsolute { get; set; }

    [JsonPropertyName("is_special")]
    public bool IsSpecial { get; set; }

    [JsonPropertyName("is_unaligned")]
    public bool IsUnaligned { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("synopsis")]
    public string? Synopsis { get; set; }

    [JsonPropertyName("aired")]
    public string? Aired { get; set; }

    /// <summary>Gets or sets the runtime in SECONDS. v5 converts TMDB's minutes for us.</summary>
    [JsonPropertyName("duration")]
    public int? Duration { get; set; }

    [JsonPropertyName("image")]
    public string? Image { get; set; }

    [JsonPropertyName("images")]
    public IReadOnlyList<EpisodeImage>? Images { get; set; }
}

/// <summary>An episode still.</summary>
public sealed class EpisodeImage
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("preview_url")]
    public string? PreviewUrl { get; set; }

    [JsonPropertyName("width")]
    public int? Width { get; set; }

    [JsonPropertyName("height")]
    public int? Height { get; set; }
}

/// <summary>
/// Envelope of <c>/v5/search-name</c>. This is NOT the standard list envelope: it has no
/// <c>pagination</c> block and carries <c>query</c> and <c>count</c> instead.
/// </summary>
public sealed class SearchNameEnvelope
{
    [JsonPropertyName("query")]
    public string? Query { get; set; }

    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("data")]
    public IReadOnlyList<SearchNameHit>? Data { get; set; }
}

/// <summary>
/// One row from <c>/v5/search-name</c>.
/// </summary>
/// <remarks>
/// The field names differ from the Work object, deliberately on the API's side, so they
/// are mapped literally here rather than renamed to match Work:
///   - <c>internal_id</c>, NOT <c>id</c> - it is the v5 ULID
///   - <c>name</c>, NOT <c>title</c>
/// Keys that are null or empty are OMITTED from the payload, so every member is nullable.
///
/// There is deliberately no <c>tmdb_id</c>. A bare TMDB number does not identify a work -
/// movie 79744 and TV 79744 are different works - which is why the id space is split here
/// exactly as it is in <c>external_ids</c>.
/// </remarks>
public sealed class SearchNameHit
{
    /// <summary>Gets or sets the v5 ULID.</summary>
    [JsonPropertyName("internal_id")]
    public string? InternalId { get; set; }

    /// <summary>Gets or sets the work's name in the negotiated language.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("year")]
    public int? Year { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("api_url")]
    public string? ApiUrl { get; set; }

    [JsonPropertyName("tmdb_movie_id")]
    public string? TmdbMovieId { get; set; }

    [JsonPropertyName("tmdb_tv_id")]
    public string? TmdbTvId { get; set; }

    [JsonPropertyName("imdb_id")]
    public string? ImdbId { get; set; }

    [JsonPropertyName("mal_id")]
    public string? MalId { get; set; }

    [JsonPropertyName("anilist_id")]
    public string? AniListId { get; set; }

    /// <summary>
    /// Gets or sets a TITLE-MATCHED TMDB movie id. A guess, not a fact.
    /// </summary>
    /// <remarks>
    /// The API names these apart from the confirmed ids on purpose. Nothing here may
    /// treat the two as equivalent: the uncertain ids are never written into Jellyfin's
    /// ProviderIds under any setting, because doing so would publish a title match to
    /// every other metadata provider on the server as though it were established.
    /// </remarks>
    [JsonPropertyName("tmdb_movie_id_uncertain")]
    public string? TmdbMovieIdUncertain { get; set; }

    /// <summary>Gets or sets a TITLE-MATCHED TMDB series id. A guess, not a fact.</summary>
    [JsonPropertyName("tmdb_tv_id_uncertain")]
    public string? TmdbTvIdUncertain { get; set; }

    /// <summary>
    /// Gets a value indicating whether this row's only TMDB evidence is a title match.
    /// </summary>
    [JsonIgnore]
    public bool HasOnlyUncertainTmdb =>
        string.IsNullOrEmpty(TmdbMovieId)
        && string.IsNullOrEmpty(TmdbTvId)
        && (!string.IsNullOrEmpty(TmdbMovieIdUncertain) || !string.IsNullOrEmpty(TmdbTvIdUncertain));
}
