using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Text.Json;
using EMMA.Plugin.Common;
using EMMA.Plugin.AspNetCore;
using EMMA.Contracts.Plugins;
using EMMA.TestPlugin.Infrastructure;
using Microsoft.Extensions.Logging;

namespace EMMA.TestPlugin.Services;

public sealed class AspNetClient(HttpClient httpClient, ILogger<AspNetClient> logger)
    : IPluginPagedMediaRuntime, IPluginVideoRuntime
{
    private const int ChapterFeedPageSize = 500;
    private const int ChapterFeedMaxPages = 20;
    private const int StatisticsBatchSize = 150;

    private static readonly CoreClient Core = new();
    private readonly HttpClient _httpClient = httpClient;
    private readonly ILogger<AspNetClient> _logger = logger;
    private static readonly ConcurrentDictionary<string, CachedAtHomePayload> AtHomeCache = new();
    private static readonly TimeSpan AtHomeCacheTtl = TimeSpan.FromMinutes(2);
    private static readonly ConcurrentDictionary<string, CachedChapterPages> ChapterPagesCache = new();
    private static readonly TimeSpan ChapterPagesCacheTtl = TimeSpan.FromMinutes(14);
    private static readonly SemaphoreSlim RequestGate = new(1, 1);
    private static readonly TimeSpan MinRequestSpacing = TimeSpan.FromMilliseconds(250);
    private static DateTimeOffset _lastRequestStartedUtc = DateTimeOffset.MinValue;
    private static readonly HttpClient InsecureTlsHttpClient = CreateInsecureTlsHttpClient();

    private readonly record struct CachedAtHomePayload(string PayloadJson, DateTimeOffset FetchedAtUtc);
    private sealed record CachedChapterPages(IReadOnlyList<MediaPage> Pages, DateTimeOffset FetchedAtUtc);

    public async Task<IReadOnlyList<MediaSummary>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var parsedQuery = PluginSearchQuery.Parse(query, fallbackQuery: query);
        var resolvedQuery = await ProviderSearchQueryResolver.ResolveAsync(
            parsedQuery,
            FetchProviderPayloadForResolverAsync,
            cancellationToken);
        var path = ProviderRequestUrls.BuildSearchPath(resolvedQuery);
        if (string.IsNullOrWhiteSpace(path))
        {
            return [];
        }

        using var response = await GetWithPolicyAsync(path, cancellationToken);
        response.EnsureSuccessStatusCode();

        var payloadJson = await response.Content.ReadAsStringAsync(cancellationToken);
        var parsed = Core.SearchFromPayloadWithTimings(payloadJson);
        var metadataById = new Dictionary<string, List<MetadataItem>>(StringComparer.OrdinalIgnoreCase);

        // Include only metadata parsed from the search payload, not statistics.
        // Statistics are loaded on-demand via EnrichMediaSummariesWithStatisticsAsync()
        // to keep search performance fast.
        foreach (var entry in parsed.Results)
        {
            if (entry.metadata is null || entry.metadata.Count == 0)
            {
                continue;
            }

            metadataById[entry.id] = new List<MetadataItem>(entry.metadata);
        }

        var results = PluginTypedExportScaffold.MapList(
            parsed.Results,
            entry => BuildMediaSummary(entry, metadataById));

        _logger.LogInformation("Mangadex search query={Query} results={Count}", query, results.Count);
        return results;
    }

    /// <summary>
    /// Enriches media summaries with statistics metadata on-demand.
    /// This is called after search results are presented to the user, not during search,
    /// to keep search performance subsecond while still providing rich metadata when needed.
    /// </summary>
    /// <param name="mediaSummaries">Media summaries to enrich</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Media summaries with merged statistics metadata</returns>
    public async Task<IReadOnlyList<MediaSummary>> EnrichMediaSummariesWithStatisticsAsync(
        IEnumerable<MediaSummary> mediaSummaries,
        CancellationToken cancellationToken = default)
    {
        var summaries = mediaSummaries?.ToList() ?? [];
        if (summaries.Count == 0)
        {
            return summaries;
        }

        var statisticsById = await FetchStatisticsMetadataAsync(
            summaries.Select(s => s.Id),
            cancellationToken);

        if (statisticsById.Count == 0)
        {
            return summaries;
        }

        var enriched = new List<MediaSummary>(summaries.Count);
        foreach (var summary in summaries)
        {
            if (!statisticsById.TryGetValue(summary.Id, out var statsItems) || statsItems.Count == 0)
            {
                enriched.Add(summary);
                continue;
            }

            var enrichedSummary = new MediaSummary
            {
                Id = summary.Id,
                Source = summary.Source,
                Title = summary.Title,
                MediaType = summary.MediaType,
                ThumbnailUrl = summary.ThumbnailUrl,
                Description = summary.Description
            };

            enrichedSummary.Metadata.AddRange(summary.Metadata);
            foreach (var item in statsItems)
            {
                enrichedSummary.Metadata.Add(new KeyValue { Key = item.key, Value = item.value });
            }

            enriched.Add(enrichedSummary);
        }

        return enriched;
    }

    private static MediaSummary BuildMediaSummary(
        EMMA.Plugin.Common.SearchItem entry,
        IReadOnlyDictionary<string, List<MetadataItem>> metadataById)
    {
        IReadOnlyList<MetadataItem>? metadata = null;
        if (metadataById.TryGetValue(entry.id, out var items))
        {
            metadata = items;
        }

        var result = new MediaSummary{
            Id = entry.id,
            Source = entry.source,
            Title = entry.title,
            MediaType = entry.mediaType,
            ThumbnailUrl = entry.thumbnailUrl ?? string.Empty,
            Description = entry.description ?? string.Empty,
        };

        if (metadata is not null)
        {
            foreach (var item in metadata)
            {
                result.Metadata.Add(new KeyValue{ Key = item.key, Value = item.value });
            }
        }

        return result;
    }

    private async Task<IReadOnlyDictionary<string, List<MetadataItem>>> FetchStatisticsMetadataAsync(
        IEnumerable<string> mangaIds,
        CancellationToken cancellationToken)
    {
        var results = new Dictionary<string, List<MetadataItem>>(StringComparer.OrdinalIgnoreCase);
        var ids = mangaIds
            .Select(id => id.Trim())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ids.Count == 0)
        {
            return results;
        }

        foreach (var batch in ids.Chunk(StatisticsBatchSize))
        {
            var url = ProviderRequestUrls.BuildStatisticsAbsoluteUrl(batch);
            if (string.IsNullOrWhiteSpace(url))
            {
                foreach (var id in batch)
                {
                    await TryFetchSingleStatisticsAsync(id, results, cancellationToken);
                }

                continue;
            }

            var returnedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using var response = await GetWithPolicyAsync(url, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var payloadJson = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!string.IsNullOrWhiteSpace(payloadJson))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(payloadJson);
                        foreach (var (id, items) in PayloadMapper.ExtractStatisticsMetadata(doc.RootElement))
                        {
                            if (items.Count == 0)
                            {
                                continue;
                            }

                            returnedIds.Add(id);
                            if (!results.TryGetValue(id, out var existing))
                            {
                                results[id] = new List<MetadataItem>(items);
                                continue;
                            }

                            existing.AddRange(items);
                        }
                    }
                    catch
                    {
                        // ignore malformed batch payload and try per-id fallback below
                    }

                    foreach (var id in batch)
                    {
                        if (returnedIds.Contains(id))
                        {
                            continue;
                        }

                        if (TryExtractRatingMetadataForId(payloadJson, id, out var ratingItem))
                        {
                            returnedIds.Add(id);
                            if (!results.TryGetValue(id, out var existing))
                            {
                                results[id] = [ratingItem];
                            }
                            else
                            {
                                existing.Add(ratingItem);
                            }
                        }
                    }
                }
            }

            foreach (var id in batch)
            {
                if (returnedIds.Contains(id))
                {
                    continue;
                }

                await TryFetchSingleStatisticsAsync(id, results, cancellationToken);
            }
        }

        return results;
    }

    private async Task TryFetchSingleStatisticsAsync(
        string mangaId,
        IDictionary<string, List<MetadataItem>> results,
        CancellationToken cancellationToken)
    {
        var singleUrl = ProviderRequestUrls.BuildStatisticsAbsoluteUrl(mangaId);
        if (string.IsNullOrWhiteSpace(singleUrl))
        {
            return;
        }

        using var singleResponse = await GetWithPolicyAsync(singleUrl, cancellationToken);
        if (!singleResponse.IsSuccessStatusCode)
        {
            return;
        }

        var singlePayload = await singleResponse.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(singlePayload))
        {
            return;
        }

        try
        {
            using var singleDoc = JsonDocument.Parse(singlePayload);
            foreach (var (singleId, items) in PayloadMapper.ExtractStatisticsMetadata(singleDoc.RootElement))
            {
                if (items.Count == 0)
                {
                    continue;
                }

                if (!results.TryGetValue(singleId, out var existing))
                {
                    results[singleId] = new List<MetadataItem>(items);
                    continue;
                }

                existing.AddRange(items);
            }

            if (TryExtractRatingMetadataForId(singlePayload, mangaId, out var ratingItem))
            {
                if (!results.TryGetValue(mangaId, out var existing))
                {
                    results[mangaId] = [ratingItem];
                }
                else
                {
                    existing.Add(ratingItem);
                }
            }
        }
        catch
        {
        }
    }

    private static bool TryExtractRatingMetadataForId(string payloadJson, string mangaId, out MetadataItem ratingItem)
    {
        ratingItem = default!;
        if (string.IsNullOrWhiteSpace(payloadJson) || string.IsNullOrWhiteSpace(mangaId))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("statistics", out var statistics)
                || statistics.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!statistics.TryGetProperty(mangaId, out var item)
                || item.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!item.TryGetProperty("rating", out var rating)
                || rating.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            string? score = null;
            if (rating.TryGetProperty("bayesian", out var bayesian)
                && (bayesian.ValueKind == JsonValueKind.Number || bayesian.ValueKind == JsonValueKind.String))
            {
                score = bayesian.ToString().Trim();
            }

            if (string.IsNullOrWhiteSpace(score)
                && rating.TryGetProperty("average", out var average)
                && (average.ValueKind == JsonValueKind.Number || average.ValueKind == JsonValueKind.String))
            {
                score = average.ToString().Trim();
            }

            if (string.IsNullOrWhiteSpace(score))
            {
                return false;
            }

            ratingItem = new MetadataItem("Rating", score);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void MergeMetadata(
        IDictionary<string, List<MetadataItem>> target,
        IReadOnlyDictionary<string, List<MetadataItem>> source)
    {
        foreach (var (id, items) in source)
        {
            if (items.Count == 0)
            {
                continue;
            }

            if (!target.TryGetValue(id, out var existing))
            {
                target[id] = new List<MetadataItem>(items);
                continue;
            }

            existing.AddRange(items);
        }
    }

    private async Task<string?> FetchProviderPayloadForResolverAsync(
        string absoluteUrl,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(absoluteUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var pathAndQuery = string.Concat(uri.AbsolutePath, uri.Query);
        using var response = await GetWithPolicyAsync(pathAndQuery, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MediaChapter>> GetChaptersAsync(string mediaId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(mediaId))
        {
            return [];
        }

        var entries = await FetchAllChapterEntriesAsync(mediaId, cancellationToken);
        var results = PluginTypedExportScaffold.MapList(
            entries,
            entry =>
            {
                var chapter = new MediaChapter
                {
                    Id = entry.id,
                    Number = entry.number,
                    Title = entry.title
                };
                chapter.UploaderGroups.AddRange(entry.uploaderGroups ?? []);
                return chapter;
            });

        _logger.LogInformation("Mangadex chapters mediaId={MediaId} count={Count}", mediaId, results.Count);
        return results;
    }

    private async Task<IReadOnlyList<ChapterItem>> FetchAllChapterEntriesAsync(
        string mediaId,
        CancellationToken cancellationToken)
    {
        var allEntries = new List<ChapterItem>();
        var seenChapterIds = new HashSet<string>(StringComparer.Ordinal);
        var offset = 0;

        for (var page = 0; page < ChapterFeedMaxPages; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var path = ProviderRequestUrls.BuildChaptersPath(mediaId, limit: ChapterFeedPageSize, offset: offset);
            if (string.IsNullOrWhiteSpace(path))
            {
                break;
            }

            using var response = await GetWithPolicyAsync(path, cancellationToken);
            response.EnsureSuccessStatusCode();

            var payloadJson = await response.Content.ReadAsStringAsync(cancellationToken);
            var entries = Core.GetChaptersFromPayload(payloadJson);

            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.id) || !seenChapterIds.Add(entry.id))
                {
                    continue;
                }

                allEntries.Add(entry);
            }

            if (!TryGetChapterFeedPageStats(payloadJson, out var stats) || stats.DataCount <= 0)
            {
                break;
            }

            var nextOffset = stats.Offset + stats.DataCount;
            if (nextOffset >= stats.Total)
            {
                break;
            }

            offset = nextOffset;
        }

        return allEntries;
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

    public Task<StreamResponse> GetStreamsAsync(string mediaId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new StreamResponse());
    }

    public Task<SegmentResponse> GetSegmentAsync(string mediaId, string streamId, int sequence, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new SegmentResponse());
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

        var pageItems = Core.GetPagesFromPayload(chapterId, 0, int.MaxValue, payloadJson);
        if (pageItems.Count == 0)
        {
            return [];
        }

        var pages = new List<MediaPage>(pageItems.Count);
        foreach (var item in pageItems)
        {
            pages.Add(new MediaPage
            {
                Id = item.id,
                Index = item.index,
                ContentUri = item.contentUri
            });
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

        var path = ProviderRequestUrls.BuildAtHomePath(chapterId);
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

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
        var insecureTlsFallbackAttempted = false;

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            await WaitForRequestSlotAsync(cancellationToken);

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.GetAsync(path, cancellationToken);
            }
            catch (HttpRequestException ex) when (!insecureTlsFallbackAttempted && IsTlsHandshakeFailure(ex))
            {
                insecureTlsFallbackAttempted = true;

                _logger.LogWarning(
                    ex,
                    "TLS validation failed when calling Mangadex. Retrying with insecure TLS fallback for local development environment.");

                response = await InsecureTlsHttpClient.GetAsync(path, cancellationToken);
            }

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

    private static bool IsTlsHandshakeFailure(HttpRequestException ex)
    {
        if (ex.Message.Contains("SSL connection could not be established", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return ex.InnerException is AuthenticationException
            or IOException;
    }

    private static HttpClient CreateInsecureTlsHttpClient()
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = static (_, _, _, _) => true
        };

        var client = new HttpClient(handler)
        {
            BaseAddress = ProviderHttpProfile.Defaults.BaseUri
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(ProviderHttpProfile.Defaults.UserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd(ProviderHttpProfile.Defaults.AcceptMediaType);
        return client;
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

    private static bool TryGetChapterFeedPageStats(string payloadJson, out ChapterFeedPageStats stats)
    {
        stats = default;

        var normalized = CoreClient.ResolvePayloadContent(payloadJson);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(normalized);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var dataCount = PluginJsonElement.GetArray(root, "data")?.GetArrayLength() ?? 0;
            var total = PluginJsonElement.GetInt32(root, "total") ?? dataCount;
            var offset = PluginJsonElement.GetInt32(root, "offset") ?? 0;

            stats = new ChapterFeedPageStats(dataCount, total, offset);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private readonly record struct ChapterFeedPageStats(int DataCount, int Total, int Offset);
}
