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
        var response = new SearchResponse();

        if (!string.IsNullOrWhiteSpace(request.Query)
            && request.Query.Contains("demo", StringComparison.OrdinalIgnoreCase))
        {
            response.Results.AddRange(TestPluginData.SearchResults);
        }

        return Task.FromResult(response);
    }
}
