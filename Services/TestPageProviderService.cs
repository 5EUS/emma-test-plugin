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
        return Task.FromResult(new ChaptersResponse());
    }

    public override Task<PageResponse> GetPage(PageRequest request, ServerCallContext context)
    {
        return Task.FromResult(new PageResponse());
    }
}
