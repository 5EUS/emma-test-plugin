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

        try
        {
            var response = new SearchResponse();
            var results = await _runtime.SearchAsync(request.Query ?? string.Empty, context.CancellationToken);
            response.Results.AddRange(results);
            return response;
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw new RpcException(new Status(StatusCode.Cancelled, "Search request was cancelled."));
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Search request {CorrelationId} failed for query={Query}.",
                correlationId,
                request.Query);

            throw new RpcException(
                new Status(
                    StatusCode.Internal,
                    $"Search request failed: {ex.GetType().Name}: {ex.Message}"));
        }
    }
}
