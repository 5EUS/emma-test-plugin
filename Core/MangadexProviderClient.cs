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
/// Mangadex-specific provider client inheriting from generic base.
/// Consolidates HTTP profile with URL building for Mangadex API.
/// </summary>
internal sealed class MangadexProviderClient : PluginProviderClient
{
    private static readonly IPluginProviderUrlStrategy Strategy = new MangadexUrlStrategy();

    protected override PluginProviderHttpProfile HttpProfile => ProviderHttpProfile.Defaults;

    public string? BuildSearchPath(PluginSearchQuery query)
    {
        var absoluteUrl = Strategy.BuildSearchAbsoluteUrl(query);
        return AbsoluteUrlToPath(absoluteUrl);
    }

    public string? BuildChaptersPath(string mediaId, int limit = 500, int offset = 0)
    {
        var absoluteUrl = BuildChaptersAbsoluteUrl(mediaId, limit, offset);
        return AbsoluteUrlToPath(absoluteUrl);
    }

    public string? BuildAtHomePath(string chapterId)
    {
        return AbsoluteUrlToPath(Strategy.BuildAtHomeAbsoluteUrl(chapterId));
    }

    public string? BuildSearchAbsoluteUrl(PluginSearchQuery query)
    {
        return Strategy.BuildSearchAbsoluteUrl(query);
    }

    public string? BuildChaptersAbsoluteUrl(string mediaId, int limit = 500, int offset = 0)
    {
        var cappedLimit = Math.Clamp(limit, 1, 500);
        var normalizedOffset = Math.Max(0, offset);

        return BuildResourceAbsoluteUrl(
            "/manga/{id}/feed",
            mediaId,
        [
            $"limit={cappedLimit}",
            $"offset={normalizedOffset}",
            "order[chapter]=asc",
            "translatedLanguage[]=en",
            "includeUnavailable=1",
            "includes[]=scanlation_group"
        ]);
    }

    public string? BuildAtHomeAbsoluteUrl(string chapterId)
    {
        return Strategy.BuildAtHomeAbsoluteUrl(chapterId);
    }

    public string? BuildTagCatalogAbsoluteUrl()
    {
        return BuildAbsoluteUrl("/manga/tag", []);
    }

    public string? BuildAuthorLookupAbsoluteUrl(string name, int limit = 10)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var cappedLimit = Math.Clamp(limit, 1, 10);
        var query = $"name={Uri.EscapeDataString(name.Trim())}&limit={cappedLimit}";
        return new Uri(BaseUri, $"/author?{query}").ToString();
    }

    public string? BuildStatisticsAbsoluteUrl(IEnumerable<string> mangaIds)
    {
        var ids = mangaIds
            .Select(id => id.Trim())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ids.Count == 0)
        {
            return null;
        }

        var parameters = new List<string>();
        PluginUriUtilities.AddQueryParameters(parameters, "manga[]", ids);

        return BuildAbsoluteUrl("/statistics/manga", parameters);
    }

    public string? BuildStatisticsAbsoluteUrl(string mangaId)
    {
        if (string.IsNullOrWhiteSpace(mangaId))
        {
            return null;
        }

        return new Uri(BaseUri, $"/statistics/manga/{Uri.EscapeDataString(mangaId.Trim())}").ToString();
    }

    /// <summary>
    /// Singleton instance for convenience.
    /// </summary>
    public static readonly MangadexProviderClient Instance = new();

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
            "includes[]=cover_art",
            "includes[]=author",
            "includes[]=artist",
            "includes[]=tag",
            "includes[]=creator"
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
                new Uri("https://api.mangadex.org"),
                "/manga",
                parameters);
        }

        public string? BuildChaptersAbsoluteUrl(string mediaId)
        {
            return Instance.BuildChaptersAbsoluteUrl(mediaId, limit: 500, offset: 0);
        }

        public string? BuildAtHomeAbsoluteUrl(string chapterId)
        {
            return PluginProviderUrlTemplates.BuildResourceByIdAbsoluteUrl(
                baseUri: new Uri("https://api.mangadex.org"),
                pathTemplate: "/at-home/server/{id}",
                id: chapterId,
                queryParameters: []);
        }
    }
}
