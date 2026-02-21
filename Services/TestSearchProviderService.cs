using EMMA.Contracts.Plugins;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace EMMA.TestPlugin.Services;

/// <summary>
/// Minimal search provider stub for testing.
/// </summary>
public sealed class TestSearchProviderService(ILogger<TestSearchProviderService> logger) : SearchProvider.SearchProviderBase
{
    private readonly ILogger<TestSearchProviderService> _logger = logger;

    public override Task<SearchResponse> Search(SearchRequest request, ServerCallContext context)
    {
        TestPluginRpcGuard.EnsureActive(context);
        var correlationId = TestPluginRpcGuard.GetCorrelationId(context);

        _logger.LogInformation(
            "Search request {CorrelationId} query={Query}",
            correlationId,
            request.Query);

        var response = new SearchResponse();

        if (!string.IsNullOrWhiteSpace(request.Query)
            && request.Query.Contains("demo", StringComparison.OrdinalIgnoreCase))
        {
            response.Results.AddRange(TestPluginData.SearchResults);
        }

        return Task.FromResult(response);
    }
}
