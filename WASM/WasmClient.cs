using System.Text.Json;
using EMMA.Plugin.Common;
using EMMA.TestPlugin.Core;
using LibraryWorld.wit.imports.emma.plugin;

namespace EMMA.TestPlugin.WASM;

internal sealed class WasmClient
{
    private const int ChapterFeedPageSize = 500;
    private const int ChapterFeedMaxPages = 20;

    private static readonly PluginProviderBundle<MangadexProviderClient, ProviderSearchQueryResolver, MangadexSearchSuggestionProvider> Provider = MangadexPluginBundle.Instance;
    private static readonly CoreClient Core = new();
    private static readonly PluginPayloadSource PayloadSource = PluginPayloadSource.FromSync(TryFetchPayload);

    public SearchParseMapResult SearchFromPayloadWithTimings(string payloadJson)
    {
        var parseMap = Core.SearchFromPayloadWithTimings(payloadJson);
        return new SearchParseMapResult(parseMap.Results ?? [], parseMap.ParseMs, parseMap.MapMs);
    }

    public IReadOnlyList<SearchItem> EnrichSearchItemsWithStatistics(string enrichmentArgsJson)
    {
        return Core.EnrichSearchItemsWithStatistics(enrichmentArgsJson, PayloadSource);
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
        Func<PluginSearchQuery, PluginPayloadSource, PluginSearchQuery> queryResolver =
            (parsedQuery, payloadSource) => Provider.QueryEnricher.Resolve(parsedQuery, payloadSource);

        return PluginWasmHostBridgeScaffold.ResolveSearchPayload(
            null,
            query,
            queryResolver,
            resolvedQuery => Provider.Client.BuildSearchAbsoluteUrl(resolvedQuery),
            HostBridgeInterop.OperationPayload);
    }

    public string? FetchChaptersPayload(string mediaId)
    {
        return PayloadSource.Fetch(Provider.Client.BuildChaptersAbsoluteUrl(mediaId));
    }

    public string? FetchAtHomePayload(string chapterId)
    {
        return PayloadSource.Fetch(Provider.Client.BuildAtHomeAbsoluteUrl(chapterId));
    }

    public IReadOnlyList<SearchSuggestionItem> GetSearchSuggestions(string requestJson)
    {
        return Core.GetSearchSuggestions(requestJson, PayloadSource);
    }

    internal static string ResolvePayloadContent(string payload)
    {
        return PluginPayload.NormalizePayload(payload);
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

        var mergedPayload = PluginWasmHostBridgeScaffold.ResolveMergedChapterFeedPayload(
            mediaId,
            firstNormalizedPayload,
            ChapterFeedPageSize,
            ChapterFeedMaxPages,
            Provider.Client.BuildChaptersAbsoluteUrl,
            HostBridgeInterop.OperationPayload);

        return mapper(mergedPayload);
    }

    private static string? TryFetchPayload(string? absoluteUrl)
    {
        return PluginWasmHostBridgeScaffold.FetchPayload(
            absoluteUrl,
            HostBridgeInterop.OperationPayload);
    }

    public readonly record struct SearchParseMapResult(
        IReadOnlyList<SearchItem> Results,
        long ParseMs,
        long MapMs);
}
