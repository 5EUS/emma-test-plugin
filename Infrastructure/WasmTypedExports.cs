#if PLUGIN_TRANSPORT_WASM
using System.Text.Json;
using EMMA.Plugin.Common;
using EMMA.TestPlugin.Infrastructure;
using LibraryWorld;
using LibraryWorld.wit.exports.emma.plugin;
using LibraryWorld.wit.imports.emma.plugin;

namespace LibraryWorld.wit.exports.emma.plugin;

public static class PluginImpl
{
    private const int ChapterFeedPageSize = 500;
    private const int ChapterFeedMaxPages = 20;

    private static readonly PluginOperationPayloadRouter InvokePayloadRouter = BuildInvokePayloadRouter();

    public static IPlugin.HandshakeResponse Handshake()
    {
        var handshake = EMMA.TestPlugin.Program.handshake();
        return new IPlugin.HandshakeResponse(handshake.version, handshake.message);
    }

    public static List<IPlugin.Capability> Capabilities()
    {
        var capabilities = EMMA.TestPlugin.Program.capabilities();
        return PluginTypedExportScaffold.MapList(
            capabilities,
            capability => new IPlugin.Capability(
                capability.name,
                [.. capability.mediaTypes],
                [.. capability.operations]));
    }

    public static List<IPlugin.MediaSearchItem> Search(string query, string payloadJson)
    {
        var parsedQuery = PluginSearchQuery.Parse(query, fallbackQuery: query);
        var resolvedSearchAbsoluteUrl = ResolveSearchAbsoluteUrl(parsedQuery);

        payloadJson = PluginPayloadResolvers.ResolveProvidedOrHostPayload(
            payloadJson,
            "search",
            resolvedSearchAbsoluteUrl,
            (operation, hint) => HostBridgeInterop.OperationPayload(operation, hint));
        var items = EMMA.TestPlugin.Program.search(query, payloadJson);

        return PluginTypedExportScaffold.MapList(
            items,
            item => new IPlugin.MediaSearchItem(
                item.id,
                item.source,
                item.title,
                item.mediaType,
                item.thumbnailUrl,
                item.description,
                []));
    }

    public static List<IPlugin.ChapterItem> Chapters(string mediaId, string payloadJson)
    {
        payloadJson = ResolveChaptersPayloadWithPagination(mediaId, payloadJson);
        var items = EMMA.TestPlugin.Program.chapters(mediaId, payloadJson);

        return PluginTypedExportScaffold.MapList(
            items,
            item => new IPlugin.ChapterItem(
                item.id,
                checked((uint)item.number),
                item.title,
                [.. item.uploaderGroups ?? []]));
    }

    public static IPlugin.PageItem? Page(string mediaId, string chapterId, uint pageIndex, string payloadJson)
    {
        payloadJson = PluginPayloadResolvers.ResolveProvidedOrHostPayload(
            payloadJson,
            "page",
            ProviderRequestUrls.BuildAtHomeAbsoluteUrl(chapterId),
            (operation, hint) => HostBridgeInterop.OperationPayload(operation, hint));

        var page = EMMA.TestPlugin.Program.page(mediaId, chapterId, pageIndex, payloadJson);
        return PluginTypedExportScaffold.MapNullable(
            page,
            value => new IPlugin.PageItem(value.id, checked((uint)value.index), value.contentUri));
    }

    public static List<IPlugin.PageItem> Pages(string mediaId, string chapterId, uint startIndex, uint count, string payloadJson)
    {
        payloadJson = PluginPayloadResolvers.ResolveProvidedOrHostPayload(
            payloadJson,
            "pages",
            ProviderRequestUrls.BuildAtHomeAbsoluteUrl(chapterId),
            (operation, hint) => HostBridgeInterop.OperationPayload(operation, hint));

        var pages = EMMA.TestPlugin.Program.pages(mediaId, chapterId, startIndex, count, payloadJson);
        return PluginTypedExportScaffold.MapList(
            pages,
            page => new IPlugin.PageItem(page.id, checked((uint)page.index), page.contentUri));
    }

    public static IPlugin.MediaOperationResponse Invoke(IPlugin.MediaOperationRequest request)
    {
        var payload = ResolveInvokePayload(request);

        var result = EMMA.TestPlugin.Program.invoke(new OperationRequest(
            request.operation,
            request.mediaId,
            request.mediaType,
            request.argsJson,
            payload));

        if (result.isError)
        {
            throw CreateOperationError(result.error);
        }

        return new IPlugin.MediaOperationResponse(result.contentType, result.payloadJson);
    }

    private static string? ResolveInvokePayload(IPlugin.MediaOperationRequest request)
    {
        return PluginTypedExportScaffold.ResolveInvokePayload(
            request.operation,
            request.mediaId,
            request.mediaType,
            request.argsJson,
            request.payloadJson,
            InvokePayloadRouter,
            (operation, hint) => HostBridgeInterop.OperationPayload(operation, hint),
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

    private static WitException<IPlugin.OperationError> CreateOperationError(string? error)
    {
        var parsed = PluginTypedExportScaffold.ResolveOperationError(error);

        return parsed.Kind switch
        {
            PluginOperationErrorKind.UnsupportedOperation => new WitException<IPlugin.OperationError>(
                IPlugin.OperationError.UnsupportedOperation(parsed.Message),
                0),
            PluginOperationErrorKind.InvalidArguments => new WitException<IPlugin.OperationError>(
                IPlugin.OperationError.InvalidArguments(parsed.Message),
                0),
            _ => new WitException<IPlugin.OperationError>(
                IPlugin.OperationError.Failed(parsed.Message),
                0)
        };
    }

    private static PluginOperationPayloadRouter BuildInvokePayloadRouter()
    {
        return new PluginOperationPayloadRouter()
            .Register("search", request =>
            {
                var parsed = PluginSearchQuery.Parse(request.argsJson);
                return ResolveSearchAbsoluteUrl(parsed);
            })
            .Register("benchmark-network", request =>
            {
                var parsed = PluginSearchQuery.Parse(request.argsJson);
                return ResolveSearchAbsoluteUrl(parsed);
            })
            .Register("chapters", request => ProviderRequestUrls.BuildChaptersAbsoluteUrl(request.ResolveMediaId()))
            .Register("page", request => ProviderRequestUrls.BuildAtHomeAbsoluteUrl(request.ResolveChapterId()))
            .Register("pages", request => ProviderRequestUrls.BuildAtHomeAbsoluteUrl(request.ResolveChapterId()));
    }

    private static string? ResolveSearchAbsoluteUrl(PluginSearchQuery parsedQuery)
    {
        var resolvedQuery = ProviderSearchQueryResolver.Resolve(
            parsedQuery,
            absoluteUrl => HostBridgeInterop.OperationPayload("search", absoluteUrl));
        return ProviderRequestUrls.BuildSearchAbsoluteUrl(resolvedQuery);
    }

    private static string ResolveChaptersPayloadWithPagination(string mediaId, string payloadJson)
    {
        var firstPayload = PluginPayloadResolvers.ResolveProvidedOrHostPayload(
            payloadJson,
            "chapters",
            ProviderRequestUrls.BuildChaptersAbsoluteUrl(mediaId, ChapterFeedPageSize, 0),
            (operation, hint) => HostBridgeInterop.OperationPayload(operation, hint));

        if (string.IsNullOrWhiteSpace(firstPayload))
        {
            return string.Empty;
        }

        if (!TryGetChapterFeedPageStats(firstPayload, out var firstStats))
        {
            return firstPayload;
        }

        var dataEntries = new List<string>();
        var includedEntries = new List<string>();
        var seenChapterIds = new HashSet<string>(StringComparer.Ordinal);
        var seenIncludedKeys = new HashSet<string>(StringComparer.Ordinal);

        AppendChapterFeedPage(firstPayload, dataEntries, includedEntries, seenChapterIds, seenIncludedKeys);

        var pagesFetched = 1;
        var nextOffset = firstStats.Offset + firstStats.DataCount;

        while (pagesFetched < ChapterFeedMaxPages && nextOffset < firstStats.Total)
        {
            var nextPayload = HostBridgeInterop.OperationPayload(
                "chapters",
                ProviderRequestUrls.BuildChaptersAbsoluteUrl(mediaId, ChapterFeedPageSize, nextOffset));

            if (string.IsNullOrWhiteSpace(nextPayload))
            {
                break;
            }

            AppendChapterFeedPage(nextPayload, dataEntries, includedEntries, seenChapterIds, seenIncludedKeys);

            if (!TryGetChapterFeedPageStats(nextPayload, out var nextStats) || nextStats.DataCount <= 0)
            {
                break;
            }

            nextOffset = nextStats.Offset + nextStats.DataCount;
            pagesFetched++;
        }

        return BuildMergedChapterPayload(dataEntries, includedEntries);
    }

    private static void AppendChapterFeedPage(
        string payloadJson,
        List<string> dataEntries,
        List<string> includedEntries,
        HashSet<string> seenChapterIds,
        HashSet<string> seenIncludedKeys)
    {
        var normalized = PluginJsonPayload.Normalize(payloadJson);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        using var doc = JsonDocument.Parse(normalized);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var chapterId = item.TryGetProperty("id", out var idElement)
                    ? idElement.GetString() ?? string.Empty
                    : string.Empty;

                if (string.IsNullOrWhiteSpace(chapterId) || !seenChapterIds.Add(chapterId))
                {
                    continue;
                }

                dataEntries.Add(item.GetRawText());
            }
        }

        if (root.TryGetProperty("included", out var included) && included.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in included.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var type = item.TryGetProperty("type", out var typeElement)
                    ? typeElement.GetString() ?? string.Empty
                    : string.Empty;
                var id = item.TryGetProperty("id", out var idElement)
                    ? idElement.GetString() ?? string.Empty
                    : string.Empty;
                var key = string.IsNullOrWhiteSpace(type) && string.IsNullOrWhiteSpace(id)
                    ? item.GetRawText()
                    : $"{type}:{id}";

                if (!seenIncludedKeys.Add(key))
                {
                    continue;
                }

                includedEntries.Add(item.GetRawText());
            }
        }
    }

    private static bool TryGetChapterFeedPageStats(string payloadJson, out ChapterFeedPageStats stats)
    {
        stats = default;

        var normalized = PluginJsonPayload.Normalize(payloadJson);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        using var doc = JsonDocument.Parse(normalized);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var dataCount = root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
            ? data.GetArrayLength()
            : 0;

        var total = root.TryGetProperty("total", out var totalElement) && totalElement.TryGetInt32(out var totalValue)
            ? totalValue
            : dataCount;

        var offset = root.TryGetProperty("offset", out var offsetElement) && offsetElement.TryGetInt32(out var offsetValue)
            ? offsetValue
            : 0;

        stats = new ChapterFeedPageStats(dataCount, total, offset);
        return true;
    }

    private static string BuildMergedChapterPayload(List<string> dataEntries, List<string> includedEntries)
    {
        var dataJson = string.Join(',', dataEntries);
        var includedJson = string.Join(',', includedEntries);
        return $"{{\"data\":[{dataJson}],\"included\":[{includedJson}]}}";
    }

    private readonly record struct ChapterFeedPageStats(int DataCount, int Total, int Offset);
}
#endif
