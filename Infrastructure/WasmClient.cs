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
    private const int StatisticsBatchSize = 50;

    private static readonly CoreClient Core = new();
    private static readonly HttpClient Http = CreateHttpClient();

    public SearchParseMapResult SearchFromPayloadWithTimings(string payloadJson)
    {
        var parseMap = Core.SearchFromPayloadWithTimings(payloadJson);
        var results = parseMap.Results ?? [];
        if (results.Count == 0)
        {
            return new SearchParseMapResult(results, parseMap.ParseMs, parseMap.MapMs);
        }

        // For WASM transport, enrich parsed search items with statistics
        var enriched = new List<SearchItem>(results.Count);
        var statisticsById = FetchStatisticsMetadata(results.Select(item => item.id));
        foreach (var item in results)
        {
            // Start with any metadata parsed from the search payload
            var metadata = item.metadata is null
                ? new List<MetadataItem>()
                : new List<MetadataItem>(item.metadata);

            // Merge statistics fetched once for the entire result set
            if (statisticsById.TryGetValue(item.id, out var statsItems) && statsItems.Count > 0)
            {
                metadata.AddRange(statsItems);
            }

            // Create a copy of the SearchItem with merged metadata when present
            enriched.Add(item with { metadata = metadata.Count > 0 ? metadata : item.metadata });
        }

        return new SearchParseMapResult(enriched, parseMap.ParseMs, parseMap.MapMs);
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