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

    /// <summary>
    /// WASM Dispatch Table Pattern
    /// 
    /// This dictionary maps operation names to their type-safe handler delegates.
    /// The PluginInvokeHelper uses this table to dispatch CLI calls to the appropriate
    /// operation method with automatic argument marshaling and type checking.
    /// 
    /// Pattern: Each key is an operation name (e.g., "search"), and each value is a
    /// delegate with the correct signature for that operation (Func with specific
    /// parameter and return types).
    /// 
    /// See PluginInvokeHelper.cs for how these delegates are invoked safely.
    /// See WasmPluginOperationHost.cs for operation implementation details.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, Delegate> WasmDispatch = new Dictionary<string, Delegate>(StringComparer.Ordinal)
    {
        [PluginOperationNames.Handshake] = (Func<HandshakeResponse>)(() => OperationHost.Handshake()),
        [PluginOperationNames.Capabilities] = (Func<CapabilityItem[]>)(() => OperationHost.Capabilities()),
        [PluginOperationNames.Search] = (Func<string, string, SearchItem[]>)((query, payloadJson) => OperationHost.Search(query, payloadJson)),
        [PluginOperationNames.Chapters] = (Func<string, string, ChapterItem[]>)((mediaId, payloadJson) => OperationHost.Chapters(mediaId, payloadJson)),
        [PluginOperationNames.Page] = (Func<string, string, uint, string, PageItem?>)((mediaId, chapterId, pageIndex, payloadJson) => OperationHost.Page(mediaId, chapterId, pageIndex, payloadJson)),
        [PluginOperationNames.Pages] = (Func<string, string, uint, uint, string, PageItem[]>)((mediaId, chapterId, startIndex, count, payloadJson) => OperationHost.Pages(mediaId, chapterId, startIndex, count, payloadJson)),
        [PluginOperationNames.Invoke] = (Func<OperationRequest, OperationResult>)(request => OperationHost.Invoke(request))
    };

    public static void Main(string[] args)
    {
        Environment.ExitCode = PluginWasmCliHost.Run(
            args,
            PluginOperationNames.WasmCliKnownOperations,
            OperationHost.ExecuteOperationForCli);
    }

    public static HandshakeResponse handshake() => PluginInvokeHelper.Invoke0<HandshakeResponse>(WasmDispatch, PluginOperationNames.Handshake);

    public static CapabilityItem[] capabilities() => PluginInvokeHelper.Invoke0<CapabilityItem[]>(WasmDispatch, PluginOperationNames.Capabilities);

    public static SearchItem[] search(string query, string payloadJson) => PluginInvokeHelper.Invoke2<string, string, SearchItem[]>(WasmDispatch, PluginOperationNames.Search, query, payloadJson);

    public static ChapterItem[] chapters(string mediaId, string payloadJson) => PluginInvokeHelper.Invoke2<string, string, ChapterItem[]>(WasmDispatch, PluginOperationNames.Chapters, mediaId, payloadJson);

    public static PageItem? page(string mediaId, string chapterId, uint pageIndex, string payloadJson) => PluginInvokeHelper.Invoke4<string, string, uint, string, PageItem?>(WasmDispatch, PluginOperationNames.Page, mediaId, chapterId, pageIndex, payloadJson);

    public static PageItem[] pages(string mediaId, string chapterId, uint startIndex, uint count, string payloadJson) => PluginInvokeHelper.Invoke5<string, string, uint, uint, string, PageItem[]>(WasmDispatch, PluginOperationNames.Pages, mediaId, chapterId, startIndex, count, payloadJson);

    public static OperationResult invoke(OperationRequest request) => PluginInvokeHelper.Invoke1<OperationRequest, OperationResult>(WasmDispatch, PluginOperationNames.Invoke, request);

#endif
}
