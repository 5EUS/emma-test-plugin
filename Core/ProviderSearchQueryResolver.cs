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

            var absoluteUrl = ProviderRequestUrls.BuildTagCatalogAbsoluteUrl();
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
