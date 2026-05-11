using System.Text.Json;
using EMMA.Plugin.Common;
using EMMA.TestPlugin.Core;
using LibraryWorld.wit.imports.emma.plugin;

namespace EMMA.TestPlugin.WASM;

internal sealed class WasmClient
{
    #region Constants and Dependencies

    private const int ChapterFeedPageSize = 500;
    private const int ChapterFeedMaxPages = 20;
    private const int StatisticsBatchSize = 150;

    private static readonly CoreClient Core = new();

    #endregion

    #region Public API

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
                    ids = [.. idsElement.EnumerateArray()
                        .Select(el => el.GetString() ?? "")
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .Distinct(StringComparer.OrdinalIgnoreCase)];
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
            return baseItems ?? [.. ids.Select(id => new SearchItem(id, "", "", "", null, null, null))];
        }

        var enriched = new List<SearchItem>();

        if (baseItems != null)
        {
            // Preserve original item data and merge statistics
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
            Core.GetChaptersFromPayload);
    }

    public IReadOnlyList<ChapterOperationItem> GetChapterOperationItemsFromPayload(string mediaId, string payloadJson)
    {
        return MapAllChapterPages(
            mediaId,
            payloadJson,
            Core.GetChapterOperationItemsFromPayload);
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
        var searchAbsoluteUrl = PluginSearchUrlResolver.ResolveSearchAbsoluteUrl(
            query,
            ProviderSearchQueryResolver.Instance.Resolve,
            ProviderRequestUrls.BuildSearchAbsoluteUrl,
            (_, payloadHint) => TryFetchPayload(payloadHint));

        return TryFetchPayload(searchAbsoluteUrl);
    }

    public string? FetchChaptersPayload(string mediaId)
    {
        return TryFetchPayload(ProviderRequestUrls.BuildChaptersAbsoluteUrl(mediaId));
    }

    public string? FetchAtHomePayload(string chapterId)
    {
        return TryFetchPayload(ProviderRequestUrls.BuildAtHomeAbsoluteUrl(chapterId));
    }

    public IReadOnlyList<SearchSuggestionItem> GetSearchSuggestions(string requestJson)
    {
        if (string.IsNullOrWhiteSpace(requestJson))
        {
            return [];
        }

        try
        {
            var request = JsonSerializer.Deserialize(requestJson, WasmJsonContext.Default.SearchSuggestionRequest);
            if (request is null)
            {
                return [];
            }

            return ProviderSearchQueryResolver.Instance.GetSuggestions(
                request,
                static absoluteUrl => TryFetchPayload(absoluteUrl));
        }
        catch
        {
            return [];
        }
    }

    #endregion

    #region Statistics Metadata Helpers

    private IReadOnlyDictionary<string, List<MetadataItem>> FetchStatisticsMetadata(IEnumerable<string> mangaIds)
    {
        var loader = new PluginBatchMetadataLoader<MetadataItem>(
            new PluginBatchMetadataLoaderOptions(
                BatchSize: StatisticsBatchSize,
                DelayBetweenBatches: TimeSpan.Zero,
                DelayBetweenRequests: TimeSpan.Zero));

        return loader.Load(
            mangaIds,
            batch => FetchStatisticsBatchSync(batch),
            mangaId => FetchSingleStatisticSync(mangaId));
    }

    private static IReadOnlyDictionary<string, List<MetadataItem>> FetchStatisticsBatchSync(
        IReadOnlyList<string> mangaIds)
    {
        var results = new Dictionary<string, List<MetadataItem>>(StringComparer.OrdinalIgnoreCase);
        var batchUrl = ProviderRequestUrls.BuildStatisticsAbsoluteUrl(mangaIds);
        if (string.IsNullOrWhiteSpace(batchUrl))
        {
            return results;
        }

        var batchPayload = TryFetchPayload(batchUrl);
        if (string.IsNullOrWhiteSpace(batchPayload))
        {
            return results;
        }

        try
        {
            using var doc = JsonDocument.Parse(batchPayload);
            foreach (var (mangaId, items) in PayloadMapper.ExtractStatisticsMetadata(doc.RootElement))
            {
                if (items.Count > 0)
                {
                    results[mangaId] = [.. items];
                }
            }
        }
        catch
        {
            // Batch loader will automatically fallback to per-item fetch.
        }

        return results;
    }

    private static IReadOnlyList<MetadataItem> FetchSingleStatisticSync(string mangaId)
    {
        var singleUrl = ProviderRequestUrls.BuildStatisticsAbsoluteUrl(mangaId);
        if (string.IsNullOrWhiteSpace(singleUrl))
        {
            return [];
        }

        var singlePayload = TryFetchPayload(singleUrl);
        if (string.IsNullOrWhiteSpace(singlePayload))
        {
            return [];
        }

        if (TryExtractRatingMetadataForId(singlePayload, mangaId, out var ratingItem))
        {
            return [ratingItem];
        }

        return [];
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

    #endregion

    #region Pagination Helpers

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

        var mergedPayload = PluginWasmPagingJsonHelpers.MergeChapterFeedPages(
            firstNormalizedPayload,
            ChapterFeedMaxPages,
            nextOffset =>
            {
                if (string.IsNullOrWhiteSpace(mediaId))
                {
                    return null;
                }

                return TryFetchPayload(
                    ProviderRequestUrls.BuildChaptersAbsoluteUrl(
                        mediaId,
                        limit: ChapterFeedPageSize,
                        offset: nextOffset));
            });

        return mapper(mergedPayload);
    }

    #endregion

    #region Host Bridge Payload Fetch

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
            var payload = HostBridgeInterop.OperationPayload("search", absoluteUrl) ?? string.Empty;
            return ResolvePayloadContent(payload);
        }
        catch
        {
            return null;
        }
    }

    #endregion

    #region Result Models

    public readonly record struct SearchParseMapResult(
        IReadOnlyList<SearchItem> Results,
        long ParseMs,
        long MapMs);

    #endregion
}
