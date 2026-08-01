using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.PdCodesApi.Api;

/// <summary>
/// Deserializes a JSON object into a <see cref="Dictionary{TKey, TValue}"/> of
/// <see cref="string"/> to <see cref="string"/>, accepting EITHER a JSON string or a JSON
/// number for each value.
/// </summary>
/// <remarks>
/// <c>external_ids</c> on a v5 Work is not uniformly typed: <c>mal</c>, <c>anilist</c>,
/// <c>tmdb_tv</c>, <c>tvdb_series</c> and most other per-source ids are JSON NUMBERS,
/// while <c>wikidata</c> ("Q61799516"), <c>imdb</c> ("tt8696458") and <c>animeplanet</c>
/// (a URL slug) are JSON STRINGS - because that is what those sources' own ids actually
/// are. The plain <c>Dictionary&lt;string, string&gt;</c> System.Text.Json would otherwise
/// use throws <c>JsonException</c> the moment it meets the first numeric value, which
/// killed EVERY search result containing an <c>anilist</c> or <c>tmdb_tv</c> id the moment
/// /v5/search started returning real, populated results (see WorkSearchIndex / v5 Typesense
/// integration) - before that, search rarely returned enough real data to hit this path.
///
/// This plugin does not use these ids as numbers anywhere - see PdCodesIds.ToLookupPairs
/// and ToJellyfinProviderIds, which treat every external id as a string token to build a
/// Jellyfin ProviderId from. Normalizing a numeric id to its string form here loses
/// nothing; going the other way (making the dictionary <c>Dictionary&lt;string, object&gt;</c>
/// and asking every call site to know which entries might be numbers) would just move this
/// exact bug to every reader instead of fixing it once.
/// </remarks>
public sealed class MixedTypeStringDictionaryConverter : JsonConverter<Dictionary<string, string>>
{
    /// <inheritdoc />
    public override Dictionary<string, string>? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"Expected a JSON object for a mixed-type id dictionary, got {reader.TokenType}.");
        }

        var result = new Dictionary<string, string>();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return result;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException($"Expected a property name in a mixed-type id dictionary, got {reader.TokenType}.");
            }

            var key = reader.GetString() ?? throw new JsonException("A mixed-type id dictionary key was null.");
            reader.Read();

            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    result[key] = reader.GetString() ?? string.Empty;
                    break;

                case JsonTokenType.Number:
                    // Every numeric id this contract sends is an integer (a MAL/AniList/TMDB/
                    // TVDB numeric id); reading as a raw token text avoids a float round-trip
                    // that could otherwise turn, say, 80826 into "80826.0".
                    result[key] = reader.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
                    break;

                case JsonTokenType.Null:
                    // The v5 contract states null/empty keys are omitted from the payload
                    // entirely, so this should not occur - but skipping rather than throwing
                    // keeps one unexpected null from failing an otherwise-good response for
                    // every OTHER id it carries.
                    break;

                default:
                    throw new JsonException(
                        $"Unexpected token type {reader.TokenType} for id '{key}' in a mixed-type id dictionary.");
            }
        }

        throw new JsonException("Unexpected end of JSON while reading a mixed-type id dictionary.");
    }

    /// <inheritdoc />
    public override void Write(
        Utf8JsonWriter writer,
        Dictionary<string, string> value,
        JsonSerializerOptions options)
    {
        // This plugin never SERIALIZES a Work back to the API - it only reads responses.
        // Implemented anyway because JsonConverter<T> requires it; writes every value back
        // out as a JSON string, which is a safe, information-preserving (if not
        // byte-identical) round trip.
        writer.WriteStartObject();

        foreach (var (key, val) in value)
        {
            writer.WriteString(key, val);
        }

        writer.WriteEndObject();
    }
}
