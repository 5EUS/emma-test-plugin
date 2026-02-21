using EMMA.Contracts.Plugins;
using Grpc.Core;

namespace EMMA.TestPlugin.Services;

/// <summary>
/// Minimal search provider stub for testing.
/// </summary>
public sealed class TestSearchProviderService : SearchProvider.SearchProviderBase
{
    public override Task<SearchResponse> Search(SearchRequest request, ServerCallContext context)
    {
        return Task.FromResult(new SearchResponse());
    }
}
