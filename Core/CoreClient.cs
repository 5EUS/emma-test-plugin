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
    private static readonly MangadexProviderClient ProviderClient = MangadexProviderClient.Instance;
    private static readonly PluginDeferredSearchMetadataEnricher SearchMetadataEnricher = new();

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

        return EnrichSearchItemsWithStatistics(
            enrichmentArgsJson,
            PluginPayloadSource.FromSync(fetchAbsoluteUrl));
    }

    public IReadOnlyList<SearchItem> EnrichSearchItemsWithStatistics(
        string enrichmentArgsJson,
        PluginPayloadSource payloadSource)
    {
        ArgumentNullException.ThrowIfNull(payloadSource);

        return SearchMetadataEnricher.Enrich(
            enrichmentArgsJson,
            itemIds => FetchStatisticsMetadata(itemIds, payloadSource));
    }

    public async Task<IReadOnlyList<SearchItem>> EnrichSearchItemsAsync(
        IReadOnlyList<SearchItem> items,
        Func<string, CancellationToken, Task<string?>> fetchAbsoluteUrlAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(fetchAbsoluteUrlAsync);

        return await EnrichSearchItemsAsync(
            items,
            PluginPayloadSource.FromAsync(fetchAbsoluteUrlAsync),
            cancellationToken);
    }

    public async Task<IReadOnlyList<SearchItem>> EnrichSearchItemsAsync(
        IReadOnlyList<SearchItem> items,
        PluginPayloadSource payloadSource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(payloadSource);

        if (items.Count == 0)
        {
            return items;
        }

        return await SearchMetadataEnricher.EnrichAsync(
            items,
            (itemIds, ct) => FetchStatisticsMetadataAsync(itemIds, payloadSource, ct),
            cancellationToken);
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

        return GetSearchSuggestions(requestJson, PluginPayloadSource.FromSync(fetchAbsoluteUrl));
    }

    public IReadOnlyList<SearchSuggestionItem> GetSearchSuggestions(
        string requestJson,
        PluginPayloadSource payloadSource)
    {
        ArgumentNullException.ThrowIfNull(payloadSource);

        var request = ParseSearchSuggestionRequest(requestJson);
        return request is null
            ? []
            : MangadexPluginBundle.Instance.SuggestionProvider.GetSuggestions(request, payloadSource);
    }

    public IReadOnlyList<SearchSuggestionItem> GetSearchSuggestions(
        SearchSuggestionRequest request,
        Func<string, string?> fetchAbsoluteUrl)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(fetchAbsoluteUrl);

        return MangadexPluginBundle.Instance.SuggestionProvider.GetSuggestions(
            request,
            PluginPayloadSource.FromSync(fetchAbsoluteUrl));
    }

    public Task<IReadOnlyList<SearchSuggestionItem>> GetSearchSuggestionsAsync(
        SearchSuggestionRequest request,
        Func<string, CancellationToken, Task<string?>> fetchAbsoluteUrlAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(fetchAbsoluteUrlAsync);

        return GetSearchSuggestionsAsync(
            request,
            PluginPayloadSource.FromAsync(fetchAbsoluteUrlAsync),
            cancellationToken);
    }

    public Task<IReadOnlyList<SearchSuggestionItem>> GetSearchSuggestionsAsync(
        SearchSuggestionRequest request,
        PluginPayloadSource payloadSource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(payloadSource);

        return MangadexPluginBundle.Instance.SuggestionProvider.GetSuggestionsAsync(request, payloadSource, cancellationToken);
    }

    public IReadOnlyDictionary<string, List<MetadataItem>> FetchStatisticsMetadata(
        IEnumerable<string> mangaIds,
        Func<string, string?> fetchAbsoluteUrl)
    {
        ArgumentNullException.ThrowIfNull(mangaIds);
        ArgumentNullException.ThrowIfNull(fetchAbsoluteUrl);

        return FetchStatisticsMetadata(mangaIds, PluginPayloadSource.FromSync(fetchAbsoluteUrl));
    }

    public IReadOnlyDictionary<string, List<MetadataItem>> FetchStatisticsMetadata(
        IEnumerable<string> mangaIds,
        PluginPayloadSource payloadSource)
    {
        ArgumentNullException.ThrowIfNull(mangaIds);
        ArgumentNullException.ThrowIfNull(payloadSource);

        var loader = new PluginBatchMetadataLoader<MetadataItem>(
            new PluginBatchMetadataLoaderOptions(
                BatchSize: StatisticsBatchSize,
                DelayBetweenBatches: TimeSpan.Zero,
                DelayBetweenRequests: TimeSpan.Zero));

        return loader.Load(
            mangaIds,
            batch => FetchStatisticsBatch(batch, payloadSource),
            mangaId => FetchSingleStatistic(mangaId, payloadSource));
    }

    public async Task<IReadOnlyDictionary<string, List<MetadataItem>>> FetchStatisticsMetadataAsync(
        IEnumerable<string> mangaIds,
        Func<string, CancellationToken, Task<string?>> fetchAbsoluteUrlAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mangaIds);
        ArgumentNullException.ThrowIfNull(fetchAbsoluteUrlAsync);

        return await FetchStatisticsMetadataAsync(
            mangaIds,
            PluginPayloadSource.FromAsync(fetchAbsoluteUrlAsync),
            cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, List<MetadataItem>>> FetchStatisticsMetadataAsync(
        IEnumerable<string> mangaIds,
        PluginPayloadSource payloadSource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mangaIds);
        ArgumentNullException.ThrowIfNull(payloadSource);

        var loader = new PluginBatchMetadataLoader<MetadataItem>(
            new PluginBatchMetadataLoaderOptions(
                BatchSize: StatisticsBatchSize,
                DelayBetweenBatches: TimeSpan.Zero,
                DelayBetweenRequests: TimeSpan.Zero));

        return await loader.LoadAsync(
            mangaIds,
            (batch, ct) => FetchStatisticsBatchAsync(batch, payloadSource, ct),
            (mangaId, ct) => FetchSingleStatisticAsync(mangaId, payloadSource, ct),
            cancellationToken);
    }

    public static string ResolvePayloadContent(string payload)
    {
        return PluginJsonPayload.Normalize(payload);
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
        PluginPayloadSource payloadSource)
    {
        var batchUrl = ProviderClient.BuildStatisticsAbsoluteUrl(mangaIds);
        if (string.IsNullOrWhiteSpace(batchUrl))
        {
            return EmptyMetadataMap;
        }

        var batchPayload = payloadSource.Fetch(batchUrl);
        return ExtractStatisticsMetadata(batchPayload, mangaIds);
    }

    private static async Task<IReadOnlyDictionary<string, List<MetadataItem>>> FetchStatisticsBatchAsync(
        IReadOnlyList<string> mangaIds,
        PluginPayloadSource payloadSource,
        CancellationToken cancellationToken)
    {
        var batchUrl = ProviderClient.BuildStatisticsAbsoluteUrl(mangaIds);
        if (string.IsNullOrWhiteSpace(batchUrl))
        {
            return EmptyMetadataMap;
        }

        var batchPayload = await payloadSource.FetchAsync(batchUrl, cancellationToken);
        return ExtractStatisticsMetadata(batchPayload, mangaIds);
    }

    private static IReadOnlyList<MetadataItem> FetchSingleStatistic(
        string mangaId,
        PluginPayloadSource payloadSource)
    {
        var singleUrl = ProviderClient.BuildStatisticsAbsoluteUrl(mangaId);
        if (string.IsNullOrWhiteSpace(singleUrl))
        {
            return [];
        }

        var singlePayload = payloadSource.Fetch(singleUrl);
        return ExtractStatisticsForSingleId(singlePayload, mangaId);
    }

    private static async Task<IReadOnlyList<MetadataItem>> FetchSingleStatisticAsync(
        string mangaId,
        PluginPayloadSource payloadSource,
        CancellationToken cancellationToken)
    {
        var singleUrl = ProviderClient.BuildStatisticsAbsoluteUrl(mangaId);
        if (string.IsNullOrWhiteSpace(singleUrl))
        {
            return [];
        }

        var singlePayload = await payloadSource.FetchAsync(singleUrl, cancellationToken);
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
}

internal readonly record struct SearchParseMapResult(
    IReadOnlyList<SearchItem> Results,
    long ParseMs,
    long MapMs);