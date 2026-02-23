using EMMA.Contracts.Plugins;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace EMMA.TestPlugin.Services;

/// <summary>
/// Minimal page provider stub for testing.
/// </summary>
public sealed class TestPageProviderService(ILogger<TestPageProviderService> logger) : PageProvider.PageProviderBase
{
    private readonly ILogger<TestPageProviderService> _logger = logger;

    public override Task<ChaptersResponse> GetChapters(ChaptersRequest request, ServerCallContext context)
    {
        TestPluginRpcGuard.EnsureActive(context);
        var correlationId = TestPluginRpcGuard.GetCorrelationId(context, request.Context?.CorrelationId);

        _logger.LogInformation(
            "Chapters request {CorrelationId} mediaId={MediaId}",
            correlationId,
            request.MediaId);

        var response = new ChaptersResponse();

        if (string.Equals(request.MediaId, TestPluginData.DemoMediaId, StringComparison.OrdinalIgnoreCase))
        {
            response.Chapters.AddRange(TestPluginData.Chapters);
        }

        return Task.FromResult(response);
    }

    public override Task<PageResponse> GetPage(PageRequest request, ServerCallContext context)
    {
        TestPluginRpcGuard.EnsureActive(context);
        var correlationId = TestPluginRpcGuard.GetCorrelationId(context, request.Context?.CorrelationId);

        _logger.LogInformation(
            "Page request {CorrelationId} mediaId={MediaId} chapterId={ChapterId} index={Index}",
            correlationId,
            request.MediaId,
            request.ChapterId,
            request.Index);

        var response = new PageResponse();

        if (string.Equals(request.MediaId, TestPluginData.DemoMediaId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(request.ChapterId, TestPluginData.DemoChapterId, StringComparison.OrdinalIgnoreCase)
            && request.Index == 0)
        {
            response.Page = TestPluginData.Page;
        }

        return Task.FromResult(response);
    }
}
