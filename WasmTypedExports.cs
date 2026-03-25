#if PLUGIN_TRANSPORT_WASM
using System.Text.Json;
using LibraryWorld;
using LibraryWorld.wit.exports.emma.plugin;
using LibraryWorld.wit.imports.emma.plugin;

namespace LibraryWorld.wit.exports.emma.plugin;

public static class PluginImpl
{
    public static IPlugin.HandshakeResponse Handshake()
    {
        var handshake = EMMA.TestPlugin.Program.handshake();
        return new IPlugin.HandshakeResponse(handshake.version, handshake.message);
    }

    public static List<IPlugin.Capability> Capabilities()
    {
        var capabilities = EMMA.TestPlugin.Program.capabilities();
        return [.. capabilities.Select(capability => new IPlugin.Capability(
            capability.name,
            [.. capability.mediaTypes],
            [.. capability.operations]))];
    }

    public static List<IPlugin.MediaSearchItem> Search(string query, string payloadJson)
    {
        payloadJson = ResolvePayload(payloadJson, "search", BuildSearchUrl(query));
        var items = EMMA.TestPlugin.Program.search(query, payloadJson);

        return [.. items.Select(item => new IPlugin.MediaSearchItem(
            item.id,
            item.source,
            item.title,
            item.mediaType,
            item.thumbnailUrl,
            item.description,
            []))];
    }

    public static List<IPlugin.ChapterItem> Chapters(string mediaId, string payloadJson)
    {
        payloadJson = ResolvePayload(payloadJson, "chapters", BuildChaptersUrl(mediaId));
        var items = EMMA.TestPlugin.Program.chapters(mediaId, payloadJson);

        return [.. items.Select(item => new IPlugin.ChapterItem(
            item.id,
            checked((uint)item.number),
            item.title))];
    }

    public static IPlugin.PageItem? Page(string mediaId, string chapterId, uint pageIndex, string payloadJson)
    {
        payloadJson = ResolvePayload(payloadJson, "page", BuildAtHomeUrl(chapterId));

        var page = EMMA.TestPlugin.Program.page(mediaId, chapterId, pageIndex, payloadJson);
        if (page is null)
        {
            return null;
        }

        return new IPlugin.PageItem(page.id, checked((uint)page.index), page.contentUri);
    }

    public static List<IPlugin.PageItem> Pages(string mediaId, string chapterId, uint startIndex, uint count, string payloadJson)
    {
        payloadJson = ResolvePayload(payloadJson, "pages", BuildAtHomeUrl(chapterId));

        var pages = EMMA.TestPlugin.Program.pages(mediaId, chapterId, startIndex, count, payloadJson);
        return [.. pages.Select(page => new IPlugin.PageItem(page.id, checked((uint)page.index), page.contentUri))];
    }

    public static IPlugin.MediaOperationResponse Invoke(IPlugin.MediaOperationRequest request)
    {
        var payload = ResolveInvokePayload(request);

        var result = EMMA.TestPlugin.Program.invoke(new EMMA.TestPlugin.Program.OperationRequest(
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
        if (!string.IsNullOrWhiteSpace(request.payloadJson))
        {
            return request.payloadJson;
        }

        var operation = (request.operation ?? string.Empty).Trim().ToLowerInvariant();
        var operationName = request.operation ?? string.Empty;
        return operation switch
        {
            "search" or "benchmark-network" =>
                HostBridgeInterop.OperationPayload(operationName, BuildSearchUrl(GetJsonArgString(request.argsJson, "query") ?? string.Empty)),
            "chapters" =>
                HostBridgeInterop.OperationPayload(
                    operationName,
                    BuildChaptersUrl(request.mediaId ?? GetJsonArgString(request.argsJson, "mediaId") ?? string.Empty)),
            "page" =>
                HostBridgeInterop.OperationPayload(
                    operationName,
                    BuildAtHomeUrl(GetJsonArgString(request.argsJson, "chapterId") ?? string.Empty)),
            "pages" =>
                HostBridgeInterop.OperationPayload(
                    operationName,
                    BuildAtHomeUrl(GetJsonArgString(request.argsJson, "chapterId") ?? string.Empty)),
            _ => HostBridgeInterop.OperationPayload(operationName, request.argsJson)
        };
    }

    private static string ResolvePayload(string payloadJson, string operation, string? payloadUrl)
    {
        if (!string.IsNullOrWhiteSpace(payloadJson))
        {
            return payloadJson;
        }

        if (string.IsNullOrWhiteSpace(payloadUrl))
        {
            return string.Empty;
        }

        return HostBridgeInterop.OperationPayload(operation, payloadUrl) ?? string.Empty;
    }

    private static string? BuildSearchUrl(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var encoded = Uri.EscapeDataString(query.Trim());
        return $"https://api.mangadex.org/manga?title={encoded}&limit=20&contentRating[]=safe&contentRating[]=suggestive&includes[]=cover_art";
    }

    private static string? BuildChaptersUrl(string mediaId)
    {
        if (string.IsNullOrWhiteSpace(mediaId))
        {
            return null;
        }

        var encoded = Uri.EscapeDataString(mediaId.Trim());
        return $"https://api.mangadex.org/manga/{encoded}/feed?limit=100&order[chapter]=asc&translatedLanguage[]=en&includeUnavailable=1";
    }

    private static string? BuildAtHomeUrl(string chapterId)
    {
        if (string.IsNullOrWhiteSpace(chapterId))
        {
            return null;
        }

        var encoded = Uri.EscapeDataString(chapterId.Trim());
        return $"https://api.mangadex.org/at-home/server/{encoded}";
    }

    private static string? GetJsonArgString(string? argsJson, string key)
    {
        if (string.IsNullOrWhiteSpace(argsJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty(key, out var prop))
            {
                return null;
            }

            return prop.ValueKind switch
            {
                JsonValueKind.String => prop.GetString(),
                JsonValueKind.Number => prop.GetRawText(),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static WitException<IPlugin.OperationError> CreateOperationError(string? error)
    {
        var message = string.IsNullOrWhiteSpace(error) ? "operation failed" : error.Trim();

        if (message.StartsWith("unsupported-operation:", StringComparison.OrdinalIgnoreCase))
        {
            return new WitException<IPlugin.OperationError>(
                IPlugin.OperationError.UnsupportedOperation(message["unsupported-operation:".Length..]),
                0);
        }

        if (message.StartsWith("invalid-arguments:", StringComparison.OrdinalIgnoreCase))
        {
            return new WitException<IPlugin.OperationError>(
                IPlugin.OperationError.InvalidArguments(message["invalid-arguments:".Length..]),
                0);
        }

        if (message.StartsWith("failed:", StringComparison.OrdinalIgnoreCase))
        {
            return new WitException<IPlugin.OperationError>(
                IPlugin.OperationError.Failed(message["failed:".Length..]),
                0);
        }

        return new WitException<IPlugin.OperationError>(IPlugin.OperationError.Failed(message), 0);
    }
}
#endif
