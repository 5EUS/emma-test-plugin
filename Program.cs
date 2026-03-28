#if PLUGIN_TRANSPORT_ASPNET
using EMMA.Plugin.Common;
using EMMA.Plugin.AspNetCore;
using EMMA.TestPlugin.Infrastructure;
using EMMA.TestPlugin.Services;
using Microsoft.Extensions.DependencyInjection;
#else
using EMMA.Plugin.Common;
using EMMA.TestPlugin.Infrastructure;
using System.Text.Json;
using System.Diagnostics;
using System.Text.Json.Serialization;
#endif

namespace EMMA.TestPlugin;

public static partial class Program
{
#if PLUGIN_TRANSPORT_ASPNET
    private static readonly PluginManifestDefaults ControlDefaults = PluginManifestDefaultsProvider.Load();

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
                services.AddHttpClient<MangadexClient>(client =>
                {
                    client.BaseAddress = new Uri("https://api.mangadex.org");
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("EMMA-TestPlugin/1.0");
                    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
                });
                services.AddScoped<ITestPluginRuntime, TestPluginRuntime>();
            })
            .UseDefaultControlService(options =>
            {
                options.Message = "EMMA test plugin ready";
                options.CpuBudgetMs = ControlDefaults.CpuBudgetMs;
                options.MemoryMb = ControlDefaults.MemoryMb;
                options.Capabilities.Add("test-plugin");
                options.Capabilities.Add("search");
                options.Capabilities.Add("pages");
                options.Capabilities.Add("video");
                options.Domains.Clear();
                options.Paths.Clear();
                foreach (var domain in ControlDefaults.Domains)
                {
                    options.Domains.Add(domain);
                }

                foreach (var path in ControlDefaults.Paths)
                {
                    options.Paths.Add(path);
                }
            })
            .AddSearchProvider<TestSearchProviderService>()
            .AddPageProvider<TestPageProviderService>()
            .AddVideoProvider<TestVideoProviderService>()
            .Run(mapDefaultEndpoints: devMode);
    }

#else
    private static readonly WasmMangadexClient Mangadex = new();

    public static void Main(string[] args)
    {
        var (operation, operationArgs) = PluginCliOperations.NormalizeOperationArgs(args, PluginOperationNames.WasmCliKnownOperations);
        var inputPayload = PluginPayload.ReadInputPayload();
        PluginPayload.EmitPayloadDiagnostics(operation, inputPayload);
        var json = ExecuteOperationForCli(operation, operationArgs, inputPayload);

        if (string.IsNullOrWhiteSpace(json))
        {
            Environment.ExitCode = 2;
            Console.Error.WriteLine("Unsupported or invalid operation.");
            return;
        }

        Console.WriteLine(json);
    }

    private static string ExecuteOperationForCli(string operation, string[] args, string inputPayload)
    {
        try
        {
            return operation switch
            {
                "handshake" => JsonSerializer.Serialize(handshake(), TestPluginWasmJsonContext.Default.HandshakeResponse),
                "capabilities" => JsonSerializer.Serialize(capabilities(), TestPluginWasmJsonContext.Default.CapabilityItemArray),
                "search" => JsonSerializer.Serialize(search(args.Length > 0 ? args[0] : string.Empty, inputPayload), TestPluginWasmJsonContext.Default.SearchItemArray),
                "chapters" => JsonSerializer.Serialize(chapters(args.Length > 0 ? args[0] : string.Empty, inputPayload), TestPluginWasmJsonContext.Default.ChapterItemArray),
                "page" => SerializePageForCli(args, inputPayload),
                "pages" => SerializePagesForCli(args, inputPayload),
                "invoke" => SerializeInvokeForCli(args, inputPayload),
                "benchmark" => Benchmark(args),
                "benchmark-network" => BenchmarkNetwork(args, inputPayload),
                _ => string.Empty
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"WASM operation '{operation}' failed: {ex}");
            return string.Empty;
        }
    }

    public static HandshakeResponse handshake()
    {
        return new HandshakeResponse("1.0.0", "EMMA test wasm component ready");
    }

    public static CapabilityItem[] capabilities()
    {
        return
        [
            new CapabilityItem(
                "health",
                ["paged", "video", "audio"],
                ["handshake", "capabilities", "search", "invoke"]),
            new CapabilityItem(
                "search",
                ["paged", "video", "audio"],
                ["search", "invoke"]),
            new CapabilityItem(
                "paged-navigation",
                ["paged"],
                ["chapters", "page", "pages", "invoke"]),
            new CapabilityItem(
                "media-operation",
                ["paged", "video", "audio"],
                ["invoke"])
        ];
    }

    public static SearchItem[] search(string query, string payloadJson)
    {
        if (PluginEnvironment.IsDevelopmentMode())
        {
            System.Console.WriteLine($"[SEARCH] Called with query='{query}' (empty={string.IsNullOrWhiteSpace(query)})");
        }
        
        if (string.IsNullOrWhiteSpace(query))
        {
            if (PluginEnvironment.IsDevelopmentMode())
            {
                System.Console.WriteLine($"[SEARCH] Returning empty results because query is null/whitespace");
            }
            return [];
        }

        var totalStopwatch = Stopwatch.StartNew();
        var fetchMs = 0L;
        var payloadWasFetched = false;

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            payloadWasFetched = true;
            var fetchStopwatch = Stopwatch.StartNew();
            payloadJson = Mangadex.FetchSearchPayload(query) ?? string.Empty;
            fetchStopwatch.Stop();
            fetchMs = fetchStopwatch.ElapsedMilliseconds;
            if (PluginEnvironment.IsDevelopmentMode())
            {
                System.Console.WriteLine($"[SEARCH] Fetched payload in {fetchMs}ms, length={payloadJson.Length}");
            }
        }

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            if (PluginEnvironment.IsDevelopmentMode())
            {
                System.Console.WriteLine($"[SEARCH] Payload is empty after fetch, returning []");
            }
            totalStopwatch.Stop();
            EmitSearchSplitTiming(query, payloadJson, fetchMs, 0, 0, 0, payloadWasFetched, totalStopwatch.ElapsedMilliseconds);
            return [];
        }

        if (PluginEnvironment.IsDevelopmentMode())
        {
            System.Console.WriteLine($"[SEARCH] Parsing payload for query='{query}'");
        }
        var parseMapResult = Mangadex.SearchFromPayloadWithTimings(query, payloadJson);
        if (PluginEnvironment.IsDevelopmentMode())
        {
            System.Console.WriteLine($"[SEARCH] Parse completed, got {parseMapResult.Results.Count} results");
        }
        totalStopwatch.Stop();

        EmitSearchSplitTiming(
            query,
            payloadJson,
            fetchMs,
            parseMapResult.ParseMs,
            parseMapResult.MapMs,
            parseMapResult.Results.Count,
            payloadWasFetched,
            totalStopwatch.ElapsedMilliseconds);

        return [.. parseMapResult.Results];
    }

    public static ChapterItem[] chapters(string mediaId, string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(mediaId))
        {
            return [];
        }

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            payloadJson = Mangadex.FetchChaptersPayload(mediaId) ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return [];
        }

        return [.. Mangadex.GetChaptersFromPayload(mediaId, payloadJson)];
    }

    public static PageItem? page(string mediaId, string chapterId, uint pageIndex, string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(mediaId) || string.IsNullOrWhiteSpace(chapterId))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            payloadJson = Mangadex.FetchAtHomePayload(chapterId) ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        return Mangadex.GetPageFromPayload(chapterId, checked((int)pageIndex), payloadJson);
    }

    public static PageItem[] pages(string mediaId, string chapterId, uint startIndex, uint count, string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(mediaId) || string.IsNullOrWhiteSpace(chapterId) || count == 0)
        {
            return [];
        }

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            payloadJson = Mangadex.FetchAtHomePayload(chapterId) ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return [];
        }

        return [.. Mangadex.GetPagesFromPayload(chapterId, checked((int)startIndex), checked((int)count), payloadJson)];
    }

    public static OperationResult invoke(OperationRequest request)
    {
        var operation = request.operation?.Trim().ToLowerInvariant() ?? string.Empty;
        var mediaType = request.mediaType?.Trim().ToLowerInvariant();
        var payloadJson = request.payloadJson ?? string.Empty;
        var searchArgs = PluginSearchQuery.Parse(request.argsJson);

        if (operation == "search" && PluginEnvironment.IsDevelopmentMode())
        {
            System.Console.WriteLine($"[DEBUG] Invoke search: argsJson={request.argsJson}");
            System.Console.WriteLine($"[DEBUG] Parsed searchArgs.Query={searchArgs.Query}");
        }

        try
        {
            return operation switch
            {
                "search" => BuildOperationJsonResult(
                    JsonSerializer.Serialize(
                        search(searchArgs.Query, payloadJson),
                        TestPluginWasmJsonContext.Default.SearchItemArray)),
                "chapters" when string.Equals(mediaType, "paged", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(mediaType) => BuildOperationJsonResult(
                    JsonSerializer.Serialize(
                        BuildChapterOperationItems(request.mediaId ?? PluginJsonArgs.GetString(request.argsJson, "mediaId"), payloadJson),
                        TestPluginWasmJsonContext.Default.WasmChapterOperationItemArray)),
                "page" when string.Equals(mediaType, "paged", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(mediaType) => InvokeSinglePage(request, payloadJson),
                "pages" when string.Equals(mediaType, "paged", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(mediaType) => InvokePages(request, payloadJson),
                "benchmark" => BuildOperationJsonResult(
                    Benchmark([Math.Max(1, PluginJsonArgs.GetInt32(request.argsJson, "iterations") ?? 5000).ToString()])),
                "benchmark-network" => BuildOperationJsonResult(
                    BenchmarkNetwork(
                        [PluginJsonArgs.GetString(request.argsJson, "query")],
                        payloadJson)),
                _ => OperationResult.UnsupportedOperation(operation)
            };
        }
        catch (Exception ex)
        {
            return OperationResult.Failed(ex.Message);
        }
    }

    private static IReadOnlyList<WasmChapterOperationItem> BuildChapterOperationItems(string mediaId, string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(mediaId))
        {
            return [];
        }

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            payloadJson = Mangadex.FetchChaptersPayload(mediaId) ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return [];
        }

        var operationItems = Mangadex.GetChapterOperationItemsFromPayload(mediaId, payloadJson);
        if (operationItems.Count == 0)
        {
            return [];
        }

        var result = new List<WasmChapterOperationItem>(operationItems.Count);
        foreach (var item in operationItems)
        {
            result.Add(new WasmChapterOperationItem(
                item.id,
                item.number,
                item.title,
                [.. item.uploaderGroups ?? []]));
        }

        return result;
    }

    private static string SerializePageForCli(string[] args, string stdinPayload)
    {
        if (args.Length < 3)
        {
            return string.Empty;
        }

        var mediaId = args[0];
        var chapterId = args[1];
        if (!uint.TryParse(args[2], out var pageIndex))
        {
            return string.Empty;
        }

        var result = page(mediaId, chapterId, pageIndex, stdinPayload);
        if (result is null)
        {
            return "null";
        }

        return JsonSerializer.Serialize(result, TestPluginWasmJsonContext.Default.PageItem);
    }

    private static string SerializePagesForCli(string[] args, string stdinPayload)
    {
        if (args.Length < 4)
        {
            return string.Empty;
        }

        var mediaId = args[0];
        var chapterId = args[1];
        if (!uint.TryParse(args[2], out var startIndex)
            || !uint.TryParse(args[3], out var count)
            || count == 0)
        {
            return string.Empty;
        }

        var results = pages(mediaId, chapterId, startIndex, count, stdinPayload);
        return JsonSerializer.Serialize(results, TestPluginWasmJsonContext.Default.PageItemArray);
    }

    private static string SerializeInvokeForCli(string[] args, string stdinPayload)
    {
        if (args.Length == 0)
        {
            return JsonSerializer.Serialize(
                OperationResult.InvalidArguments("missing operation"),
                TestPluginWasmJsonContext.Default.OperationResult);
        }

        var request = new OperationRequest(
            args[0],
            args.Length > 1 ? args[1] : null,
            args.Length > 2 ? args[2] : null,
            args.Length > 3 ? args[3] : null,
            stdinPayload);

        var result = invoke(request);
        return JsonSerializer.Serialize(result, TestPluginWasmJsonContext.Default.OperationResult);
    }

    private static OperationResult InvokeSinglePage(OperationRequest request, string payloadJson)
    {
        var chapterId = PluginJsonArgs.GetString(request.argsJson, "chapterId");
        var pageIndex = PluginJsonArgs.GetUInt32(request.argsJson, "pageIndex") ?? 0;
        var pageResult = page(request.mediaId ?? PluginJsonArgs.GetString(request.argsJson, "mediaId"), chapterId, pageIndex, payloadJson);
        var json = pageResult is null
            ? "null"
            : JsonSerializer.Serialize(pageResult, TestPluginWasmJsonContext.Default.PageItem);

        return BuildOperationJsonResult(json);
    }

    private static OperationResult InvokePages(OperationRequest request, string payloadJson)
    {
        var chapterId = PluginJsonArgs.GetString(request.argsJson, "chapterId");
        var startIndex = PluginJsonArgs.GetUInt32(request.argsJson, "startIndex") ?? 0;
        var count = PluginJsonArgs.GetUInt32(request.argsJson, "count") ?? 0;
        var pagesResult = pages(request.mediaId ?? PluginJsonArgs.GetString(request.argsJson, "mediaId"), chapterId, startIndex, count, payloadJson);
        var json = JsonSerializer.Serialize(pagesResult, TestPluginWasmJsonContext.Default.PageItemArray);
        return BuildOperationJsonResult(json);
    }

    private static OperationResult BuildOperationJsonResult(string payloadJson)
    {
        return new OperationResult(false, null, "application/json", payloadJson);
    }

    private static string Benchmark(string[] args)
    {
        var iterations = 5000;
        if (args.Length > 0 && int.TryParse(args[0], out var parsed) && parsed > 0)
        {
            iterations = Math.Clamp(parsed, 1, 1_000_000);
        }

        var stopwatch = Stopwatch.StartNew();
        long checksum = 1469598103934665603;
        const ulong prime = 1099511628211;
        var generated = 0;

        for (var i = 0; i < iterations; i++)
        {
            var text = $"bench:{i}:{(i * 31) % 97}";
            foreach (var rune in text.EnumerateRunes())
            {
                checksum ^= rune.Value;
                checksum = (long)((ulong)checksum * prime);
            }

            generated += text.Length;
        }

        stopwatch.Stop();

        var result = new BenchmarkResult(
            iterations,
            checksum,
            generated,
            stopwatch.ElapsedMilliseconds);

        return JsonSerializer.Serialize(
            result,
            TestPluginWasmJsonContext.Default.BenchmarkResult);
    }

    private static string BenchmarkNetwork(string[] args, string stdinPayload)
    {
        var query = args.Length > 0 ? args[0] : "one piece";
        var payloadJson = stdinPayload;
        payloadJson = WasmMangadexClient.ResolvePayloadContent(payloadJson);
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            payloadJson = Mangadex.FetchSearchPayload(query) ?? string.Empty;
        }

        var stopwatch = Stopwatch.StartNew();
        var payloadBytes = System.Text.Encoding.UTF8.GetByteCount(payloadJson ?? string.Empty);
        var itemCount = 0;

        if (!string.IsNullOrWhiteSpace(payloadJson))
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var data = PluginJsonElement.GetArray(doc.RootElement, "data");
            itemCount = data?.GetArrayLength() ?? 0;
        }

        stopwatch.Stop();

        var result = new NetworkBenchmarkResult(
            query,
            payloadBytes,
            itemCount,
            stopwatch.ElapsedMilliseconds);

        return JsonSerializer.Serialize(
            result,
            TestPluginWasmJsonContext.Default.NetworkBenchmarkResult);
    }

    private static void EmitSearchSplitTiming(
        string query,
        string payload,
        long fetchMs,
        long parseMs,
        long mapMs,
        int resultCount,
        bool payloadWasFetched,
        long totalMs)
    {
        if (!ShouldLogPluginTimingDiagnostics())
        {
            return;
        }

        var payloadBytes = System.Text.Encoding.UTF8.GetByteCount(payload ?? string.Empty);
        Console.Error.WriteLine(
            "[TEMP_TIMING_REMOVE] pluginSearch op=search queryLength={0} payloadSource={1} fetchMs={2} parseMs={3} mapMs={4} totalMs={5} payloadBytes={6} resultCount={7}",
            query?.Length ?? 0,
            payloadWasFetched ? "provider" : "provided",
            fetchMs,
            parseMs,
            mapMs,
            totalMs,
            payloadBytes,
            resultCount);
    }

    private static bool ShouldLogPluginTimingDiagnostics()
    {
        return PluginEnvironmentFlags.IsEnabled(Environment.GetEnvironmentVariable("EMMA_PLUGIN_TIMING_DIAGNOSTICS"))
            || PluginEnvironmentFlags.IsEnabled(Environment.GetEnvironmentVariable("EMMA_WASM_PAYLOAD_DIAGNOSTICS"));
    }

    [JsonSerializable(typeof(HandshakeResponse))]
    [JsonSerializable(typeof(string[]))]
    [JsonSerializable(typeof(CapabilityItem[]))]
    [JsonSerializable(typeof(SearchItem[]))]
    [JsonSerializable(typeof(ChapterItem[]))]
    [JsonSerializable(typeof(WasmChapterOperationItem[]))]
    [JsonSerializable(typeof(PageItem))]
    [JsonSerializable(typeof(PageItem[]))]
    [JsonSerializable(typeof(OperationResult))]
    [JsonSerializable(typeof(BenchmarkResult))]
    [JsonSerializable(typeof(NetworkBenchmarkResult))]
    private sealed partial class TestPluginWasmJsonContext : JsonSerializerContext
    {
    }

    private sealed record WasmChapterOperationItem(
        string id,
        int number,
        string title,
        string[] uploaderGroups);
#endif
}
