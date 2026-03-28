#if PLUGIN_TRANSPORT_WASM
using System.Text.Json;
using EMMA.Plugin.Common;
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
            item.title,
            [.. item.uploaderGroups ?? []]))];
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
        if (!string.IsNullOrWhiteSpace(request.payloadJson))
        {
            return request.payloadJson;
        }

        var operation = (request.operation ?? string.Empty).Trim().ToLowerInvariant();
        var operationName = request.operation ?? string.Empty;
        var searchArgs = PluginSearchQuery.Parse(request.argsJson);
        return operation switch
        {
            "search" or "benchmark-network" =>
                HostBridgeInterop.OperationPayload(operationName, BuildSearchUrl(searchArgs)),
            "chapters" =>
                HostBridgeInterop.OperationPayload(
                    operationName,
                    BuildChaptersUrl(request.mediaId ?? PluginJsonArgs.GetString(request.argsJson, "mediaId"))),
            "page" =>
                HostBridgeInterop.OperationPayload(
                    operationName,
                    BuildAtHomeUrl(PluginJsonArgs.GetString(request.argsJson, "chapterId"))),
            "pages" =>
                HostBridgeInterop.OperationPayload(
                    operationName,
                    BuildAtHomeUrl(PluginJsonArgs.GetString(request.argsJson, "chapterId"))),
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
        return BuildSearchUrl(new PluginSearchQuery(query ?? string.Empty, [], [], [], null, null, null));
    }

    private static string? BuildSearchUrl(PluginSearchQuery query)
    {
        if (string.IsNullOrWhiteSpace(query.Query))
        {
            return null;
        }

        var parameters = new List<string>
        {
            $"title={Uri.EscapeDataString(query.Query.Trim())}",
            "limit=20",
            "includes[]=cover_art"
        };

        var contentRatings = query.GetFilterValues("core.maturity");
        if (contentRatings.Count == 0)
        {
            contentRatings = ["safe", "suggestive"];
        }

        foreach (var rating in contentRatings)
        {
            parameters.Add($"contentRating[]={Uri.EscapeDataString(rating)}");
        }

        var includedTags = query.GetFilterValues("core.tags");
        foreach (var tag in includedTags)
        {
            parameters.Add($"includedTags[]={Uri.EscapeDataString(tag)}");
        }

        var excludedTags = query.GetFilterValues("core.tags.exclude");
        foreach (var tag in excludedTags)
        {
            parameters.Add($"excludedTags[]={Uri.EscapeDataString(tag)}");
        }

        foreach (var author in query.GetFilterValues("core.author"))
        {
            parameters.Add($"authors[]={Uri.EscapeDataString(author)}");
        }

        foreach (var artist in query.GetFilterValues("core.artist"))
        {
            parameters.Add($"artists[]={Uri.EscapeDataString(artist)}");
        }

        foreach (var status in query.GetFilterValues("core.status"))
        {
            parameters.Add($"status[]={Uri.EscapeDataString(status)}");
        }

        foreach (var demographic in query.GetFilterValues("core.demographic"))
        {
            parameters.Add($"publicationDemographic[]={Uri.EscapeDataString(demographic)}");
        }

        var translatedLanguage = query.GetQueryAddition("core.language");
        if (!string.IsNullOrWhiteSpace(translatedLanguage))
        {
            parameters.Add($"availableTranslatedLanguage[]={Uri.EscapeDataString(translatedLanguage.Trim())}");
        }

        var originalLanguage = query.GetQueryAddition("core.originalLanguage");
        if (!string.IsNullOrWhiteSpace(originalLanguage))
        {
            parameters.Add($"originalLanguage[]={Uri.EscapeDataString(originalLanguage.Trim())}");
        }

        var year = query.GetQueryAddition("core.year");
        if (!string.IsNullOrWhiteSpace(year))
        {
            parameters.Add($"year={Uri.EscapeDataString(year.Trim())}");
        }

        var includedTagMode = query.GetQueryAddition("core.tags.mode");
        if (includedTags.Count > 0 && !string.IsNullOrWhiteSpace(includedTagMode))
        {
            var normalizedIncludedMode = includedTagMode.Trim().ToUpperInvariant();
            if (normalizedIncludedMode is "AND" or "OR")
            {
                parameters.Add($"includedTagsMode={Uri.EscapeDataString(normalizedIncludedMode)}");
            }
        }

        var excludedTagMode = query.GetQueryAddition("core.tags.exclude.mode");
        if (excludedTags.Count > 0 && !string.IsNullOrWhiteSpace(excludedTagMode))
        {
            var normalizedExcludedMode = excludedTagMode.Trim().ToUpperInvariant();
            if (normalizedExcludedMode is "AND" or "OR")
            {
                parameters.Add($"excludedTagsMode={Uri.EscapeDataString(normalizedExcludedMode)}");
            }
        }

        return $"https://api.mangadex.org/manga?{string.Join("&", parameters)}";
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
