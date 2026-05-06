#if PLUGIN_TRANSPORT_WASM
using System.Net.Http;
using System.Text.Json;
using EMMA.Plugin.Common;
using LibraryWorld.wit.imports.emma.plugin;

namespace EMMA.TestPlugin.Infrastructure;

internal sealed class WasmClient
{
    private const int ChapterFeedPageSize = 500;
    private const int ChapterFeedMaxPages = 20;
    private const int StatisticsBatchSize = 150;

    private static readonly CoreClient Core = new();
    public SearchParseMapResult SearchFromPayloadWithTimings(string payloadJson)
    {
        // Return results quickly without fetching statistics.
        // Metadata should be loaded on-demand via EnrichSearchItemsWithStatistics().
        var parseMap = Core.SearchFromPayloadWithTimings(payloadJson);
        return new SearchParseMapResult(parseMap.Results ?? [], parseMap.ParseMs, parseMap.MapMs);
    }

    /// <summary>
    /// Enriches search items with statistics metadata on-demand.
    /// This is called after search results are presented to the user, not during search,
    /// to keep search performance subsecond while still providing rich metadata when needed.
    /// </summary>
    /// <param name="enrichmentArgsJson">JSON with optional "itemIds" array and optional "baseItems" array</param>
    /// <returns>Search items with merged statistics metadata</returns>
    public IReadOnlyList<SearchItem> EnrichSearchItemsWithStatistics(string enrichmentArgsJson)
    {
        List<string> ids = [];
        IReadOnlyList<SearchItem>? baseItems = null;

        if (!string.IsNullOrWhiteSpace(enrichmentArgsJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(enrichmentArgsJson);
                var root = doc.RootElement;

                // Extract itemIds array
                if (root.TryGetProperty("itemIds", out var idsElement) && idsElement.ValueKind == JsonValueKind.Array)
                {
                    ids = idsElement.EnumerateArray()
                        .Select(el => el.GetString() ?? "")
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }

                // Extract optional baseItems array
                if (root.TryGetProperty("baseItems", out var baseItemsElement) && baseItemsElement.ValueKind == JsonValueKind.Array)
                {
                    var baseItemsList = new List<SearchItem>();
                    foreach (var item in baseItemsElement.EnumerateArray())
                    {
                        var id = PluginJsonElement.GetString(item, "id");
                        var source = PluginJsonElement.GetString(item, "source");
                        var title = PluginJsonElement.GetString(item, "title");
                        var mediaType = PluginJsonElement.GetString(item, "mediaType");
                        var thumbnailUrl = PluginJsonElement.GetString(item, "thumbnailUrl");
                        var description = PluginJsonElement.GetString(item, "description");

                        if (!string.IsNullOrWhiteSpace(id))
                        {
                            IReadOnlyList<MetadataItem>? metadata = null;
                            if (item.TryGetProperty("metadata", out var metadataElement) && metadataElement.ValueKind == JsonValueKind.Array)
                            {
                                var metadataList = new List<MetadataItem>();
                                foreach (var meta in metadataElement.EnumerateArray())
                                {
                                    var key = PluginJsonElement.GetString(meta, "key");
                                    var value = PluginJsonElement.GetString(meta, "value");
                                    if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                                    {
                                        metadataList.Add(new MetadataItem(key, value));
                                    }
                                }
                                metadata = metadataList.Count > 0 ? metadataList : null;
                            }

                            baseItemsList.Add(new SearchItem(id, source ?? "", title ?? "", mediaType ?? "", thumbnailUrl, description, metadata));
                        }
                    }
                    baseItems = baseItemsList.Count > 0 ? baseItemsList : null;
                }
            }
            catch
            {
                // If parsing fails, proceed with empty/defaults
            }
        }

        if (ids.Count == 0)
        {
            return [];
        }

        var statisticsById = FetchStatisticsMetadata(ids);
        if (statisticsById.Count == 0)
        {
            // No statistics fetched, return items as-is or create minimal items from IDs
            return baseItems ?? ids.Select(id => new SearchItem(id, "", "", "", null, null, null)).ToList();
        }

        var enriched = new List<SearchItem>();

        if (baseItems != null)
        {
            // Preserve original item data and merge statistics
            foreach (var item in baseItems)
            {
                var metadata = item.metadata is null
                    ? new List<MetadataItem>()
                    : new List<MetadataItem>(item.metadata);

                if (statisticsById.TryGetValue(item.id, out var statsItems) && statsItems.Count > 0)
                {
                    metadata.AddRange(statsItems);
                }

                enriched.Add(item with { metadata = metadata.Count > 0 ? metadata : item.metadata });
            }
        }
        else
        {
            // Create items with only statistics metadata
            foreach (var id in ids)
            {
                var metadata = new List<MetadataItem>();
                if (statisticsById.TryGetValue(id, out var statsItems) && statsItems.Count > 0)
                {
                    metadata.AddRange(statsItems);
                }

                enriched.Add(new SearchItem(id, "", "", "", null, null, metadata.Count > 0 ? metadata : null));
            }
        }

        return enriched;
    }

    public IReadOnlyList<ChapterItem> GetChaptersFromPayload(string mediaId, string payloadJson)
    {
        return MapAllChapterPages(
            mediaId,
            payloadJson,
            payload => Core.GetChaptersFromPayload(payload));
    }

    public IReadOnlyList<ChapterOperationItem> GetChapterOperationItemsFromPayload(string mediaId, string payloadJson)
    {
        return MapAllChapterPages(
            mediaId,
            payloadJson,
            payload => Core.GetChapterOperationItemsFromPayload(payload));
    }

    public PageItem? GetPageFromPayload(string chapterId, int pageIndex, string payloadJson)
    {
        return Core.GetPageFromPayload(chapterId, pageIndex, payloadJson);
    }

    public IReadOnlyList<PageItem> GetPagesFromPayload(
        string chapterId,
        int startIndex,
        int count,
        string payloadJson)
    {
        return Core.GetPagesFromPayload(chapterId, startIndex, count, payloadJson);
    }

    public string? FetchSearchPayload(string query)
    {
        var parsedQuery = PluginSearchQuery.Parse(query, fallbackQuery: query);
        return FetchSearchPayload(parsedQuery);
    }

    public string? FetchSearchPayload(PluginSearchQuery query)
    {
        var resolvedQuery = ProviderSearchQueryResolver.Resolve(query, TryFetchPayload);
        return TryFetchPayload(ProviderRequestUrls.BuildSearchAbsoluteUrl(resolvedQuery));
    }

    public string? FetchChaptersPayload(string mediaId)
    {
        return TryFetchPayload(ProviderRequestUrls.BuildChaptersAbsoluteUrl(mediaId));
    }

    public string? FetchAtHomePayload(string chapterId)
    {
        return TryFetchPayload(ProviderRequestUrls.BuildAtHomeAbsoluteUrl(chapterId));
    }

    private IReadOnlyDictionary<string, List<MetadataItem>> FetchStatisticsMetadata(IEnumerable<string> mangaIds)
    {
        var results = new Dictionary<string, List<MetadataItem>>(StringComparer.OrdinalIgnoreCase);
        var ids = mangaIds
            .Select(id => id.Trim())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ids.Count == 0)
        {
            return results;
        }

        foreach (var batch in ids.Chunk(StatisticsBatchSize))
        {
            var url = ProviderRequestUrls.BuildStatisticsAbsoluteUrl(batch);
            if (string.IsNullOrWhiteSpace(url))
            {
                foreach (var id in batch)
                {
                    TryFetchSingleStatistics(id, results);
                }

                continue;
            }

            var returnedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var payloadJson = TryFetchPayload(url);
            if (!string.IsNullOrWhiteSpace(payloadJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(payloadJson);
                    foreach (var (id, items) in PayloadMapper.ExtractStatisticsMetadata(doc.RootElement))
                    {
                        if (items.Count == 0)
                        {
                            continue;
                        }

                        returnedIds.Add(id);
                        if (!results.TryGetValue(id, out var existing))
                        {
                            results[id] = new List<MetadataItem>(items);
                            continue;
                        }

                        existing.AddRange(items);
                    }
                }
                catch
                {
                    // ignore malformed batch payload and try per-id fallback below
                }

                foreach (var id in batch)
                {
                    if (returnedIds.Contains(id))
                    {
                        continue;
                    }

                    if (TryExtractRatingMetadataForId(payloadJson, id, out var ratingItem))
                    {
                        returnedIds.Add(id);
                        if (!results.TryGetValue(id, out var existing))
                        {
                            results[id] = [ratingItem];
                        }
                        else
                        {
                            existing.Add(ratingItem);
                        }
                    }
                }
            }

            foreach (var id in batch)
            {
                if (returnedIds.Contains(id))
                {
                    continue;
                }

                TryFetchSingleStatistics(id, results);
            }
        }

        return results;
    }

    private static void TryFetchSingleStatistics(
        string mangaId,
        IDictionary<string, List<MetadataItem>> results)
    {
        var singleUrl = ProviderRequestUrls.BuildStatisticsAbsoluteUrl(mangaId);
        if (string.IsNullOrWhiteSpace(singleUrl))
        {
            return;
        }

        var singlePayload = TryFetchPayload(singleUrl);
        if (string.IsNullOrWhiteSpace(singlePayload))
        {
            return;
        }

        try
        {
            using var singleDoc = JsonDocument.Parse(singlePayload);
            foreach (var (singleId, items) in PayloadMapper.ExtractStatisticsMetadata(singleDoc.RootElement))
            {
                if (items.Count == 0)
                {
                    continue;
                }

                if (!results.TryGetValue(singleId, out var existing))
                {
                    results[singleId] = new List<MetadataItem>(items);
                    continue;
                }

                existing.AddRange(items);
            }

            if (TryExtractRatingMetadataForId(singlePayload, mangaId, out var ratingItem))
            {
                if (!results.TryGetValue(mangaId, out var existing))
                {
                    results[mangaId] = [ratingItem];
                }
                else
                {
                    existing.Add(ratingItem);
                }
            }
        }
        catch
        {
        }
    }

    private static bool TryExtractRatingMetadataForId(string payloadJson, string mangaId, out MetadataItem ratingItem)
    {
        ratingItem = default!;
        if (string.IsNullOrWhiteSpace(payloadJson) || string.IsNullOrWhiteSpace(mangaId))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("statistics", out var statistics)
                || statistics.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!statistics.TryGetProperty(mangaId, out var item)
                || item.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!item.TryGetProperty("rating", out var rating)
                || rating.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            string? score = null;
            if (rating.TryGetProperty("bayesian", out var bayesian)
                && (bayesian.ValueKind == JsonValueKind.Number || bayesian.ValueKind == JsonValueKind.String))
            {
                score = bayesian.ToString().Trim();
            }

            if (string.IsNullOrWhiteSpace(score)
                && rating.TryGetProperty("average", out var average)
                && (average.ValueKind == JsonValueKind.Number || average.ValueKind == JsonValueKind.String))
            {
                score = average.ToString().Trim();
            }

            if (string.IsNullOrWhiteSpace(score))
            {
                return false;
            }

            ratingItem = new MetadataItem("Rating", score);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static string ResolvePayloadContent(string payload)
    {
        return CoreClient.ResolvePayloadContent(payload);
    }

    private static IReadOnlyList<TChapter> MapAllChapterPages<TChapter>(
        string mediaId,
        string firstPayload,
        Func<string, IReadOnlyList<TChapter>> mapper)
        where TChapter : class
    {
        var firstNormalizedPayload = ResolvePayloadContent(firstPayload);
        if (string.IsNullOrWhiteSpace(firstNormalizedPayload))
        {
            return [];
        }

        var combined = new List<TChapter>();
        var seenChapterIds = new HashSet<string>(StringComparer.Ordinal);

        AppendMappedChapters(firstNormalizedPayload, mapper, combined, seenChapterIds);

        if (!TryGetChapterFeedPageStats(firstNormalizedPayload, out var firstStats))
        {
            return combined;
        }

        var fetchedPages = 1;
        var nextOffset = firstStats.Offset + firstStats.DataCount;

        while (fetchedPages < ChapterFeedMaxPages
            && nextOffset < firstStats.Total
            && !string.IsNullOrWhiteSpace(mediaId))
        {
            var nextPayload = TryFetchPayload(
                ProviderRequestUrls.BuildChaptersAbsoluteUrl(
                    mediaId,
                    limit: ChapterFeedPageSize,
                    offset: nextOffset));

            if (string.IsNullOrWhiteSpace(nextPayload))
            {
                break;
            }

            AppendMappedChapters(nextPayload, mapper, combined, seenChapterIds);

            if (!TryGetChapterFeedPageStats(nextPayload, out var nextStats) || nextStats.DataCount <= 0)
            {
                break;
            }

            nextOffset = nextStats.Offset + nextStats.DataCount;
            fetchedPages++;
        }

        return combined;
    }

    private static void AppendMappedChapters<TChapter>(
        string payload,
        Func<string, IReadOnlyList<TChapter>> mapper,
        List<TChapter> destination,
        HashSet<string> seenChapterIds)
        where TChapter : class
    {
        var mapped = mapper(payload);
        if (mapped.Count == 0)
        {
            return;
        }

        foreach (var chapter in mapped)
        {
            var chapterId = chapter switch
            {
                ChapterItem typed => typed.id,
                ChapterOperationItem op => op.id,
                _ => string.Empty
            };

            if (string.IsNullOrWhiteSpace(chapterId) || !seenChapterIds.Add(chapterId))
            {
                continue;
            }

            destination.Add(chapter);
        }
    }

    private static bool TryGetChapterFeedPageStats(string payload, out ChapterFeedPageStats stats)
    {
        stats = default;

        var normalized = ResolvePayloadContent(payload);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(normalized);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var dataCount = PluginJsonElement.GetArray(root, "data")?.GetArrayLength() ?? 0;
            var total = PluginJsonElement.GetInt32(root, "total") ?? dataCount;
            var offset = PluginJsonElement.GetInt32(root, "offset") ?? 0;

            stats = new ChapterFeedPageStats(dataCount, total, offset);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? TryFetchPayload(string? absoluteUrl)
    {
        if (string.IsNullOrWhiteSpace(absoluteUrl))
        {
            return null;
        }

        try
        {
            // In WASM transport the host is responsible for network I/O; delegate
            // to the host bridge to retrieve payloads. This avoids HttpClient
            // failures inside the WASM sandbox.
            var payload = HostBridgeInterop.OperationPayload("search", absoluteUrl);
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
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", ProviderHttpProfile.Defaults.UserAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", ProviderHttpProfile.Defaults.AcceptMediaType);
        return client;
    }

    public readonly record struct SearchParseMapResult(
        IReadOnlyList<SearchItem> Results,
        long ParseMs,
        long MapMs);

    private readonly record struct ChapterFeedPageStats(int DataCount, int Total, int Offset);
}
#endif