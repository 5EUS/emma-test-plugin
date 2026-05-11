using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using EMMA.Plugin.Common;

namespace EMMA.TestPlugin.Core;

internal sealed class CoreClient
{
    private const int StatisticsBatchSize = 150;
    private static readonly IReadOnlyDictionary<string, List<MetadataItem>> EmptyMetadataMap =
        new Dictionary<string, List<MetadataItem>>(StringComparer.OrdinalIgnoreCase);

    public SearchParseMapResult SearchFromPayloadWithTimings(string payloadJson)
    {
        var normalizedPayload = ResolvePayloadContent(payloadJson);
        if (string.IsNullOrWhiteSpace(normalizedPayload))
        {
            return new SearchParseMapResult([], 0, 0);
        }

        var parseStopwatch = Stopwatch.StartNew();
        using var doc = JsonDocument.Parse(normalizedPayload);
        parseStopwatch.Stop();

        var mapStopwatch = Stopwatch.StartNew();
        var entries = PayloadMapper.ParseSearchEntries(doc.RootElement);
        var metadataById = PayloadMapper.ExtractSearchMetadata(doc.RootElement);
        var results = new List<SearchItem>(entries.Count);
        foreach (var entry in entries)
        {
            metadataById.TryGetValue(entry.Id, out var metadata);
            results.Add(new SearchItem(
                entry.Id,
                PayloadMapper.SourceId,
                entry.Title,
                PayloadMapper.MediaTypePaged,
                entry.ThumbnailUrl,
                entry.Description,
                metadata));
        }

        mapStopwatch.Stop();
        return new SearchParseMapResult(results, parseStopwatch.ElapsedMilliseconds, mapStopwatch.ElapsedMilliseconds);
    }

    public IReadOnlyList<SearchItem> EnrichSearchItemsWithStatistics(
        string enrichmentArgsJson,
        Func<string, string?> fetchAbsoluteUrl)
    {
        ArgumentNullException.ThrowIfNull(fetchAbsoluteUrl);

        var request = ParseSearchEnrichmentRequest(enrichmentArgsJson);
        if (request.ItemIds.Count == 0)
        {
            return [];
        }

        var statisticsById = FetchStatisticsMetadata(request.ItemIds, fetchAbsoluteUrl);
        return MergeEnrichedSearchItems(request.ItemIds, request.BaseItems, statisticsById);
    }

    public async Task<IReadOnlyList<SearchItem>> EnrichSearchItemsAsync(
        IReadOnlyList<SearchItem> items,
        Func<string, CancellationToken, Task<string?>> fetchAbsoluteUrlAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(fetchAbsoluteUrlAsync);

        if (items.Count == 0)
        {
            return items;
        }

        var itemIds = items
            .Select(static item => item.id)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (itemIds.Length == 0)
        {
            return items;
        }

        var statisticsById = await FetchStatisticsMetadataAsync(itemIds, fetchAbsoluteUrlAsync, cancellationToken);
        return MergeEnrichedSearchItems(itemIds, items, statisticsById);
    }

    public IReadOnlyList<ChapterItem> GetChaptersFromPayload(string payloadJson)
    {
        var entries = ParseChapterEntries(payloadJson);
        var results = new List<ChapterItem>(entries.Count);
        foreach (var entry in entries)
        {
            results.Add(new ChapterItem(entry.Id, entry.Number, entry.Title, entry.UploaderGroups));
        }

        return results;
    }

    public IReadOnlyList<ChapterOperationItem> GetChapterOperationItemsFromPayload(string payloadJson)
    {
        var entries = ParseChapterEntries(payloadJson);
        var results = new List<ChapterOperationItem>(entries.Count);
        foreach (var entry in entries)
        {
            results.Add(new ChapterOperationItem(entry.Id, entry.Number, entry.Title, entry.UploaderGroups));
        }

        return results;
    }

    public PageItem? GetPageFromPayload(string chapterId, int pageIndex, string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(chapterId) || pageIndex < 0)
        {
            return null;
        }

        if (!TryGetAtHomePayload(payloadJson, out var atHomePayload))
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

        return new PageItem(
            $"{chapterId}:{pageIndex}",
            pageIndex,
            $"{atHomePayload.BaseUrl}/{atHomePayload.DataPathSegment}/{atHomePayload.Hash}/{fileName}");
    }

    public IReadOnlyList<PageItem> GetPagesFromPayload(
        string chapterId,
        int startIndex,
        int count,
        string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(chapterId) || startIndex < 0 || count <= 0)
        {
            return [];
        }

        if (!TryGetAtHomePayload(payloadJson, out var atHomePayload))
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

    public IReadOnlyList<SearchSuggestionItem> GetSearchSuggestions(
        string requestJson,
        Func<string, string?> fetchAbsoluteUrl)
    {
        ArgumentNullException.ThrowIfNull(fetchAbsoluteUrl);

        var request = ParseSearchSuggestionRequest(requestJson);
        return request is null
            ? []
            : GetSearchSuggestions(request, fetchAbsoluteUrl);
    }

    public IReadOnlyList<SearchSuggestionItem> GetSearchSuggestions(
        SearchSuggestionRequest request,
        Func<string, string?> fetchAbsoluteUrl)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(fetchAbsoluteUrl);

        return ProviderSearchQueryResolver.Instance.GetSuggestions(request, fetchAbsoluteUrl);
    }

    public Task<IReadOnlyList<SearchSuggestionItem>> GetSearchSuggestionsAsync(
        SearchSuggestionRequest request,
        Func<string, CancellationToken, Task<string?>> fetchAbsoluteUrlAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(fetchAbsoluteUrlAsync);

        return ProviderSearchQueryResolver.Instance.GetSuggestionsAsync(request, fetchAbsoluteUrlAsync, cancellationToken);
    }

    public IReadOnlyDictionary<string, List<MetadataItem>> FetchStatisticsMetadata(
        IEnumerable<string> mangaIds,
        Func<string, string?> fetchAbsoluteUrl)
    {
        ArgumentNullException.ThrowIfNull(mangaIds);
        ArgumentNullException.ThrowIfNull(fetchAbsoluteUrl);

        var loader = new PluginBatchMetadataLoader<MetadataItem>(
            new PluginBatchMetadataLoaderOptions(
                BatchSize: StatisticsBatchSize,
                DelayBetweenBatches: TimeSpan.Zero,
                DelayBetweenRequests: TimeSpan.Zero));

        return loader.Load(
            mangaIds,
            batch => FetchStatisticsBatch(batch, fetchAbsoluteUrl),
            mangaId => FetchSingleStatistic(mangaId, fetchAbsoluteUrl));
    }

    public async Task<IReadOnlyDictionary<string, List<MetadataItem>>> FetchStatisticsMetadataAsync(
        IEnumerable<string> mangaIds,
        Func<string, CancellationToken, Task<string?>> fetchAbsoluteUrlAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mangaIds);
        ArgumentNullException.ThrowIfNull(fetchAbsoluteUrlAsync);

        var loader = new PluginBatchMetadataLoader<MetadataItem>(
            new PluginBatchMetadataLoaderOptions(
                BatchSize: StatisticsBatchSize,
                DelayBetweenBatches: TimeSpan.Zero,
                DelayBetweenRequests: TimeSpan.Zero));

        return await loader.LoadAsync(
            mangaIds,
            (batch, ct) => FetchStatisticsBatchAsync(batch, fetchAbsoluteUrlAsync, ct),
            (mangaId, ct) => FetchSingleStatisticAsync(mangaId, fetchAbsoluteUrlAsync, ct),
            cancellationToken);
    }

    public static string ResolvePayloadContent(string payload)
    {
        return PluginJsonPayload.Normalize(payload);
    }

    private static SearchEnrichmentRequest ParseSearchEnrichmentRequest(string enrichmentArgsJson)
    {
        List<string> itemIds = [];
        IReadOnlyList<SearchItem>? baseItems = null;

        if (!string.IsNullOrWhiteSpace(enrichmentArgsJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(enrichmentArgsJson);
                var root = doc.RootElement;

                if (root.TryGetProperty("itemIds", out var idsElement) && idsElement.ValueKind == JsonValueKind.Array)
                {
                    itemIds = [.. idsElement.EnumerateArray()
                        .Select(static el => el.GetString() ?? string.Empty)
                        .Where(static id => !string.IsNullOrWhiteSpace(id))
                        .Distinct(StringComparer.OrdinalIgnoreCase)];
                }

                if (root.TryGetProperty("baseItems", out var baseItemsElement) && baseItemsElement.ValueKind == JsonValueKind.Array)
                {
                    var baseItemsList = new List<SearchItem>();
                    foreach (var item in baseItemsElement.EnumerateArray())
                    {
                        var id = PluginJsonElement.GetString(item, "id");
                        if (string.IsNullOrWhiteSpace(id))
                        {
                            continue;
                        }

                        var source = PluginJsonElement.GetString(item, "source");
                        var title = PluginJsonElement.GetString(item, "title");
                        var mediaType = PluginJsonElement.GetString(item, "mediaType");
                        var thumbnailUrl = PluginJsonElement.GetString(item, "thumbnailUrl");
                        var description = PluginJsonElement.GetString(item, "description");

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

                        baseItemsList.Add(new SearchItem(id, source ?? string.Empty, title ?? string.Empty, mediaType ?? string.Empty, thumbnailUrl, description, metadata));
                    }

                    baseItems = baseItemsList.Count > 0 ? baseItemsList : null;
                }
            }
            catch
            {
            }
        }

        return new SearchEnrichmentRequest(itemIds, baseItems);
    }

    private static IReadOnlyList<SearchItem> MergeEnrichedSearchItems(
        IReadOnlyList<string> itemIds,
        IReadOnlyList<SearchItem>? baseItems,
        IReadOnlyDictionary<string, List<MetadataItem>> statisticsById)
    {
        if (itemIds.Count == 0)
        {
            return [];
        }

        if (statisticsById.Count == 0)
        {
            return baseItems ?? [.. itemIds.Select(id => new SearchItem(id, string.Empty, string.Empty, string.Empty, null, null, null))];
        }

        var enriched = new List<SearchItem>(baseItems?.Count ?? itemIds.Count);
        if (baseItems is not null)
        {
            foreach (var item in baseItems)
            {
                var metadata = item.metadata is null
                    ? []
                    : new List<MetadataItem>(item.metadata);

                if (statisticsById.TryGetValue(item.id, out var statsItems) && statsItems.Count > 0)
                {
                    metadata.AddRange(statsItems);
                }

                enriched.Add(item with { metadata = metadata.Count > 0 ? metadata : item.metadata });
            }

            return enriched;
        }

        foreach (var id in itemIds)
        {
            var metadata = new List<MetadataItem>();
            if (statisticsById.TryGetValue(id, out var statsItems) && statsItems.Count > 0)
            {
                metadata.AddRange(statsItems);
            }

            enriched.Add(new SearchItem(id, string.Empty, string.Empty, string.Empty, null, null, metadata.Count > 0 ? metadata : null));
        }

        return enriched;
    }

    private static SearchSuggestionRequest? ParseSearchSuggestionRequest(string requestJson)
    {
        if (string.IsNullOrWhiteSpace(requestJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(requestJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var controlId = PluginJsonElement.GetString(root, "controlId")?.Trim();
            if (string.IsNullOrWhiteSpace(controlId))
            {
                return null;
            }

            var query = PluginJsonElement.GetString(root, "query")?.Trim() ?? string.Empty;
            var limit = root.TryGetProperty("limit", out var limitElement) && limitElement.ValueKind == JsonValueKind.Number
                ? limitElement.GetInt32()
                : (int?)null;

            PluginSearchQuery? searchQuery = null;
            if (root.TryGetProperty("searchQuery", out var searchQueryElement)
                && searchQueryElement.ValueKind == JsonValueKind.Object)
            {
                searchQuery = PluginSearchQuery.Parse(searchQueryElement.GetRawText());
            }

            return new SearchSuggestionRequest(controlId, query, searchQuery, limit);
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyDictionary<string, List<MetadataItem>> FetchStatisticsBatch(
        IReadOnlyList<string> mangaIds,
        Func<string, string?> fetchAbsoluteUrl)
    {
        var batchUrl = ProviderRequestUrls.BuildStatisticsAbsoluteUrl(mangaIds);
        if (string.IsNullOrWhiteSpace(batchUrl))
        {
            return EmptyMetadataMap;
        }

        var batchPayload = fetchAbsoluteUrl(batchUrl);
        return ExtractStatisticsMetadata(batchPayload, mangaIds);
    }

    private static async Task<IReadOnlyDictionary<string, List<MetadataItem>>> FetchStatisticsBatchAsync(
        IReadOnlyList<string> mangaIds,
        Func<string, CancellationToken, Task<string?>> fetchAbsoluteUrlAsync,
        CancellationToken cancellationToken)
    {
        var batchUrl = ProviderRequestUrls.BuildStatisticsAbsoluteUrl(mangaIds);
        if (string.IsNullOrWhiteSpace(batchUrl))
        {
            return EmptyMetadataMap;
        }

        var batchPayload = await fetchAbsoluteUrlAsync(batchUrl, cancellationToken);
        return ExtractStatisticsMetadata(batchPayload, mangaIds);
    }

    private static IReadOnlyList<MetadataItem> FetchSingleStatistic(
        string mangaId,
        Func<string, string?> fetchAbsoluteUrl)
    {
        var singleUrl = ProviderRequestUrls.BuildStatisticsAbsoluteUrl(mangaId);
        if (string.IsNullOrWhiteSpace(singleUrl))
        {
            return [];
        }

        var singlePayload = fetchAbsoluteUrl(singleUrl);
        return ExtractStatisticsForSingleId(singlePayload, mangaId);
    }

    private static async Task<IReadOnlyList<MetadataItem>> FetchSingleStatisticAsync(
        string mangaId,
        Func<string, CancellationToken, Task<string?>> fetchAbsoluteUrlAsync,
        CancellationToken cancellationToken)
    {
        var singleUrl = ProviderRequestUrls.BuildStatisticsAbsoluteUrl(mangaId);
        if (string.IsNullOrWhiteSpace(singleUrl))
        {
            return [];
        }

        var singlePayload = await fetchAbsoluteUrlAsync(singleUrl, cancellationToken);
        return ExtractStatisticsForSingleId(singlePayload, mangaId);
    }

    private static IReadOnlyDictionary<string, List<MetadataItem>> ExtractStatisticsMetadata(
        string? payloadJson,
        IReadOnlyList<string> requestedIds)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return EmptyMetadataMap;
        }

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var results = new Dictionary<string, List<MetadataItem>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (mangaId, items) in PayloadMapper.ExtractStatisticsMetadata(doc.RootElement))
            {
                if (items.Count > 0)
                {
                    results[mangaId] = [.. items];
                }
            }

            foreach (var mangaId in requestedIds)
            {
                if (results.ContainsKey(mangaId) || !TryExtractRatingMetadataForId(payloadJson, mangaId, out var ratingItem))
                {
                    continue;
                }

                results[mangaId] = [ratingItem];
            }

            return results;
        }
        catch
        {
            return EmptyMetadataMap;
        }
    }

    private static IReadOnlyList<MetadataItem> ExtractStatisticsForSingleId(string? payloadJson, string mangaId)
    {
        if (string.IsNullOrWhiteSpace(payloadJson) || string.IsNullOrWhiteSpace(mangaId))
        {
            return [];
        }

        var metadata = ExtractStatisticsMetadata(payloadJson, [mangaId]);
        return metadata.TryGetValue(mangaId, out var items) && items.Count > 0
            ? items
            : [];
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

    private static IReadOnlyList<MangadexChapterEntry> ParseChapterEntries(string payloadJson)
    {
        var normalizedPayload = ResolvePayloadContent(payloadJson);
        if (string.IsNullOrWhiteSpace(normalizedPayload))
        {
            return [];
        }

        using var doc = JsonDocument.Parse(normalizedPayload);
        return PayloadMapper.ParseChapterEntries(doc.RootElement);
    }

    private static bool TryGetAtHomePayload(string payloadJson, out MangadexAtHomePayload payload)
    {
        var normalizedPayload = ResolvePayloadContent(payloadJson);
        if (string.IsNullOrWhiteSpace(normalizedPayload))
        {
            payload = default;
            return false;
        }

        return PayloadMapper.TryParseAtHomePayload(normalizedPayload, out payload);
    }

    private readonly record struct SearchEnrichmentRequest(
        IReadOnlyList<string> ItemIds,
        IReadOnlyList<SearchItem>? BaseItems);
}

internal readonly record struct SearchParseMapResult(
    IReadOnlyList<SearchItem> Results,
    long ParseMs,
    long MapMs);