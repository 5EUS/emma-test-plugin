using EMMA.Contracts.Plugins;
using Grpc.Core;

namespace EMMA.TestPlugin.Services;

/// <summary>
/// Minimal page provider stub for testing.
/// </summary>
public sealed class TestPageProviderService : PageProvider.PageProviderBase
{
    public override Task<ChaptersResponse> GetChapters(ChaptersRequest request, ServerCallContext context)
    {
        var response = new ChaptersResponse();

        if (string.Equals(request.MediaId, TestPluginData.DemoMediaId, StringComparison.OrdinalIgnoreCase))
        {
            response.Chapters.AddRange(TestPluginData.Chapters);
        }

        return Task.FromResult(response);
    }

    public override Task<PageResponse> GetPage(PageRequest request, ServerCallContext context)
    {
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
