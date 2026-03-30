using EMMA.Plugin.Common;

namespace EMMA.TestPlugin.Infrastructure;

internal static class ProviderHttpProfile
{
    public static readonly Uri BaseUri = new("https://api.mangadex.org");
    public const string UserAgent = "EMMA-TestPlugin/1.0";
    public const string AcceptMediaType = "application/json";
}

internal static class ProviderRequestUrls
{
    public static string? BuildSearchPath(string query)
    {
        return ToPathAndQuery(BuildSearchAbsoluteUrl(query));
    }

    public static string? BuildChaptersPath(string mediaId)
    {
        return ToPathAndQuery(BuildChaptersAbsoluteUrl(mediaId));
    }

    public static string? BuildAtHomePath(string chapterId)
    {
        return ToPathAndQuery(BuildAtHomeAbsoluteUrl(chapterId));
    }

    public static string? BuildSearchAbsoluteUrl(string query)
    {
        return BuildSearchAbsoluteUrl(new PluginSearchQuery(query ?? string.Empty, [], [], [], null, null, null));
    }

    public static string? BuildSearchAbsoluteUrl(PluginSearchQuery query)
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

        return $"{ProviderHttpProfile.BaseUri}/manga?{string.Join("&", parameters)}";
    }

    public static string? BuildChaptersAbsoluteUrl(string mediaId)
    {
        if (string.IsNullOrWhiteSpace(mediaId))
        {
            return null;
        }

        var encoded = Uri.EscapeDataString(mediaId.Trim());
        return $"{ProviderHttpProfile.BaseUri}/manga/{encoded}/feed?limit=100&order[chapter]=asc&translatedLanguage[]=en&includeUnavailable=1&includes[]=scanlation_group";
    }

    public static string? BuildAtHomeAbsoluteUrl(string chapterId)
    {
        if (string.IsNullOrWhiteSpace(chapterId))
        {
            return null;
        }

        var encoded = Uri.EscapeDataString(chapterId.Trim());
        return $"{ProviderHttpProfile.BaseUri}/at-home/server/{encoded}";
    }

    private static string? ToPathAndQuery(string? absoluteUrl)
    {
        if (string.IsNullOrWhiteSpace(absoluteUrl))
        {
            return null;
        }

        return new Uri(absoluteUrl, UriKind.Absolute).PathAndQuery;
    }
}