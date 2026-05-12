using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using EMMA.Plugin.Common;

namespace EMMA.TestPlugin.Core;

/// <summary>
/// Mangadex-specific search query resolver inheriting from generic base.
/// Handles tag catalog lookup, author/artist resolution with caching.
/// </summary>
internal sealed partial class ProviderSearchQueryResolver : PluginSearchQueryEnricher
{
    private static readonly TimeSpan TagLookupCacheTtl = TimeSpan.FromHours(1);
    private static readonly SemaphoreSlim TagLookupRefreshGate = new(1, 1);
    private static readonly ConcurrentDictionary<string, string?> AuthorLookupCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string?> ArtistLookupCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly MangadexProviderClient ProviderClient = MangadexProviderClient.Instance;
    private static CatalogCacheEntry _tagLookupCache =
        new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), DateTimeOffset.MinValue);

    private static readonly Regex UuidRegex = UuidValidationRegex();

    /// <summary>
    /// Singleton instance for reuse across requests.
    /// </summary>
    public static readonly ProviderSearchQueryResolver Instance = new();

    protected override string[] ResolvableFilterIds =>
    [
        "core.tags",
        "core.tags.exclude",
        "core.author",
        "core.artist"
    ];

    protected override TimeSpan FilterCacheTtl => TagLookupCacheTtl;

    protected override async Task<IReadOnlyList<string>> ResolveFilterValuesAsync(
        string filterId,
        IReadOnlyList<string> values,
        Func<string, CancellationToken, Task<string?>> fetchAbsoluteUrlAsync,
        CancellationToken cancellationToken)
    {
        return filterId switch
        {
            "core.tags" or "core.tags.exclude" => await ResolveLookupValuesAsync(
                values,
                ct => GetTagLookupAsync(fetchAbsoluteUrlAsync, ct),
                fetchAbsoluteUrlAsync,
                cancellationToken),

            "core.author" => await ResolveWithCacheAsync(
                values,
                AuthorLookupCache,
                (name, ct) => ResolveAuthorOrArtistIdAsync(name, fetchAbsoluteUrlAsync, ct),
                cancellationToken),

            "core.artist" => await ResolveWithCacheAsync(
                values,
                ArtistLookupCache,
                (name, ct) => ResolveAuthorOrArtistIdAsync(name, fetchAbsoluteUrlAsync, ct),
                cancellationToken),

            _ => values
        };
    }

    protected override bool LooksLikeUuid(string value)
    {
        return UuidRegex.IsMatch(value);
    }

    protected override IReadOnlyDictionary<string, string> ParseCatalogResponse(string payloadJson)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(payloadJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            return map;
        }

        foreach (var tag in data.EnumerateArray())
        {
            if (tag.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = ReadString(tag, "id");
            if (string.IsNullOrWhiteSpace(id)
                || !tag.TryGetProperty("attributes", out var attributes)
                || attributes.ValueKind != JsonValueKind.Object
                || !attributes.TryGetProperty("name", out var names)
                || names.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var translation in names.EnumerateObject())
            {
                var normalized = translation.Value.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    continue;
                }

                map[normalized.ToLowerInvariant()] = id;
            }
        }

        return map;
    }

    internal Task<IReadOnlyDictionary<string, string>> GetTagLookupAsync(
        PluginPayloadSource payloadSource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payloadSource);
        return GetTagLookupAsync(payloadSource.FetchAsync, cancellationToken);
    }

    private async Task<IReadOnlyDictionary<string, string>> GetTagLookupAsync(
        Func<string, CancellationToken, Task<string?>> fetchAbsoluteUrlAsync,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = _tagLookupCache;
        if (snapshot.ValueByName.Count > 0 && now - snapshot.FetchedAtUtc <= TagLookupCacheTtl)
        {
            return snapshot.ValueByName;
        }

        await TagLookupRefreshGate.WaitAsync(cancellationToken);
        try
        {
            snapshot = _tagLookupCache;
            now = DateTimeOffset.UtcNow;
            if (snapshot.ValueByName.Count > 0 && now - snapshot.FetchedAtUtc <= TagLookupCacheTtl)
            {
                return snapshot.ValueByName;
            }

            var absoluteUrl = ProviderClient.BuildTagCatalogAbsoluteUrl();
            var payload = string.IsNullOrWhiteSpace(absoluteUrl)
                ? null
                : await fetchAbsoluteUrlAsync(absoluteUrl, cancellationToken);

            if (string.IsNullOrWhiteSpace(payload))
            {
                return snapshot.ValueByName;
            }

            var map = ParseCatalogResponse(payload);
            _tagLookupCache = new CatalogCacheEntry(map, DateTimeOffset.UtcNow);
            return _tagLookupCache.ValueByName;
        }
        catch
        {
            return snapshot.ValueByName;
        }
        finally
        {
            TagLookupRefreshGate.Release();
        }
    }

    private async Task<string?> ResolveAuthorOrArtistIdAsync(
        string input,
        Func<string, CancellationToken, Task<string?>> fetchAbsoluteUrlAsync,
        CancellationToken cancellationToken)
    {
        var absoluteUrl = ProviderClient.BuildAuthorLookupAbsoluteUrl(input, 10);
        if (string.IsNullOrWhiteSpace(absoluteUrl))
        {
            return null;
        }

        var payload = await fetchAbsoluteUrlAsync(absoluteUrl, cancellationToken);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(payload);
        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var needle = input.Trim();
        string? fuzzy = null;
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = ReadString(item, "id");
            if (!item.TryGetProperty("attributes", out var attributes)
                || attributes.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = ReadString(attributes, "name");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (string.Equals(name, needle, StringComparison.OrdinalIgnoreCase))
            {
                return id;
            }

            if (fuzzy is null && name.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                fuzzy = id;
            }
        }

        return fuzzy;
    }

    internal Task<IReadOnlyList<SearchSuggestionItem>> GetAuthorOrArtistSuggestionsAsync(
        string input,
        int limit,
        HashSet<string> excludedValues,
        PluginPayloadSource payloadSource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payloadSource);
        return GetAuthorOrArtistSuggestionsAsync(input, limit, excludedValues, payloadSource.FetchAsync, cancellationToken);
    }

    private async Task<IReadOnlyList<SearchSuggestionItem>> GetAuthorOrArtistSuggestionsAsync(
        string input,
        int limit,
        HashSet<string> excludedValues,
        Func<string, CancellationToken, Task<string?>> fetchAbsoluteUrlAsync,
        CancellationToken cancellationToken)
    {
        var absoluteUrl = ProviderClient.BuildAuthorLookupAbsoluteUrl(input, limit);
        if (string.IsNullOrWhiteSpace(absoluteUrl))
        {
            return [];
        }

        var payload = await fetchAbsoluteUrlAsync(absoluteUrl, cancellationToken);
        return ParseAuthorOrArtistSuggestions(payload, input, limit, excludedValues);
    }

    private static IReadOnlyList<SearchSuggestionItem> ParseAuthorOrArtistSuggestions(
        string? payloadJson,
        string input,
        int limit,
        HashSet<string> excludedValues)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return [];
        }

        var suggestions = new List<SearchSuggestionItem>();

        using var doc = JsonDocument.Parse(payloadJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            return suggestions;
        }

        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = ReadString(item, "id");
            if (!item.TryGetProperty("attributes", out var attributes)
                || attributes.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = ReadString(attributes, "name");
            if (string.IsNullOrWhiteSpace(id)
                || string.IsNullOrWhiteSpace(name)
                || excludedValues.Contains(name))
            {
                continue;
            }

            suggestions.Add(new SearchSuggestionItem(name, name, id));
        }

        var needle = input?.Trim() ?? string.Empty;
        return [.. suggestions
            .DistinctBy(static suggestion => suggestion.Value, StringComparer.OrdinalIgnoreCase)
            .OrderBy(suggestion => RankSuggestion(suggestion.Label, needle))
            .ThenBy(static suggestion => suggestion.Label, StringComparer.OrdinalIgnoreCase)
            .Take(limit)];
    }

    private static int RankSuggestion(string candidate, string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return 2;
        }

        if (string.Equals(candidate, input, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (candidate.StartsWith(input, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return 2;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString()?.Trim();
        }

        return null;
    }

    [GeneratedRegex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[1-5][0-9a-fA-F]{3}-[89abAB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}$", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex UuidValidationRegex();
}
