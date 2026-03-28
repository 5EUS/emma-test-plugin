#if PLUGIN_TRANSPORT_WASM
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using EMMA.Plugin.Common;

namespace EMMA.TestPlugin.Infrastructure;

internal sealed class WasmMangadexClient
{
    private static readonly HttpClient Http = CreateHttpClient();

    public SearchParseMapResult SearchFromPayloadWithTimings(string query, string payloadJson)
    {
        payloadJson = ResolvePayloadContent(payloadJson);

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return new SearchParseMapResult([], 0, 0);
        }

        var parseStopwatch = Stopwatch.StartNew();
        using var doc = JsonDocument.Parse(payloadJson);
        var data = PluginJsonElement.GetArray(doc.RootElement, "data");
        parseStopwatch.Stop();

        if (data is null)
        {
            return new SearchParseMapResult([], parseStopwatch.ElapsedMilliseconds, 0);
        }

        var mapStopwatch = Stopwatch.StartNew();
        var results = new List<SearchItem>();
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

            var thumbnailUrl = BuildThumbnailUrl(item);
            var description = GetDescription(item);

            results.Add(new SearchItem(
                id,
                "mangadex",
                title,
                "paged",
                thumbnailUrl,
                description));
        }

            mapStopwatch.Stop();
            return new SearchParseMapResult(results, parseStopwatch.ElapsedMilliseconds, mapStopwatch.ElapsedMilliseconds);
    }

    public IReadOnlyList<ChapterItem> GetChaptersFromPayload(string mediaId, string payloadJson)
    {
        payloadJson = ResolvePayloadContent(payloadJson);

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return [];
        }

        using var doc = JsonDocument.Parse(payloadJson);
        var data = PluginJsonElement.GetArray(doc.RootElement, "data");
        if (data is null)
        {
            return [];
        }

        var results = new List<ChapterItem>();
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

            results.Add(new ChapterItem(id, number, title));
            index++;
        }

        return results;
    }

    public IReadOnlyList<ChapterOperationItem> GetChapterOperationItemsFromPayload(string mediaId, string payloadJson)
    {
        payloadJson = ResolvePayloadContent(payloadJson);

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return [];
        }

        using var doc = JsonDocument.Parse(payloadJson);
        var data = PluginJsonElement.GetArray(doc.RootElement, "data");
        if (data is null)
        {
            return [];
        }

        var results = new List<ChapterOperationItem>();
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

            var uploaderGroups = ExtractUploaderGroups(item);
            results.Add(new ChapterOperationItem(id, number, title, uploaderGroups));
            index++;
        }

        return results;
    }

    public PageItem? GetPageFromPayload(string chapterId, int pageIndex, string payloadJson)
    {
        payloadJson = ResolvePayloadContent(payloadJson);

        if (string.IsNullOrWhiteSpace(chapterId) || pageIndex < 0 || string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        if (!TryParseAtHomePayload(payloadJson, out var atHomePayload))
        {
            return null;
        }

        if (pageIndex >= atHomePayload.Files.Count)
        {
            return null;
        }

        var fileName = atHomePayload.Files[pageIndex];
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var pageId = $"{chapterId}:{pageIndex}";

        return new PageItem(
            pageId,
            pageIndex,
            $"{atHomePayload.BaseUrl}/{atHomePayload.DataPathSegment}/{atHomePayload.Hash}/{fileName}");
    }

    public IReadOnlyList<PageItem> GetPagesFromPayload(
        string chapterId,
        int startIndex,
        int count,
        string payloadJson)
    {
        payloadJson = ResolvePayloadContent(payloadJson);

        if (string.IsNullOrWhiteSpace(chapterId)
            || startIndex < 0
            || count <= 0
            || string.IsNullOrWhiteSpace(payloadJson))
        {
            return [];
        }

        if (!TryParseAtHomePayload(payloadJson, out var atHomePayload))
        {
            return [];
        }

        if (startIndex >= atHomePayload.Files.Count)
        {
            return [];
        }

        var endExclusive = Math.Min(atHomePayload.Files.Count, startIndex + count);
        var pages = new List<PageItem>(Math.Max(0, endExclusive - startIndex));

        for (var pageIndex = startIndex; pageIndex < endExclusive; pageIndex++)
        {
            var fileName = atHomePayload.Files[pageIndex];
            if (string.IsNullOrWhiteSpace(fileName))
            {
                continue;
            }

            pages.Add(new PageItem(
                $"{chapterId}:{pageIndex}",
                pageIndex,
                $"{atHomePayload.BaseUrl}/{atHomePayload.DataPathSegment}/{atHomePayload.Hash}/{fileName}"));
        }

        return pages;
    }

    public string? FetchSearchPayload(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var encodedQuery = Uri.EscapeDataString(query.Trim());
        return TryFetchPayload($"https://api.mangadex.org/manga?title={encodedQuery}&limit=20&contentRating[]=safe&contentRating[]=suggestive&includes[]=cover_art");
    }

    public string? FetchChaptersPayload(string mediaId)
    {
        if (string.IsNullOrWhiteSpace(mediaId))
        {
            return null;
        }

        var encodedMediaId = Uri.EscapeDataString(mediaId.Trim());
        return TryFetchPayload($"https://api.mangadex.org/manga/{encodedMediaId}/feed?limit=100&order[chapter]=asc&translatedLanguage[]=en&includeUnavailable=1");
    }

    public string? FetchAtHomePayload(string chapterId)
    {
        if (string.IsNullOrWhiteSpace(chapterId))
        {
            return null;
        }

        var encodedChapterId = Uri.EscapeDataString(chapterId.Trim());
        return TryFetchPayload($"https://api.mangadex.org/at-home/server/{encodedChapterId}");
    }

    internal static string ResolvePayloadContent(string payload)
    {
        return PluginJsonPayload.Normalize(payload);
    }

    private static string? TryFetchPayload(string absoluteUrl)
    {
        try
        {
            var payload = Http.GetStringAsync(absoluteUrl).GetAwaiter().GetResult();
            return ResolvePayloadContent(payload);
        }
        catch
        {
            return null;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "EMMA-TestPlugin/1.0");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        return client;
    }

    private static bool TryParseAtHomePayload(string payloadJson, out AtHomePayload payload)
    {
        payload = default;

        using var doc = JsonDocument.Parse(payloadJson);

        var baseUrl = PluginJsonElement.GetString(doc.RootElement, "baseUrl");
        var chapter = PluginJsonElement.GetObject(doc.RootElement, "chapter");
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

        payload = new AtHomePayload(baseUrl, hash, dataPathSegment, fileNames);
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

    private static string[] ExtractUploaderGroups(JsonElement chapterItem)
    {
        if (!chapterItem.TryGetProperty("relationships", out var relationships) ||
            relationships.ValueKind != JsonValueKind.Array)
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

            if (!relation.TryGetProperty("type", out var typeProp) ||
                typeProp.ValueKind != JsonValueKind.String ||
                !string.Equals(typeProp.GetString(), "scanlation_group", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? name = null;
            if (relation.TryGetProperty("attributes", out var attributes) &&
                attributes.ValueKind == JsonValueKind.Object &&
                attributes.TryGetProperty("name", out var nameProp) &&
                nameProp.ValueKind == JsonValueKind.String)
            {
                name = nameProp.GetString();
            }

            name ??= PluginJsonElement.GetString(relation, "id");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var normalized = name.Trim();
            if (normalized.Length == 0 ||
                groups.Any(existing => string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            groups.Add(normalized);
        }

        return [.. groups];
    }

    private readonly record struct AtHomePayload(
        string BaseUrl,
        string Hash,
        string DataPathSegment,
        IReadOnlyList<string> Files);

    public readonly record struct SearchParseMapResult(
        IReadOnlyList<SearchItem> Results,
        long ParseMs,
        long MapMs);
}
#endif