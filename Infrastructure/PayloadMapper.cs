using System.Globalization;
using System.Text.Json;
using EMMA.Plugin.Common;
using static EMMA.Plugin.Common.PluginPayloadMapperBase;

namespace EMMA.TestPlugin.Infrastructure;

/// <summary>
/// Mangadex-specific payload mapper.
/// Uses reusable parsing patterns from PluginPayloadMapperBase utility class.
/// </summary>
internal static class PayloadMapper
{
    #region Constants
    public const string SourceId = "mangadex";
    public const string MediaTypePaged = "paged";
    private static readonly System.Text.RegularExpressions.Regex ChapterPrefixPattern = CreateChapterPrefixPattern();
    #endregion

    #region Public Mapping API

    public static IReadOnlyList<MangadexSearchEntry> ParseSearchEntries(JsonElement root)
    {
        var data = GetArray(root, "data");
        if (data is null)
        {
            return [];
        }

        var results = new List<MangadexSearchEntry>();
        foreach (var item in data.Value.EnumerateArray())
        {
            var id = GetString(item, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var title = GetTitle(item);
            if (string.IsNullOrWhiteSpace(title))
            {
                title = "Untitled";
            }

            results.Add(new MangadexSearchEntry(
                id,
                title,
                BuildThumbnailUrl(item),
                GetDescription(item)));
        }

        return results;
    }

    public static IReadOnlyDictionary<string, List<MetadataItem>> ExtractSearchMetadata(JsonElement root)
    {
        var metadataById = new Dictionary<string, List<MetadataItem>>(StringComparer.OrdinalIgnoreCase);
        var includedNames = BuildIncludedNameLookup(root);

        var data = GetArray(root, "data");
        if (data is null)
        {
            return metadataById;
        }

        foreach (var item in data.Value.EnumerateArray())
        {
            var id = GetString(item, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var metadata = ExtractSearchItemMetadata(item, includedNames);
            metadataById[id] = metadata;
        }

        return metadataById;
    }

    public static IReadOnlyDictionary<string, List<MetadataItem>> ExtractStatisticsMetadata(JsonElement root)
    {
        var metadataById = new Dictionary<string, List<MetadataItem>>(StringComparer.OrdinalIgnoreCase);
        var statistics = GetObject(root, "statistics");
        if (statistics is null)
        {
            return metadataById;
        }

        foreach (var item in statistics.Value.EnumerateObject())
        {
            var metadata = ExtractStatisticsItemMetadata(item.Value);
            if (metadata.Count > 0)
            {
                metadataById[item.Name] = metadata;
            }
        }

        return metadataById;
    }

    public static IReadOnlyList<MangadexChapterEntry> ParseChapterEntries(JsonElement root)
    {
        var data = GetArray(root, "data");
        if (data is null)
        {
            return [];
        }

        var scanlationGroupNameById = BuildScanlationGroupNameById(root);
        var results = new List<MangadexChapterEntry>();
        var index = 0;

        foreach (var item in data.Value.EnumerateArray())
        {
            var id = GetString(item, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var attributes = GetObject(item, "attributes");
            var pages = attributes is null ? null : GetInt32(attributes.Value, "pages");
            if (pages is not null && pages <= 0)
            {
                continue;
            }

            var title = attributes is null ? null : GetString(attributes.Value, "title");
            var chapterText = attributes is null ? null : GetString(attributes.Value, "chapter");
            var number = ParseChapterNumber(chapterText, index + 1);

            var formattedTitle = FormatChapterTitle(chapterText, title, number, ChapterPrefixPattern);

            var uploaderGroups = ExtractUploaderGroups(item, scanlationGroupNameById);
            results.Add(new MangadexChapterEntry(id, number, formattedTitle, uploaderGroups));
            index++;
        }

        return results;
    }

    public static bool TryParseAtHomePayload(string payloadJson, out MangadexAtHomePayload payload)
    {
        using var doc = JsonDocument.Parse(payloadJson);
        return TryParseAtHomePayload(doc.RootElement, out payload);
    }

    public static bool TryParseAtHomePayload(JsonElement root, out MangadexAtHomePayload payload)
    {
        payload = default;

        var baseUrl = GetString(root, "baseUrl");
        var chapter = GetObject(root, "chapter");
        if (string.IsNullOrWhiteSpace(baseUrl) || chapter is null)
        {
            return false;
        }

        var hash = GetString(chapter.Value, "hash");
        var files = GetArray(chapter.Value, "data");
        var dataPathSegment = "data";
        if (files is null || files.Value.GetArrayLength() == 0)
        {
            files = GetArray(chapter.Value, "dataSaver");
            dataPathSegment = "data-saver";
        }

        if (string.IsNullOrWhiteSpace(hash) || files is null)
        {
            return false;
        }

        var fileNames = ExtractStringArray(files);
        if (fileNames.Count == 0)
        {
            return false;
        }

        payload = new MangadexAtHomePayload(baseUrl, hash, dataPathSegment, fileNames);
        return true;
    }

    #endregion

    #region Search Field Extractors

    private static string? GetTitle(JsonElement item)
    {
        var attributes = PluginJsonElement.GetObject(item, "attributes");
        if (attributes is null)
        {
            return null;
        }

        var titleMap = PluginJsonElement.GetObject(attributes.Value, "title");
        return PluginJsonElement.PickMapString(titleMap);
    }

    private static string? GetDescription(JsonElement item)
    {
        var attributes = PluginJsonElement.GetObject(item, "attributes");
        if (attributes is null)
        {
            return null;
        }

        var descriptionMap = PluginJsonElement.GetObject(attributes.Value, "description");
        return PluginJsonElement.PickMapString(descriptionMap);
    }

    private static string? BuildThumbnailUrl(JsonElement item)
    {
        var mangaId = PluginJsonElement.GetString(item, "id");
        if (string.IsNullOrWhiteSpace(mangaId))
        {
            return null;
        }

        var relationships = PluginJsonElement.GetArray(item, "relationships");
        if (relationships is null)
        {
            return null;
        }

        foreach (var relation in relationships.Value.EnumerateArray())
        {
            var relationType = PluginJsonElement.GetString(relation, "type");
            if (!string.Equals(relationType, "cover_art", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var attributes = PluginJsonElement.GetObject(relation, "attributes");
            if (attributes is null)
            {
                continue;
            }

            var fileName = PluginJsonElement.GetString(attributes.Value, "fileName");
            if (string.IsNullOrWhiteSpace(fileName))
            {
                continue;
            }

            return $"https://uploads.mangadex.org/covers/{mangaId}/{fileName}";
        }

        return null;
    }

    #endregion

    #region Metadata Extraction Helpers

    private static List<MetadataItem> ExtractSearchItemMetadata(
        JsonElement item,
        IReadOnlyDictionary<string, string> includedNames)
    {
        var metadata = new List<MetadataItem>();

        var attributes = PluginJsonElement.GetObject(item, "attributes");
        if (attributes is null)
        {
            return metadata;
        }

        AddLocalizedList(metadata, "Alt Titles", PluginJsonElement.GetArray(attributes.Value, "altTitles"));

        if (attributes.Value.TryGetProperty("year", out var yearProp) &&
            (yearProp.ValueKind == JsonValueKind.Number || yearProp.ValueKind == JsonValueKind.String))
        {
            var year = yearProp.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(year))
            {
                metadata.Add(new MetadataItem("Year", year));
            }
        }

        if (attributes.Value.TryGetProperty("publicationDemographic", out var demographicProp) &&
            demographicProp.ValueKind == JsonValueKind.String)
        {
            var demographic = demographicProp.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(demographic))
            {
                metadata.Add(new MetadataItem("Demographic", demographic));
            }
        }

        if (attributes.Value.TryGetProperty("contentRating", out var contentRating) &&
            contentRating.ValueKind == JsonValueKind.String)
        {
            var rating = contentRating.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(rating))
            {
                metadata.Add(new MetadataItem("Content Rating", rating));
            }
        }

        if (attributes.Value.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String)
        {
            var statusValue = status.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(statusValue))
            {
                metadata.Add(new MetadataItem("Status", statusValue));
            }
        }

        AddTagNames(metadata, attributes.Value);

        AddRelationshipNames(metadata, item, includedNames, "author", "Author");
        AddRelationshipNames(metadata, item, includedNames, "artist", "Artist");

        return metadata;
    }

    private static List<MetadataItem> ExtractStatisticsItemMetadata(JsonElement item)
    {
        var metadata = new List<MetadataItem>();

        if (!item.TryGetProperty("rating", out var rating) || rating.ValueKind != JsonValueKind.Object)
        {
            return metadata;
        }

        var score = GetRatingScore(rating);
        if (!string.IsNullOrWhiteSpace(score))
        {
            metadata.Add(new MetadataItem("Rating", score));
        }

        return metadata;
    }

    private static string? GetRatingScore(JsonElement rating)
    {
        string? raw = null;

        if (rating.TryGetProperty("bayesian", out var bayesian) &&
            (bayesian.ValueKind == JsonValueKind.Number || bayesian.ValueKind == JsonValueKind.String))
        {
            raw = bayesian.ToString().Trim();
        }

        if (string.IsNullOrWhiteSpace(raw) && rating.TryGetProperty("average", out var average) &&
            (average.ValueKind == JsonValueKind.Number || average.ValueKind == JsonValueKind.String))
        {
            raw = average.ToString().Trim();
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        // Normalize numeric ratings to at-most two decimal places and trim
        // trailing zeros (eg. 4 -> "4", 4.5 -> "4.5", 4.12345 -> "4.12").
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        return raw;
    }

    private static void AddLocalizedList(
        List<MetadataItem> metadata,
        string label,
        JsonElement? array)
    {
        if (array is null || array.Value.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var values = new List<string>();
        foreach (var item in array.Value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var text = item.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    values.Add(text);
                }
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var localized = PluginJsonElement.PickMapString(item);
            if (!string.IsNullOrWhiteSpace(localized))
            {
                values.Add(localized);
            }
        }

        if (values.Count > 0)
        {
            metadata.Add(new MetadataItem(label, string.Join(", ", values.Distinct(StringComparer.OrdinalIgnoreCase))));
        }
    }

    private static void AddTagNames(List<MetadataItem> metadata, JsonElement attributes)
    {
        if (!attributes.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var names = new List<string>();
        foreach (var tag in tags.EnumerateArray())
        {
            if (tag.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var tagAttributes = PluginJsonElement.GetObject(tag, "attributes");
            if (tagAttributes is null)
            {
                continue;
            }

            var nameMap = PluginJsonElement.GetObject(tagAttributes.Value, "name");
            var name = PluginJsonElement.PickMapString(nameMap)?.Trim();
            if (!string.IsNullOrWhiteSpace(name))
            {
                names.Add(name);
                continue;
            }

            var fallbackName = PluginJsonElement.GetString(tagAttributes.Value, "name")?.Trim();
            if (!string.IsNullOrWhiteSpace(fallbackName))
            {
                names.Add(fallbackName);
            }
        }

        if (names.Count > 0)
        {
            metadata.Add(new MetadataItem("Genres", string.Join(", ", names.Distinct(StringComparer.OrdinalIgnoreCase))));
        }
    }

    private static void AddRelationshipNames(
        List<MetadataItem> metadata,
        JsonElement item,
        IReadOnlyDictionary<string, string> includedNames,
        string relationshipType,
        string label)
    {
        if (!item.TryGetProperty("relationships", out var relationships) || relationships.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var names = new List<string>();
        foreach (var relationship in relationships.EnumerateArray())
        {
            if (relationship.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var type = PluginJsonElement.GetString(relationship, "type");
            // Accept exact matches, and treat "creator" as an alias for
            // "author" so creator records surface as Author metadata.
            if (!string.Equals(type, relationshipType, StringComparison.OrdinalIgnoreCase)
                && !(string.Equals(relationshipType, "author", StringComparison.OrdinalIgnoreCase)
                     && string.Equals(type, "creator", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var id = PluginJsonElement.GetString(relationship, "id")?.Trim();
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            string? resolved = null;
            if (includedNames.TryGetValue($"{type}:{id}", out var resolvedName) && !string.IsNullOrWhiteSpace(resolvedName))
            {
                resolved = resolvedName;
            }
            else if (includedNames.TryGetValue($"{relationshipType}:{id}", out var resolvedName2) && !string.IsNullOrWhiteSpace(resolvedName2))
            {
                resolved = resolvedName2;
            }
            else if (relationship.TryGetProperty("attributes", out var relAttrs) && relAttrs.ValueKind == JsonValueKind.Object)
            {
                var rn = PluginJsonElement.GetString(relAttrs, "name")?.Trim();
                if (string.IsNullOrWhiteSpace(rn))
                {
                    // Do not treat `username` as an author/artist name. Only
                    // fall back to `username` for creator-like records.
                    var allowUsernameFallback = !string.Equals(
                        relationshipType,
                        "author",
                        StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(relationshipType, "artist", StringComparison.OrdinalIgnoreCase);

                    if (allowUsernameFallback)
                    {
                        rn = PluginJsonElement.GetString(relAttrs, "username")?.Trim();
                    }
                }

                if (!string.IsNullOrWhiteSpace(rn))
                {
                    resolved = rn;
                }
            }

            if (!string.IsNullOrWhiteSpace(resolved))
            {
                names.Add(resolved);
            }
        }

        if (names.Count > 0)
        {
            metadata.Add(new MetadataItem(label, string.Join(", ", names.Distinct(StringComparer.OrdinalIgnoreCase))));
        }
    }

    #endregion

    #region Included Relationship Lookup

    private static Dictionary<string, string> BuildIncludedNameLookup(JsonElement root)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var included = PluginJsonElement.GetArray(root, "included");
        if (included is null)
        {
            return map;
        }

        foreach (var item in included.Value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var type = PluginJsonElement.GetString(item, "type")?.Trim();
            var id = PluginJsonElement.GetString(item, "id")?.Trim();
            if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var attributes = PluginJsonElement.GetObject(item, "attributes");
            if (attributes is null)
            {
                continue;
            }

            var name = PluginJsonElement.GetString(attributes.Value, "name")?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                var titleMap = PluginJsonElement.GetObject(attributes.Value, "name");
                name = PluginJsonElement.PickMapString(titleMap)?.Trim();
            }

            // Fall back to `username` for creator-like records only. Avoid
            // using usernames for `author` or `artist` types to prevent
            // pooling unrelated usernames as creators.
            if (string.IsNullOrWhiteSpace(name))
            {
                var username = PluginJsonElement.GetString(attributes.Value, "username")?.Trim();
                if (!string.IsNullOrWhiteSpace(username)
                    && (string.Equals(type, "creator", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(type, "user", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(type, "scanlation_group", StringComparison.OrdinalIgnoreCase)))
                {
                    name = username;
                }
            }

            if (!string.IsNullOrWhiteSpace(name))
            {
                map[$"{type}:{id}"] = name;
            }
        }

        return map;
    }

    private static Dictionary<string, string> BuildScanlationGroupNameById(JsonElement root)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var included = PluginJsonElement.GetArray(root, "included");
        if (included is null)
        {
            return map;
        }

        foreach (var item in included.Value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var type = PluginJsonElement.GetString(item, "type");
            if (!string.Equals(type, "scanlation_group", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var id = PluginJsonElement.GetString(item, "id")?.Trim();
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var attributes = PluginJsonElement.GetObject(item, "attributes");
            var name = attributes is null ? null : PluginJsonElement.GetString(attributes.Value, "name");
            var normalizedName = name?.Trim();
            if (!string.IsNullOrWhiteSpace(normalizedName))
            {
                map[id] = normalizedName;
            }
        }

        return map;
    }

    private static string[] ExtractUploaderGroups(
        JsonElement chapterItem,
        Dictionary<string, string> scanlationGroupNameById)
    {
        if (!chapterItem.TryGetProperty("relationships", out var relationships)
            || relationships.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var groups = new List<string>();
        foreach (var relation in relationships.EnumerateArray())
        {
            if (relation.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!relation.TryGetProperty("type", out var typeProp)
                || typeProp.ValueKind != JsonValueKind.String
                || !string.Equals(typeProp.GetString(), "scanlation_group", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? name = null;
            if (relation.TryGetProperty("attributes", out var attributes)
                && attributes.ValueKind == JsonValueKind.Object
                && attributes.TryGetProperty("name", out var nameProp)
                && nameProp.ValueKind == JsonValueKind.String)
            {
                name = nameProp.GetString();
            }

            var id = PluginJsonElement.GetString(relation, "id");
            if (string.IsNullOrWhiteSpace(name)
                && !string.IsNullOrWhiteSpace(id)
                && scanlationGroupNameById.TryGetValue(id, out var resolvedName)
                && !string.IsNullOrWhiteSpace(resolvedName))
            {
                name = resolvedName;
            }

            name ??= id;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var normalized = name.Trim();
            if (normalized.Length == 0
                || groups.Any(existing => string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            groups.Add(normalized);
        }

        return [.. groups];
    }

    #endregion
}

#region Payload Records

internal readonly record struct MangadexSearchEntry(
    string Id,
    string Title,
    string? ThumbnailUrl,
    string? Description);

internal readonly record struct MangadexChapterEntry(
    string Id,
    int Number,
    string Title,
    string[] UploaderGroups);

internal readonly record struct MangadexAtHomePayload(
    string BaseUrl,
    string Hash,
    string DataPathSegment,
    IReadOnlyList<string> Files);

#endregion