using EMMA.Contracts.Plugins;
using Grpc.Core;

namespace EMMA.TestPlugin.Services;

/// <summary>
/// Minimal control surface for the test plugin.
/// </summary>
public sealed class TestPluginControlService : PluginControl.PluginControlBase
{
    public override Task<HealthResponse> GetHealth(HealthRequest request, ServerCallContext context)
    {
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

        response.Permissions.Domains.Add("example.com");
        response.Permissions.Paths.Add("/plugin-data");

        return Task.FromResult(response);
    }
}
