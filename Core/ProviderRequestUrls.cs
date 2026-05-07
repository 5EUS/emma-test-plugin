using EMMA.Plugin.Common;

namespace EMMA.TestPlugin.Core;

internal static class ProviderHttpProfile
{
    public static readonly PluginProviderHttpProfile Defaults = new(
        BaseUri: new Uri("https://api.mangadex.org"),
        UserAgent: "EMMA-TestPlugin/1.0 (+https://github.com/5EUS/emma-test-plugin)",
        AcceptMediaType: "application/json");
}

/// <summary>
/// URL builder facade for Mangadex API.
/// Delegates URL construction to <see cref="MangadexProviderClient"/> to keep
/// a single source of truth for request URL behavior.
/// </summary>
internal static class ProviderRequestUrls
{
    public static string? BuildSearchPath(string query)
    {
        var parsed = PluginSearchQuery.Parse(query, fallbackQuery: query);
        return BuildSearchPath(parsed);
    }

    public static string? BuildSearchPath(PluginSearchQuery query)
    {
        return PluginUriUtilities.ToPathAndQuery(BuildSearchAbsoluteUrl(query));
    }

    public static string? BuildChaptersPath(string mediaId)
    {
        return BuildChaptersPath(mediaId, limit: 500, offset: 0);
    }

    public static string? BuildChaptersPath(string mediaId, int limit, int offset)
    {
        return PluginUriUtilities.ToPathAndQuery(BuildChaptersAbsoluteUrl(mediaId, limit, offset));
    }

    public static string? BuildAtHomePath(string chapterId)
    {
        return PluginUriUtilities.ToPathAndQuery(BuildAtHomeAbsoluteUrl(chapterId));
    }

    public static string? BuildSearchAbsoluteUrl(string query)
    {
        return BuildSearchAbsoluteUrl(PluginSearchQuery.Parse(query, fallbackQuery: query));
    }

    public static string? BuildSearchAbsoluteUrl(PluginSearchQuery query)
    {
        return MangadexProviderClient.Instance.BuildSearchAbsoluteUrl(query);
    }

    public static string? BuildChaptersAbsoluteUrl(string mediaId)
    {
        return BuildChaptersAbsoluteUrl(mediaId, limit: 500, offset: 0);
    }

    public static string? BuildChaptersAbsoluteUrl(string mediaId, int limit, int offset)
    {
        return MangadexProviderClient.Instance.BuildChaptersAbsoluteUrl(mediaId, limit, offset);
    }

    public static string? BuildAtHomeAbsoluteUrl(string chapterId)
    {
        return MangadexProviderClient.Instance.BuildAtHomeAbsoluteUrl(chapterId);
    }

    public static string? BuildTagCatalogAbsoluteUrl()
    {
        return MangadexProviderClient.Instance.BuildTagCatalogAbsoluteUrl();
    }

    public static string? BuildAuthorLookupAbsoluteUrl(string name, int limit = 10)
    {
        return MangadexProviderClient.Instance.BuildAuthorLookupAbsoluteUrl(name, limit);
    }

    public static string? BuildStatisticsAbsoluteUrl(IEnumerable<string> mangaIds)
    {
        return MangadexProviderClient.Instance.BuildStatisticsAbsoluteUrl(mangaIds);
    }

    public static string? BuildStatisticsAbsoluteUrl(string mangaId)
    {
        return MangadexProviderClient.Instance.BuildStatisticsAbsoluteUrl(mangaId);
    }
}
