using EMMA.Contracts.Plugins;
using EMMA.Plugin.AspNetCore;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace EMMA.TestPlugin.Services;

/// <summary>
/// Minimal page provider stub for testing.
/// </summary>
public sealed class TestPageProviderService(
    ITestPluginRuntime runtime,
    ILogger<TestPageProviderService> logger) : PageProvider.PageProviderBase
{
    private readonly ITestPluginRuntime _runtime = runtime;
    private readonly ILogger<TestPageProviderService> _logger = logger;

    public override async Task<ChaptersResponse> GetChapters(ChaptersRequest request, ServerCallContext context)
    {
        var correlationId = PluginRequestContext.GetCorrelationId(context, request.Context?.CorrelationId);

        _logger.LogInformation(
            "Chapters request {CorrelationId} mediaId={MediaId}",
            correlationId,
            request.MediaId);

        var response = new ChaptersResponse();
        var chapters = await _runtime.GetChaptersAsync(request.MediaId, context.CancellationToken);
        response.Chapters.AddRange(chapters);
        return response;
    }

    public override async Task<PageResponse> GetPage(PageRequest request, ServerCallContext context)
    {
        var correlationId = PluginRequestContext.GetCorrelationId(context, request.Context?.CorrelationId);

        _logger.LogInformation(
            "Page request {CorrelationId} mediaId={MediaId} chapterId={ChapterId} index={Index}",
            correlationId,
            request.MediaId,
            request.ChapterId,
            request.Index);

        var response = new PageResponse();
        var page = await _runtime.GetPageAsync(request.ChapterId, request.Index, context.CancellationToken);
        if (page is not null)
        {
            response.Page = page;
        }
        return response;
    }

    public override async Task<PagesResponse> GetPages(PagesRequest request, ServerCallContext context)
    {
        var correlationId = PluginRequestContext.GetCorrelationId(context, request.Context?.CorrelationId);

        _logger.LogInformation(
            "Pages request {CorrelationId} mediaId={MediaId} chapterId={ChapterId} startIndex={StartIndex} count={Count}",
            correlationId,
            request.MediaId,
            request.ChapterId,
            request.StartIndex,
            request.Count);

        var (pages, reachedEnd) = await _runtime.GetPagesAsync(
            request.ChapterId,
            request.StartIndex,
            request.Count,
            context.CancellationToken);

        var response = new PagesResponse
        {
            ReachedEnd = reachedEnd
        };
        response.Pages.AddRange(pages);
        return response;
    }
}
