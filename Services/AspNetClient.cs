using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using EMMA.Plugin.Common;
using EMMA.Plugin.AspNetCore;
using EMMA.Contracts.Plugins;
using EMMA.TestPlugin.Infrastructure;
using Microsoft.Extensions.Logging;

namespace EMMA.TestPlugin.Services;

public sealed class AspNetClient(HttpClient httpClient, ILogger<AspNetClient> logger)
    : IPluginPagedMediaRuntime, IPluginVideoRuntime
{
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
        var results = PluginTypedExportScaffold.MapList(
            parsed.Results,
            entry => new MediaSummary
            {
                Id = entry.id,
                Source = entry.source,
                Title = entry.title,
                MediaType = entry.mediaType,
                ThumbnailUrl = entry.thumbnailUrl ?? string.Empty,
                Description = entry.description ?? string.Empty
            });

        _logger.LogInformation("Mangadex search query={Query} results={Count}", query, results.Count);
        return results;
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
        var path = ProviderRequestUrls.BuildChaptersPath(mediaId);
        if (string.IsNullOrWhiteSpace(path))
        {
            return [];
        }

        using var response = await GetWithPolicyAsync(path, cancellationToken);
        response.EnsureSuccessStatusCode();

        var payloadJson = await response.Content.ReadAsStringAsync(cancellationToken);
        var entries = Core.GetChaptersFromPayload(payloadJson);
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
}
