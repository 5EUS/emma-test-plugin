using System.Text.Json;
using EMMA.Plugin.Common;

namespace EMMA.TestPlugin.Infrastructure;

internal static class PayloadMapper
{
    public const string SourceId = "mangadex";
    public const string MediaTypePaged = "paged";
    public static IReadOnlyList<MangadexSearchEntry> ParseSearchEntries(JsonElement root)
    {
        var data = PluginJsonElement.GetArray(root, "data");
        if (data is null)
        {
            return [];
        }

        var results = new List<MangadexSearchEntry>();
        foreach (var item in data.Value.EnumerateArray())
        {
            var id = PluginJsonElement.GetString(item, "id");
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

    public static IReadOnlyList<MangadexChapterEntry> ParseChapterEntries(JsonElement root)
    {
        var data = PluginJsonElement.GetArray(root, "data");
        if (data is null)
        {
            return [];
        }

        var scanlationGroupNameById = BuildScanlationGroupNameById(root);
        var results = new List<MangadexChapterEntry>();
        var index = 0;

        foreach (var item in data.Value.EnumerateArray())
        {
            var id = PluginJsonElement.GetString(item, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var attributes = PluginJsonElement.GetObject(item, "attributes");
            var pages = attributes is null ? null : PluginJsonElement.GetInt32(attributes.Value, "pages");
            if (pages is not null && pages <= 0)
            {
                continue;
            }

            var title = attributes is null ? null : PluginJsonElement.GetString(attributes.Value, "title");
            var chapterText = attributes is null ? null : PluginJsonElement.GetString(attributes.Value, "chapter");
            var number = index + 1;
            if (!string.IsNullOrWhiteSpace(chapterText) && int.TryParse(chapterText, out var parsed))
            {
                number = parsed;
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                title = string.IsNullOrWhiteSpace(chapterText)
                    ? $"Chapter {number}"
                    : $"Chapter {chapterText}";
            }

            var uploaderGroups = ExtractUploaderGroups(item, scanlationGroupNameById);
            results.Add(new MangadexChapterEntry(id, number, title, uploaderGroups));
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

        var baseUrl = PluginJsonElement.GetString(root, "baseUrl");
        var chapter = PluginJsonElement.GetObject(root, "chapter");
        if (string.IsNullOrWhiteSpace(baseUrl) || chapter is null)
        {
            return false;
        }

        var hash = PluginJsonElement.GetString(chapter.Value, "hash");
        var files = PluginJsonElement.GetArray(chapter.Value, "data");
        var dataPathSegment = "data";
        if (files is null || files.Value.GetArrayLength() == 0)
        {
            files = PluginJsonElement.GetArray(chapter.Value, "dataSaver");
            dataPathSegment = "data-saver";
        }

        if (string.IsNullOrWhiteSpace(hash) || files is null)
        {
            return false;
        }

        var fileNames = files.Value.EnumerateArray()
            .Select(file => file.GetString())
            .Where(file => !string.IsNullOrWhiteSpace(file))
            .Select(file => file!)
            .ToList();

        payload = new MangadexAtHomePayload(baseUrl, hash, dataPathSegment, fileNames);
        return true;
    }

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
        IReadOnlyDictionary<string, string> scanlationGroupNameById)
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
}

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