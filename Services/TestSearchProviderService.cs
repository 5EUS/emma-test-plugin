using EMMA.Contracts.Plugins;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace EMMA.TestPlugin.Services;

/// <summary>
/// Minimal search provider stub for testing.
/// </summary>
public sealed class TestSearchProviderService(
    MangadexClient mangadexClient,
    ILogger<TestSearchProviderService> logger) : SearchProvider.SearchProviderBase
{
    private readonly MangadexClient _mangadexClient = mangadexClient;
    private readonly ILogger<TestSearchProviderService> _logger = logger;

    public override async Task<SearchResponse> Search(SearchRequest request, ServerCallContext context)
    {
        TestPluginRpcGuard.EnsureActive(context);
        var correlationId = TestPluginRpcGuard.GetCorrelationId(context, request.Context?.CorrelationId);

        _logger.LogInformation(
            "Search request {CorrelationId} query={Query}",
            correlationId,
            request.Query);

        var response = new SearchResponse();
        var results = await _mangadexClient.SearchAsync(request.Query ?? string.Empty, context.CancellationToken);
        response.Results.AddRange(results);
        return response;
    }
}
