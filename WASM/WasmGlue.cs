using System.Text.Json.Serialization;
using EMMA.Plugin.Common;
using EMMA.TestPlugin.Core;
using LibraryWorld;
using LibraryWorld.wit.imports.emma.plugin;

namespace EMMA.TestPlugin.WASM
{
    internal sealed class WasmPluginOperationHost : PluginBasicPagedWasmOperationHost<WasmChapterOperationItem>
    {
        private static readonly PluginBasicPagedWasmHostOptions<WasmChapterOperationItem> HostOptions = new(
            HandshakeVersion: "1.0.0",
            HandshakeMessage: "EMMA wasm component ready",
            CapabilityProfile: PluginCapabilityProfile.PagedOnly,
            HandshakeTypeInfo: WasmJsonContext.Default.HandshakeResponse,
            CapabilityTypeInfo: WasmJsonContext.Default.CapabilityItemArray,
            SearchTypeInfo: WasmJsonContext.Default.SearchItemArray,
            ChapterTypeInfo: WasmJsonContext.Default.ChapterItemArray,
            ChapterInvokeTypeInfo: WasmJsonContext.Default.WasmChapterOperationItemArray,
            PageTypeInfo: WasmJsonContext.Default.PageItem,
            PageArrayTypeInfo: WasmJsonContext.Default.PageItemArray,
            OperationResultTypeInfo: WasmJsonContext.Default.OperationResult,
            BenchmarkTypeInfo: WasmJsonContext.Default.BenchmarkResult,
            NetworkBenchmarkTypeInfo: WasmJsonContext.Default.NetworkBenchmarkResult);

        private readonly WasmClient _client = new();

        public WasmPluginOperationHost()
            : base(HostOptions)
        {
        }

        protected override PluginOperationDispatcher ConfigureCustomInvokeHandlers(PluginOperationDispatcher dispatcher)
        {
            return dispatcher
                .Register("enrich-search-metadata", request =>
                {
                    var enriched = _client.EnrichSearchItemsWithStatistics(request.argsJson ?? string.Empty).ToArray();
                    return PluginWasmInvokeScaffold.BuildJsonResult(
                        enriched,
                        WasmJsonContext.Default.SearchItemArray);
                })
                .Register("search-suggestions", request =>
                {
                    var suggestions = _client.GetSearchSuggestions(request.argsJson ?? string.Empty).ToArray();
                    return PluginWasmInvokeScaffold.BuildJsonResult(
                        suggestions,
                        WasmJsonContext.Default.SearchSuggestionItemArray);
                });
        }

        protected override string? FetchSearchPayload(PluginSearchQuery parsedQuery) => _client.FetchSearchPayload(parsedQuery);

        protected override (IReadOnlyList<SearchItem> Results, long ParseMs, long MapMs) SearchFromPayloadWithTimings(string payloadJson)
        {
            var result = _client.SearchFromPayloadWithTimings(payloadJson);
            return (result.Results, result.ParseMs, result.MapMs);
        }

        protected override string? FetchChaptersPayload(string mediaId) => _client.FetchChaptersPayload(mediaId);

        protected override IReadOnlyList<ChapterItem> GetChaptersFromPayload(string mediaId, string payloadJson) =>
            _client.GetChaptersFromPayload(mediaId, payloadJson);

        protected override IReadOnlyList<ChapterOperationItem> GetChapterOperationItemsFromPayload(string mediaId, string payloadJson) =>
            _client.GetChapterOperationItemsFromPayload(mediaId, payloadJson);

        protected override WasmChapterOperationItem MapChapterOperationItem(ChapterOperationItem item) =>
            new(
                item.id,
                item.number,
                item.title,
                [.. item.uploaderGroups ?? []]);

        protected override string? FetchAtHomePayload(string chapterId) => _client.FetchAtHomePayload(chapterId);

        protected override PageItem? GetPageFromPayload(string chapterId, int pageIndex, string payloadJson) =>
            _client.GetPageFromPayload(chapterId, pageIndex, payloadJson);

        protected override IReadOnlyList<PageItem> GetPagesFromPayload(string chapterId, int startIndex, int count, string payloadJson) =>
            _client.GetPagesFromPayload(chapterId, startIndex, count, payloadJson);
    }

    [JsonSerializable(typeof(string[]))]
    [JsonSerializable(typeof(HandshakeResponse))]
    [JsonSerializable(typeof(CapabilityItem[]))]
    [JsonSerializable(typeof(MetadataItem))]
    [JsonSerializable(typeof(IReadOnlyList<MetadataItem>))]
    [JsonSerializable(typeof(List<MetadataItem>))]
    [JsonSerializable(typeof(SearchSuggestionRequest))]
    [JsonSerializable(typeof(SearchSuggestionItem))]
    [JsonSerializable(typeof(SearchSuggestionItem[]))]
    [JsonSerializable(typeof(IReadOnlyList<SearchSuggestionItem>))]
    [JsonSerializable(typeof(List<SearchSuggestionItem>))]
    [JsonSerializable(typeof(SearchItem[]))]
    [JsonSerializable(typeof(ChapterItem[]))]
    [JsonSerializable(typeof(WasmChapterOperationItem[]))]
    [JsonSerializable(typeof(PageItem))]
    [JsonSerializable(typeof(PageItem[]))]
    [JsonSerializable(typeof(OperationResult))]
    [JsonSerializable(typeof(BenchmarkResult))]
    [JsonSerializable(typeof(NetworkBenchmarkResult))]
    internal sealed partial class WasmJsonContext : JsonSerializerContext
    {
    }

    internal sealed record WasmChapterOperationItem(
        string id,
        int number,
        string title,
        string[] uploaderGroups);
}

namespace LibraryWorld.wit.exports.emma.plugin
{
    using global::EMMA.Plugin.Common;
    using global::EMMA.TestPlugin.Core;
    using global::LibraryWorld.wit.imports.emma.plugin;

    public static partial class PluginImpl
    {
        private const int ChapterFeedPageSize = 500;
        private const int ChapterFeedMaxPages = 20;

        private static readonly PluginOperationPayloadRouter InvokePayloadRouter = BuildInvokePayloadRouter();

        private static string ResolveSearchPayload(PluginSearchQuery query, string? payloadJson)
        {
            return PluginWasmHostBridgeScaffold.ResolveSearchPayload(
                payloadJson,
                query,
                ProviderSearchQueryResolver.Instance.Resolve,
                ProviderRequestUrls.BuildSearchAbsoluteUrl,
                HostBridgeInterop.OperationPayload);
        }

        private static string ResolveChaptersPayload(string mediaId, string? payloadJson)
        {
            return PluginWasmHostBridgeScaffold.ResolveMergedChapterFeedPayload(
                mediaId,
                payloadJson,
                ChapterFeedPageSize,
                ChapterFeedMaxPages,
                ProviderRequestUrls.BuildChaptersAbsoluteUrl,
                HostBridgeInterop.OperationPayload);
        }

        private static string ResolvePagePayload(string chapterId, string? payloadJson)
        {
            return PluginPayloadResolvers.ResolveProvidedOrHostPayload(
                payloadJson,
                "page",
                ProviderRequestUrls.BuildAtHomeAbsoluteUrl(chapterId),
                HostBridgeInterop.OperationPayload);
        }

        private static string ResolvePagesPayload(string chapterId, string? payloadJson)
        {
            return PluginPayloadResolvers.ResolveProvidedOrHostPayload(
                payloadJson,
                "pages",
                ProviderRequestUrls.BuildAtHomeAbsoluteUrl(chapterId),
                HostBridgeInterop.OperationPayload);
        }

        private static string? ResolveInvokePayload(IPlugin.MediaOperationRequest request)
        {
            if (string.Equals(request.operation, "enrich-search-metadata", StringComparison.OrdinalIgnoreCase))
            {
                return request.argsJson;
            }

            return PluginTypedExportScaffold.ResolveInvokePayload(
                request.operation,
                request.mediaId,
                request.mediaType,
                request.argsJson,
                request.payloadJson,
                InvokePayloadRouter,
                HostBridgeInterop.OperationPayload,
                useArgsJsonFallbackHint: true);
        }

        public static List<IPlugin.VideoStreamItem> VideoStreams(string mediaId, string payloadJson)
        {
            return [];
        }

        public static IPlugin.VideoSegmentItem? VideoSegment(string mediaId, string streamId, uint sequence, string payloadJson)
        {
            return null;
        }

        private static PluginOperationPayloadRouter BuildInvokePayloadRouter()
        {
            return new PluginOperationPayloadRouter()
                .RegisterStandardPagedMediaHints(
                    parsed => PluginSearchUrlResolver.ResolveSearchAbsoluteUrl(
                        parsed,
                        ProviderSearchQueryResolver.Instance.Resolve,
                        ProviderRequestUrls.BuildSearchAbsoluteUrl,
                        HostBridgeInterop.OperationPayload),
                    ProviderRequestUrls.BuildChaptersAbsoluteUrl,
                    ProviderRequestUrls.BuildAtHomeAbsoluteUrl)
                .Register("enrich-search-metadata", _ => null);
        }
    }
}