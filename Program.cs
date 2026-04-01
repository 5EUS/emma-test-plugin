#if PLUGIN_TRANSPORT_ASPNET
using EMMA.Plugin.Common;
using EMMA.Plugin.AspNetCore;
using EMMA.TestPlugin.Infrastructure;
using EMMA.TestPlugin.Services;
using Microsoft.Extensions.DependencyInjection;
#else
using EMMA.Plugin.Common;
using EMMA.TestPlugin.Infrastructure;
#endif

namespace EMMA.TestPlugin;

public static partial class Program
{
#if PLUGIN_TRANSPORT_ASPNET
    private static readonly PluginManifestDefaults ControlDefaults = PluginManifestDefaultsProvider.Load(
        pluginManifestFileName: "EMMA.TestPlugin.plugin.json",
        fallback: new PluginManifestDefaults(
            250,
            512,
            ["api.mangadex.org", "uploads.mangadex.org"],
            []),
        pluginProjectFolderName: "EMMA.TestPlugin");

    public static void Main(string[] args)
    {
        var devMode = PluginEnvironment.IsDevelopmentMode();
        var hostOptions = new PluginAspNetHostOptions(
            DefaultPort: PluginEnvironment.GetPort(args, 5000),
            PortEnvironmentVariables: devMode
                ? ["EMMA_PLUGIN_PORT", "EMMA_TEST_PLUGIN_PORT"]
                : ["EMMA_PLUGIN_PORT"],
            PortArgumentName: devMode ? "--port" : string.Empty,
            RootMessage: "EMMA test plugin is running.");

        PluginBuilder.Create(args, hostOptions)
            .ConfigureServices(services =>
            {
                services.AddHttpClient<AspNetClient>(client =>
                {
                    client.BaseAddress = ProviderHttpProfile.Defaults.BaseUri;
                    client.DefaultRequestHeaders.UserAgent.ParseAdd(ProviderHttpProfile.Defaults.UserAgent);
                    client.DefaultRequestHeaders.Accept.ParseAdd(ProviderHttpProfile.Defaults.AcceptMediaType);
                });
            })
            .UseDefaultControlService(ConfigureDefaultControlService)
                .AddDefaultPagedProviders<AspNetClient>()
                .AddDefaultVideoProvider<AspNetClient>()
            .Run(mapDefaultEndpoints: devMode);
    }

    private static void ConfigureDefaultControlService(PluginSdkControlOptions options)
    {
        options.Message = "EMMA test plugin ready";
        options.CpuBudgetMs = ControlDefaults.CpuBudgetMs;
        options.MemoryMb = ControlDefaults.MemoryMb;
        options.Capabilities.Add("test-plugin");
        options.Capabilities.Add("search");
        options.Capabilities.Add("pages");
        options.Capabilities.Add("video");

        options.Domains.Clear();
        foreach (var domain in ControlDefaults.Domains)
        {
            options.Domains.Add(domain);
        }

        options.Paths.Clear();
        foreach (var path in ControlDefaults.Paths)
        {
            options.Paths.Add(path);
        }
    }

#else
    private static readonly WasmPluginOperationHost OperationHost = new();

    public static void Main(string[] args)
    {
        Environment.ExitCode = PluginWasmCliHost.Run(
            args,
            PluginOperationNames.WasmCliKnownOperations,
            OperationHost.ExecuteOperationForCli);
    }

    public static HandshakeResponse handshake()
    {
        return OperationHost.Handshake();
    }

    public static CapabilityItem[] capabilities()
    {
        return OperationHost.Capabilities();
    }

    public static SearchItem[] search(string query, string payloadJson)
    {
        return OperationHost.Search(query, payloadJson);
    }

    public static ChapterItem[] chapters(string mediaId, string payloadJson)
    {
        return OperationHost.Chapters(mediaId, payloadJson);
    }

    public static PageItem? page(string mediaId, string chapterId, uint pageIndex, string payloadJson)
    {
        return OperationHost.Page(mediaId, chapterId, pageIndex, payloadJson);
    }

    public static PageItem[] pages(string mediaId, string chapterId, uint startIndex, uint count, string payloadJson)
    {
        return OperationHost.Pages(mediaId, chapterId, startIndex, count, payloadJson);
    }

    public static OperationResult invoke(OperationRequest request)
    {
        return OperationHost.Invoke(request);
    }
#endif
}
