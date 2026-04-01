using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using EMMA.Plugin.Common;

namespace EMMA.TestPlugin.Infrastructure;

internal static class ProviderSearchQueryResolver
{
    private static readonly TimeSpan TagLookupCacheTtl = TimeSpan.FromHours(1);
    private static readonly SemaphoreSlim TagLookupRefreshGate = new(1, 1);
    private static readonly ConcurrentDictionary<string, string?> AuthorLookupCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string?> ArtistLookupCache = new(StringComparer.OrdinalIgnoreCase);
    private static TagLookupCacheEntry _tagLookupCache =
        new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), DateTimeOffset.MinValue);

    private static readonly Regex UuidRegex = new(
        "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[1-5][0-9a-fA-F]{3}-[89abAB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private sealed record TagLookupCacheEntry(IReadOnlyDictionary<string, string> ValueByName, DateTimeOffset FetchedAtUtc);

    public static async Task<PluginSearchQuery> ResolveAsync(
        PluginSearchQuery query,
        Func<string, CancellationToken, Task<string?>> fetchAbsoluteUrlAsync,
        CancellationToken cancellationToken)
    {
        if (query.Filters.Count == 0)
        {
            return query;
        }

        var resolvedFilters = new List<PluginSearchFilter>(query.Filters.Count);
        foreach (var filter in query.Filters)
        {
            var normalizedFilterId = filter.Id?.Trim();
            if (string.IsNullOrWhiteSpace(normalizedFilterId))
            {
                continue;
            }

            var filterId = normalizedFilterId.ToLowerInvariant();
            if (filter.Values.Count == 0)
            {
                resolvedFilters.Add(filter);
                continue;
            }

            IReadOnlyList<string> resolvedValues;
            if (filterId is "core.tags" or "core.tags.exclude")
            {
                resolvedValues = await ResolveTagValuesAsync(filter.Values, fetchAbsoluteUrlAsync, cancellationToken);
            }
            else if (filterId == "core.author")
            {
                resolvedValues = await ResolveAuthorValuesAsync(filter.Values, AuthorLookupCache, fetchAbsoluteUrlAsync, cancellationToken);
            }
            else if (filterId == "core.artist")
            {
                resolvedValues = await ResolveAuthorValuesAsync(filter.Values, ArtistLookupCache, fetchAbsoluteUrlAsync, cancellationToken);
            }
            else
            {
                resolvedValues = filter.Values;
            }

            resolvedFilters.Add(new PluginSearchFilter(normalizedFilterId, resolvedValues, filter.Operation));
        }

        return query with { Filters = resolvedFilters };
    }

    public static PluginSearchQuery Resolve(
        PluginSearchQuery query,
        Func<string, string?> fetchAbsoluteUrl)
    {
        return ResolveAsync(
                query,
                (absoluteUrl, _) => Task.FromResult(fetchAbsoluteUrl(absoluteUrl)),
                CancellationToken.None)
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();
    }

    private static async Task<IReadOnlyList<string>> ResolveTagValuesAsync(
        IReadOnlyList<string> values,
        Func<string, CancellationToken, Task<string?>> fetchAbsoluteUrlAsync,
        CancellationToken cancellationToken)
    {
        var lookup = await GetTagLookupAsync(fetchAbsoluteUrlAsync, cancellationToken);
        var resolved = new List<string>(values.Count);

        foreach (var value in values)
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            if (LooksLikeUuid(normalized))
            {
                resolved.Add(normalized);
                continue;
            }

            var key = normalized.ToLowerInvariant();
            if (lookup.TryGetValue(key, out var id) && !string.IsNullOrWhiteSpace(id))
            {
                resolved.Add(id);
                continue;
            }

            resolved.Add(normalized);
        }

        return resolved.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static async Task<IReadOnlyList<string>> ResolveAuthorValuesAsync(
        IReadOnlyList<string> values,
        ConcurrentDictionary<string, string?> cache,
        Func<string, CancellationToken, Task<string?>> fetchAbsoluteUrlAsync,
        CancellationToken cancellationToken)
    {
        var resolved = new List<string>(values.Count);

        foreach (var value in values)
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            if (LooksLikeUuid(normalized))
            {
                resolved.Add(normalized);
                continue;
            }

            var key = normalized.ToLowerInvariant();
            if (!cache.TryGetValue(key, out var id))
            {
                id = await ResolveAuthorOrArtistIdAsync(normalized, fetchAbsoluteUrlAsync, cancellationToken);
                cache[key] = id;
            }

            resolved.Add(string.IsNullOrWhiteSpace(id) ? normalized : id);
        }

        return resolved.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static async Task<IReadOnlyDictionary<string, string>> GetTagLookupAsync(
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

            var absoluteUrl = ProviderRequestUrls.BuildTagCatalogAbsoluteUrl();
            var payload = string.IsNullOrWhiteSpace(absoluteUrl)
                ? null
                : await fetchAbsoluteUrlAsync(absoluteUrl, cancellationToken);
            if (string.IsNullOrWhiteSpace(payload))
            {
                return snapshot.ValueByName;
            }

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return snapshot.ValueByName;
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

            _tagLookupCache = new TagLookupCacheEntry(map, DateTimeOffset.UtcNow);
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

    private static async Task<string?> ResolveAuthorOrArtistIdAsync(
        string input,
        Func<string, CancellationToken, Task<string?>> fetchAbsoluteUrlAsync,
        CancellationToken cancellationToken)
    {
        var absoluteUrl = ProviderRequestUrls.BuildAuthorLookupAbsoluteUrl(input, 10);
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

    private static bool LooksLikeUuid(string value)
    {
        return UuidRegex.IsMatch(value);
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
}
