#if PLUGIN_TRANSPORT_WASM
using System.Net.Http;
using EMMA.Plugin.Common;

namespace EMMA.TestPlugin.Infrastructure;

internal sealed class WasmClient
{
    private static readonly CoreClient Core = new();
    private static readonly HttpClient Http = CreateHttpClient();

    public SearchParseMapResult SearchFromPayloadWithTimings(string payloadJson)
    {
        var parseMap = Core.SearchFromPayloadWithTimings(payloadJson);
        return new SearchParseMapResult(parseMap.Results, parseMap.ParseMs, parseMap.MapMs);
    }

    public IReadOnlyList<ChapterItem> GetChaptersFromPayload(string mediaId, string payloadJson)
    {
        return Core.GetChaptersFromPayload(payloadJson);
    }

    public IReadOnlyList<ChapterOperationItem> GetChapterOperationItemsFromPayload(string mediaId, string payloadJson)
    {
        return Core.GetChapterOperationItemsFromPayload(payloadJson);
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
        return TryFetchPayload(ProviderRequestUrls.BuildSearchAbsoluteUrl(query));
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
}
#endif