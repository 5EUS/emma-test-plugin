#if PLUGIN_TRANSPORT_ASPNET
using EMMA.Plugin.Common;
using EMMA.Plugin.AspNetCore;
using EMMA.TestPlugin.ASPNET;
using EMMA.TestPlugin.Core;
using Microsoft.Extensions.DependencyInjection;
#else
using EMMA.Plugin.Common;
using EMMA.TestPlugin.Core;
using EMMA.TestPlugin.WASM;
#endif

namespace EMMA.TestPlugin;

#if PLUGIN_TRANSPORT_WASM
[PluginWasmExports(
    typeof(WasmPluginOperationHost),
    typeof(WasmJsonContext),
    typeof(WasmChapterOperationItem[]),
    typeof(BenchmarkResult),
    typeof(NetworkBenchmarkResult),
    ExportBridgeNamespace = "LibraryWorld.wit.exports.emma.plugin")]
#endif
public static partial class Program
{
#if PLUGIN_TRANSPORT_ASPNET
    private static readonly PluginSdkManifestDefaultsOptions HostDefaults = new(
        PluginManifestFileName: "EMMA.TestPlugin.plugin.json",
        Fallback: new PluginManifestDefaults(
            250,
            512,
            ["api.mangadex.org", "uploads.mangadex.org"],
            []),
        PluginProjectFolderName: "EMMA.TestPlugin",
        DefaultPort: 5000,
        DevelopmentPortEnvironmentVariables: ["EMMA_PLUGIN_PORT", "EMMA_TEST_PLUGIN_PORT"],
        ProductionPortEnvironmentVariables: ["EMMA_PLUGIN_PORT"],
        DevelopmentPortArgumentName: "--port",
        ProductionPortArgumentName: string.Empty,
        RootMessage: "EMMA test plugin is running.");

    public static void Main(string[] args)
    {
        var devMode = PluginEnvironment.IsDevelopmentMode();
        PluginBuilder.CreateWithDefaults(args, HostDefaults)
            .ConfigureServices(services =>
            {
                services.AddHttpClient<AspNetClient>(client =>
                {
                    client.BaseAddress = ProviderHttpProfile.Defaults.BaseUri;
                    client.DefaultRequestHeaders.UserAgent.ParseAdd(ProviderHttpProfile.Defaults.UserAgent);
                    client.DefaultRequestHeaders.Accept.ParseAdd(ProviderHttpProfile.Defaults.AcceptMediaType);
                });
                services.AddTransient<IPluginSearchMetadataRuntime>(static provider => provider.GetRequiredService<AspNetClient>());
                services.AddTransient<IPluginSearchSuggestionsRuntime>(static provider => provider.GetRequiredService<AspNetClient>());
            })
            .ConfigureDefaultControl(ConfigureDefaultControlService)
                .AddDefaultPagedProviders<AspNetClient>()
            .Run(mapDefaultEndpoints: devMode);
    }

    private static void ConfigureDefaultControlService(PluginSdkControlOptions options)
    {
        options.Message = "EMMA test plugin ready";
        options.Capabilities.Add("test-plugin");
        options.Capabilities.Add("search");
        options.Capabilities.Add("pages");
    }

#else
    public static void Main(string[] args)
    {
        Environment.ExitCode = PluginWasmCliHost.Run(
            args,
            PluginOperationNames.WasmCliKnownOperations,
            OperationHost.ExecuteOperationForCli);
    }

#endif
}
