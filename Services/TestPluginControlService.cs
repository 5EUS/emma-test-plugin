using EMMA.Contracts.Plugins;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace EMMA.TestPlugin.Services;

/// <summary>
/// Minimal control surface for the test plugin.
/// TODO IMPORTANT: this is a big attack surface
/// </summary>
public sealed class TestPluginControlService(ILogger<TestPluginControlService> logger) : PluginControl.PluginControlBase
{
    private readonly ILogger<TestPluginControlService> _logger = logger;

    public override Task<HealthResponse> GetHealth(HealthRequest request, ServerCallContext context)
    {
        TestPluginRpcGuard.EnsureActive(context);
        var correlationId = TestPluginRpcGuard.GetCorrelationId(context, request.Context?.CorrelationId);

        _logger.LogInformation("Health request {CorrelationId}", correlationId);

        var version = typeof(TestPluginControlService).Assembly.GetName().Version?.ToString() ?? "dev";

        return Task.FromResult(new HealthResponse
        {
            Status = "ok",
            Version = version,
            Message = "EMMA test plugin ready"
        });
    }

    public override Task<CapabilitiesResponse> GetCapabilities(CapabilitiesRequest request, ServerCallContext context)
    {
        TestPluginRpcGuard.EnsureActive(context);
        var correlationId = TestPluginRpcGuard.GetCorrelationId(context, request.Context?.CorrelationId);

        _logger.LogInformation("Capabilities request {CorrelationId}", correlationId);

        var response = new CapabilitiesResponse
        {
            Budgets = new CapabilityBudgets
            {
                CpuBudgetMs = 150,
                MemoryMb = 128
            },
            Permissions = new CapabilityPermissions()
        };

        response.Capabilities.AddRange(new[]
        {
            "health",
            "capabilities",
            "test-plugin",
            "search",
            "pages",
            "video"
        });

        // TODO IMPORTANT: this is a big attack surface
        response.Permissions.Domains.Add("api.mangadex.org");
        response.Permissions.Domains.Add("uploads.mangadex.org");
        response.Permissions.Paths.Add("/plugin-data");

        return Task.FromResult(response);
    }
}
