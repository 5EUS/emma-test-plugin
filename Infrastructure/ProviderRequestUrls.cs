using EMMA.Plugin.Common;

namespace EMMA.TestPlugin.Infrastructure;

internal static class ProviderHttpProfile
{
    public static readonly PluginProviderHttpProfile Defaults = new(
        BaseUri: new Uri("https://api.mangadex.org"),
        UserAgent: "EMMA-TestPlugin/1.0",
        AcceptMediaType: "application/json");
}

internal static class ProviderRequestUrls
{
    private static readonly IPluginProviderUrlStrategy Strategy = new MangadexUrlStrategy();

    public static string? BuildSearchPath(string query)
    {
        var parsed = PluginSearchQuery.Parse(query, fallbackQuery: query);
        return BuildSearchPath(parsed);
    }

    public static string? BuildSearchPath(PluginSearchQuery query)
    {
        return PluginUriUtilities.ToPathAndQuery(Strategy.BuildSearchAbsoluteUrl(query));
    }

    public static string? BuildChaptersPath(string mediaId)
    {
        return Strategy.BuildChaptersPath(mediaId);
    }

    public static string? BuildAtHomePath(string chapterId)
    {
        return Strategy.BuildAtHomePath(chapterId);
    }

    public static string? BuildSearchAbsoluteUrl(string query)
    {
        return Strategy.BuildSearchAbsoluteUrl(PluginSearchQuery.Parse(query, fallbackQuery: query));
    }

    public static string? BuildSearchAbsoluteUrl(PluginSearchQuery query)
    {
        return Strategy.BuildSearchAbsoluteUrl(query);
    }

    public static string? BuildChaptersAbsoluteUrl(string mediaId)
    {
        return Strategy.BuildChaptersAbsoluteUrl(mediaId);
    }

    public static string? BuildAtHomeAbsoluteUrl(string chapterId)
    {
        return Strategy.BuildAtHomeAbsoluteUrl(chapterId);
    }

    public static string? BuildTagCatalogAbsoluteUrl()
    {
        return new Uri(ProviderHttpProfile.Defaults.BaseUri, "/manga/tag").ToString();
    }

    public static string? BuildAuthorLookupAbsoluteUrl(string name, int limit = 10)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var cappedLimit = Math.Clamp(limit, 1, 10);
        var query = $"name={Uri.EscapeDataString(name.Trim())}&limit={cappedLimit}";
        return new Uri(ProviderHttpProfile.Defaults.BaseUri, $"/author?{query}").ToString();
    }

    private sealed class MangadexUrlStrategy : IPluginProviderUrlStrategy
    {
        public string? BuildSearchAbsoluteUrl(PluginSearchQuery query)
        {
        if (string.IsNullOrWhiteSpace(query.Query)
            && query.Filters.Count == 0
            && query.QueryAdditions.Count == 0)
        {
            return null;
        }

        var pageSize = Math.Clamp(query.PageSize ?? 100, 1, 100);
        var page = Math.Max(0, query.Page ?? 0);
        var offset = page * pageSize;

        var parameters = new List<string>
        {
            $"limit={pageSize}",
            $"offset={offset}",
            "includes[]=cover_art"
        };

        var contentRatings = query.GetFilterValues("core.maturity");
        if (contentRatings.Count == 0)
        {
            contentRatings = ["safe", "suggestive"];
        }

        PluginUriUtilities.AddQueryParameters(parameters, "contentRating[]", contentRatings);

        var includedTags = query.GetFilterValues("core.tags");
        var excludedTags = query.GetFilterValues("core.tags.exclude");

        PluginUriUtilities.AddQueryParameters(parameters, "includedTags[]", includedTags);
        PluginUriUtilities.AddQueryParameters(parameters, "excludedTags[]", excludedTags);
        PluginUriUtilities.AddQueryParameters(parameters, "authors[]", query.GetFilterValues("core.author"));
        PluginUriUtilities.AddQueryParameters(parameters, "artists[]", query.GetFilterValues("core.artist"));
        PluginUriUtilities.AddQueryParameters(parameters, "status[]", query.GetFilterValues("core.status"));
        PluginUriUtilities.AddQueryParameters(parameters, "publicationDemographic[]", query.GetFilterValues("core.demographic"));

        PluginUriUtilities.AddQueryParameter(parameters, "availableTranslatedLanguage[]", query.GetQueryAddition("core.language"));
        PluginUriUtilities.AddQueryParameter(parameters, "originalLanguage[]", query.GetQueryAddition("core.originalLanguage"));
        PluginUriUtilities.AddQueryParameter(parameters, "year", query.GetQueryAddition("core.year"));

        var includedTagMode = query.GetQueryAddition("core.tags.mode");
        if (includedTags.Count > 0 && !string.IsNullOrWhiteSpace(includedTagMode))
        {
            var normalizedIncludedMode = includedTagMode.Trim().ToUpperInvariant();
            if (normalizedIncludedMode is "AND" or "OR")
            {
                PluginUriUtilities.AddQueryParameter(parameters, "includedTagsMode", normalizedIncludedMode);
            }
        }

        var excludedTagMode = query.GetQueryAddition("core.tags.exclude.mode");
        if (excludedTags.Count > 0 && !string.IsNullOrWhiteSpace(excludedTagMode))
        {
            var normalizedExcludedMode = excludedTagMode.Trim().ToUpperInvariant();
            if (normalizedExcludedMode is "AND" or "OR")
            {
                PluginUriUtilities.AddQueryParameter(parameters, "excludedTagsMode", normalizedExcludedMode);
            }
        }

        if (!string.IsNullOrWhiteSpace(query.Query))
        {
            PluginUriUtilities.AddQueryParameter(parameters, "title", query.Query.Trim());
        }

        return PluginUriUtilities.BuildAbsoluteUrl(
            ProviderHttpProfile.Defaults.BaseUri,
            "/manga",
            parameters);
        }

        public string? BuildChaptersAbsoluteUrl(string mediaId)
        {
        return PluginProviderUrlTemplates.BuildResourceByIdAbsoluteUrl(
            baseUri: ProviderHttpProfile.Defaults.BaseUri,
            pathTemplate: "/manga/{id}/feed",
            id: mediaId,
            queryParameters: ["limit=100", "order[chapter]=asc", "translatedLanguage[]=en", "includeUnavailable=1", "includes[]=scanlation_group"]);
        }

        public string? BuildAtHomeAbsoluteUrl(string chapterId)
        {
        return PluginProviderUrlTemplates.BuildResourceByIdAbsoluteUrl(
            baseUri: ProviderHttpProfile.Defaults.BaseUri,
            pathTemplate: "/at-home/server/{id}",
            id: chapterId,
            queryParameters: []);
        }
    }
}