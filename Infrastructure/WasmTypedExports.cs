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
        payloadJson = PluginPayloadResolvers.ResolveProvidedOrHostPayload(
            payloadJson,
            "search",
            ProviderRequestUrls.BuildSearchAbsoluteUrl(query),
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
        payloadJson = PluginPayloadResolvers.ResolveProvidedOrHostPayload(
            payloadJson,
            "chapters",
            ProviderRequestUrls.BuildChaptersAbsoluteUrl(mediaId),
            (operation, hint) => HostBridgeInterop.OperationPayload(operation, hint));
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
            .Register("search", request => ProviderRequestUrls.BuildSearchAbsoluteUrl(PluginSearchQuery.Parse(request.argsJson)))
            .Register("benchmark-network", request => ProviderRequestUrls.BuildSearchAbsoluteUrl(PluginSearchQuery.Parse(request.argsJson)))
            .Register("chapters", request => ProviderRequestUrls.BuildChaptersAbsoluteUrl(request.ResolveMediaId()))
            .Register("page", request => ProviderRequestUrls.BuildAtHomeAbsoluteUrl(request.ResolveChapterId()))
            .Register("pages", request => ProviderRequestUrls.BuildAtHomeAbsoluteUrl(request.ResolveChapterId()));
    }
}
#endif
