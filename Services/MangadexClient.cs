using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using EMMA.Plugin.Common;
using EMMA.Contracts.Plugins;
using Microsoft.Extensions.Logging;

namespace EMMA.TestPlugin.Services;

public sealed class MangadexClient(HttpClient httpClient, ILogger<MangadexClient> logger)
{
    private const string SourceId = "mangadex";
    private const string MediaTypePaged = "paged";
    private readonly HttpClient _httpClient = httpClient;
    private readonly ILogger<MangadexClient> _logger = logger;
    private static readonly ConcurrentDictionary<string, CachedAtHomePayload> AtHomeCache = new();
    private static readonly TimeSpan AtHomeCacheTtl = TimeSpan.FromMinutes(2);
    private static readonly ConcurrentDictionary<string, CachedChapterPages> ChapterPagesCache = new();
    private static readonly TimeSpan ChapterPagesCacheTtl = TimeSpan.FromMinutes(14);
    private static readonly SemaphoreSlim RequestGate = new(1, 1);
    private static readonly TimeSpan MinRequestSpacing = TimeSpan.FromMilliseconds(250);
    private static DateTimeOffset _lastRequestStartedUtc = DateTimeOffset.MinValue;

    private readonly record struct CachedAtHomePayload(string PayloadJson, DateTimeOffset FetchedAtUtc);
    private sealed record CachedChapterPages(IReadOnlyList<MediaPage> Pages, DateTimeOffset FetchedAtUtc);

    public async Task<IReadOnlyList<MediaSummary>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var path = $"/manga?title={Uri.EscapeDataString(query)}&limit=20&contentRating[]=safe&contentRating[]=suggestive&includes[]=cover_art";
        using var response = await GetWithPolicyAsync(path, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var data = PluginJsonElement.GetArray(doc.RootElement, "data");
        if (data is null)
        {
            return [];
        }

        var results = new List<MediaSummary>();
        foreach (var item in data.Value.EnumerateArray())
        {
            var id = PluginJsonElement.GetString(item, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var title = GetTitle(item);
            if (string.IsNullOrWhiteSpace(title))
            {
                title = "Untitled";
            }

            results.Add(new MediaSummary
            {
                Id = id,
                Source = SourceId,
                Title = title,
                MediaType = MediaTypePaged,
                ThumbnailUrl = BuildThumbnailUrl(item) ?? string.Empty,
                Description = GetDescription(item) ?? string.Empty
            });
        }

        _logger.LogInformation("Mangadex search query={Query} results={Count}", query, results.Count);
        return results;
    }

    public async Task<IReadOnlyList<MediaChapter>> GetChaptersAsync(string mediaId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(mediaId))
        {
            return [];
        }

        var path = $"/manga/{Uri.EscapeDataString(mediaId)}/feed?limit=100&order[chapter]=asc&translatedLanguage[]=en&includeUnavailable=1";
        using var response = await GetWithPolicyAsync(path, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var data = PluginJsonElement.GetArray(doc.RootElement, "data");
        if (data is null)
        {
            return [];
        }

        var results = new List<MediaChapter>();
        var index = 0;
        foreach (var item in data.Value.EnumerateArray())
        {
            var id = PluginJsonElement.GetString(item, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var attributes = PluginJsonElement.GetObject(item, "attributes");
            var pages = attributes is null ? null : PluginJsonElement.GetInt32(attributes.Value, "pages");
            if (pages is not null && pages <= 0)
            {
                continue;
            }

            var title = attributes is null ? null : PluginJsonElement.GetString(attributes.Value, "title");
            var chapterText = attributes is null ? null : PluginJsonElement.GetString(attributes.Value, "chapter");
            var number = index + 1;
            if (!string.IsNullOrWhiteSpace(chapterText) && int.TryParse(chapterText, out var parsed))
            {
                number = parsed;
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                title = string.IsNullOrWhiteSpace(chapterText)
                    ? $"Chapter {number}"
                    : $"Chapter {chapterText}";
            }

            results.Add(new MediaChapter
            {
                Id = id,
                Number = number,
                Title = title
            });
            index++;
        }

        _logger.LogInformation("Mangadex chapters mediaId={MediaId} count={Count}", mediaId, results.Count);
        return results;
    }

    public async Task<MediaPage?> GetPageAsync(string chapterId, int pageIndex, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(chapterId) || pageIndex < 0)
        {
            return null;
        }

        var pages = await GetChapterPagesAsync(chapterId, cancellationToken);
        if (pageIndex >= pages.Count)
        {
            return null;
        }

        return pages[pageIndex];
    }

    public async Task<(IReadOnlyList<MediaPage> Pages, bool ReachedEnd)> GetPagesAsync(
        string chapterId,
        int startIndex,
        int count,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(chapterId) || startIndex < 0 || count <= 0)
        {
            return ([], true);
        }

        var pages = await GetChapterPagesAsync(chapterId, cancellationToken);
        if (startIndex >= pages.Count)
        {
            return ([], true);
        }

        var max = Math.Min(pages.Count - startIndex, count);
        var slice = pages
            .Skip(startIndex)
            .Take(max)
            .ToList();

        var reachedEnd = startIndex + slice.Count >= pages.Count;
        return (slice, reachedEnd);
    }

    private async Task<IReadOnlyList<MediaPage>> GetChapterPagesAsync(string chapterId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (ChapterPagesCache.TryGetValue(chapterId, out var cached)
            && now - cached.FetchedAtUtc <= ChapterPagesCacheTtl)
        {
            return cached.Pages;
        }

        var payloadJson = await GetAtHomePayloadAsync(chapterId, cancellationToken);
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return [];
        }

        using var doc = JsonDocument.Parse(payloadJson);

        var baseUrl = PluginJsonElement.GetString(doc.RootElement, "baseUrl");
        var chapter = PluginJsonElement.GetObject(doc.RootElement, "chapter");
        if (string.IsNullOrWhiteSpace(baseUrl) || chapter is null)
        {
            return [];
        }

        var hash = PluginJsonElement.GetString(chapter.Value, "hash");
        var files = PluginJsonElement.GetArray(chapter.Value, "data");
        var dataPathSegment = "data";
        if (files is null || files.Value.GetArrayLength() == 0)
        {
            files = PluginJsonElement.GetArray(chapter.Value, "dataSaver");
            dataPathSegment = "data-saver";
        }

        if (string.IsNullOrWhiteSpace(hash) || files is null)
        {
            return [];
        }

        var pages = new List<MediaPage>();
        var index = 0;
        foreach (var item in files.Value.EnumerateArray())
        {
            var fileName = item.GetString();
            if (string.IsNullOrWhiteSpace(fileName))
            {
                index++;
                continue;
            }

            var uri = new Uri($"{baseUrl}/{dataPathSegment}/{hash}/{fileName}", UriKind.Absolute);
            pages.Add(new MediaPage
            {
                Id = $"{chapterId}:{index}",
                Index = index,
                ContentUri = uri.ToString()
            });
            index++;
        }

        ChapterPagesCache[chapterId] = new CachedChapterPages(pages, DateTimeOffset.UtcNow);
        return pages;
    }

    private async Task<string?> GetAtHomePayloadAsync(string chapterId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (AtHomeCache.TryGetValue(chapterId, out var cached)
            && now - cached.FetchedAtUtc <= AtHomeCacheTtl)
        {
            return cached.PayloadJson;
        }

        var path = $"/at-home/server/{Uri.EscapeDataString(chapterId)}";
        using var response = await GetWithPolicyAsync(path, cancellationToken);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        AtHomeCache[chapterId] = new CachedAtHomePayload(payload, DateTimeOffset.UtcNow);
        return payload;
    }

    private async Task<HttpResponseMessage> GetWithPolicyAsync(string path, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            await WaitForRequestSlotAsync(cancellationToken);

            var response = await _httpClient.GetAsync(path, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            var statusCode = (int)response.StatusCode;
            var transient = response.StatusCode == (HttpStatusCode)429
                || response.StatusCode is HttpStatusCode.BadGateway
                    or HttpStatusCode.ServiceUnavailable
                    or HttpStatusCode.GatewayTimeout;

            if (!transient || attempt == 4)
            {
                return response;
            }

            var delay = ResolveRetryDelay(response, attempt);
            _logger.LogWarning(
                "Mangadex transient HTTP {StatusCode} for {Path}; retrying in {DelayMs}ms (attempt {Attempt}/4)",
                statusCode,
                path,
                (int)delay.TotalMilliseconds,
                attempt);

            response.Dispose();
            await Task.Delay(delay, cancellationToken);
        }

        throw new InvalidOperationException("Unreachable retry path.");
    }

    private async Task WaitForRequestSlotAsync(CancellationToken cancellationToken)
    {
        await RequestGate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var elapsed = now - _lastRequestStartedUtc;
            if (elapsed < MinRequestSpacing)
            {
                await Task.Delay(MinRequestSpacing - elapsed, cancellationToken);
            }

            _lastRequestStartedUtc = DateTimeOffset.UtcNow;
        }
        finally
        {
            RequestGate.Release();
        }
    }

    private static TimeSpan ResolveRetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return delta;
        }

        return TimeSpan.FromMilliseconds(400 * attempt);
    }

    private static string? GetTitle(JsonElement item)
    {
        var attributes = PluginJsonElement.GetObject(item, "attributes");
        if (attributes is null)
        {
            return null;
        }

        var titleMap = PluginJsonElement.GetObject(attributes.Value, "title");
        return PluginJsonElement.PickMapString(titleMap);
    }

    private static string? GetDescription(JsonElement item)
    {
        var attributes = PluginJsonElement.GetObject(item, "attributes");
        if (attributes is null)
        {
            return null;
        }

        var descriptionMap = PluginJsonElement.GetObject(attributes.Value, "description");
        return PluginJsonElement.PickMapString(descriptionMap);
    }

    private static string? BuildThumbnailUrl(JsonElement item)
    {
        var mangaId = PluginJsonElement.GetString(item, "id");
        if (string.IsNullOrWhiteSpace(mangaId))
        {
            return null;
        }

        var relationships = PluginJsonElement.GetArray(item, "relationships");
        if (relationships is null)
        {
            return null;
        }

        foreach (var relation in relationships.Value.EnumerateArray())
        {
            var relationType = PluginJsonElement.GetString(relation, "type");
            if (!string.Equals(relationType, "cover_art", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var attributes = PluginJsonElement.GetObject(relation, "attributes");
            if (attributes is null)
            {
                continue;
            }

            var fileName = PluginJsonElement.GetString(attributes.Value, "fileName");
            if (string.IsNullOrWhiteSpace(fileName))
            {
                continue;
            }

            return $"https://uploads.mangadex.org/covers/{mangaId}/{fileName}";
        }

        return null;
    }

}
