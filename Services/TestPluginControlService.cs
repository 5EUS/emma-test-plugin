using EMMA.Contracts.Plugins;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace EMMA.TestPlugin.Services;

/// <summary>
/// Minimal control surface for the test plugin.
/// TODO IMPORTANT: this is a big attack surface
/// </summary>
public sealed class TestPluginControlService(
    ITestPluginRuntime runtime,
    ILogger<TestPluginControlService> logger) : PluginControl.PluginControlBase
{
    private readonly ITestPluginRuntime _runtime = runtime;
    private readonly ILogger<TestPluginControlService> _logger = logger;

    public override Task<HealthResponse> GetHealth(HealthRequest request, ServerCallContext context)
    {
        TestPluginRpcGuard.EnsureActive(context);
        var correlationId = TestPluginRpcGuard.GetCorrelationId(context, request.Context?.CorrelationId);

        _logger.LogInformation("Health request {CorrelationId}", correlationId);

        return _runtime.GetHealthAsync(context.CancellationToken);
    }

    public override Task<CapabilitiesResponse> GetCapabilities(CapabilitiesRequest request, ServerCallContext context)
    {
        TestPluginRpcGuard.EnsureActive(context);
        var correlationId = TestPluginRpcGuard.GetCorrelationId(context, request.Context?.CorrelationId);

        _logger.LogInformation("Capabilities request {CorrelationId}", correlationId);

        return _runtime.GetCapabilitiesAsync(context.CancellationToken);
    }
}
