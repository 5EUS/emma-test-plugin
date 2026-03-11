using EMMA.Contracts.Plugins;
using EMMA.Plugin.AspNetCore;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace EMMA.TestPlugin.Services;

/// <summary>
/// Minimal search provider stub for testing.
/// </summary>
public sealed class TestSearchProviderService(
    ITestPluginRuntime runtime,
    ILogger<TestSearchProviderService> logger) : SearchProvider.SearchProviderBase
{
    private readonly ITestPluginRuntime _runtime = runtime;
    private readonly ILogger<TestSearchProviderService> _logger = logger;

    public override async Task<SearchResponse> Search(SearchRequest request, ServerCallContext context)
    {
        var correlationId = PluginRequestContext.GetCorrelationId(context, request.Context?.CorrelationId);

        _logger.LogInformation(
            "Search request {CorrelationId} query={Query}",
            correlationId,
            request.Query);

        var response = new SearchResponse();
        var results = await _runtime.SearchAsync(request.Query ?? string.Empty, context.CancellationToken);
        response.Results.AddRange(results);
        return response;
    }
}
