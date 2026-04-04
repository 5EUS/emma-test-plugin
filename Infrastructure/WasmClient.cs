#if PLUGIN_TRANSPORT_WASM
using System.Net.Http;
using System.Text.Json;
using EMMA.Plugin.Common;

namespace EMMA.TestPlugin.Infrastructure;

internal sealed class WasmClient
{
    private const int ChapterFeedPageSize = 500;
    private const int ChapterFeedMaxPages = 20;

    private static readonly CoreClient Core = new();
    private static readonly HttpClient Http = CreateHttpClient();

    public SearchParseMapResult SearchFromPayloadWithTimings(string payloadJson)
    {
        var parseMap = Core.SearchFromPayloadWithTimings(payloadJson);
        return new SearchParseMapResult(parseMap.Results, parseMap.ParseMs, parseMap.MapMs);
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