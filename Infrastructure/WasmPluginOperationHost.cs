#if PLUGIN_TRANSPORT_WASM
using System.Diagnostics;
using System.Text.Json;
using EMMA.Plugin.Common;

namespace EMMA.TestPlugin.Infrastructure;

/// <summary>
/// WASM Plugin Operation Host
/// 
/// This class orchestrates all plugin operations for the WASM transport. It implements
/// the three-layer architecture:
/// 
/// 1. TRANSPORT LAYER (this file):
///    - ConfigureDefaultOperations: Handshake, Capabilities (always present)
///    - ConfigurePagedMediaOperations: Search, Chapters, Page, Pages (paged media support)
///    - ConfigureCustomOperations: Plugin-specific operations (Benchmark, BenchmarkNetwork)
///    - ConfigureInvokeDispatcher: Error handling and payload resolution
/// 
/// 2. DOMAIN LAYER (Infrastructure/CoreClient.cs):
///    - Search/Chapters/Page retrieval using the real provider API
///    - Shared across both ASP.NET and WASM transports
/// 
/// 3. SDK HELPERS (EMMA.Plugin.Common):
///    - PluginPayloadResolvers: Payload precedence (provided > fetched > host)
///    - PluginOperationDispatcher: Operation routing and error handling
///    - PluginWasmPagingJsonHelpers: Pagination and serialization
///    - PluginWasmInvokeScaffold: CLI invocation helpers
/// 
/// To understand operation flow: Pick any operation (e.g., Search) and trace:
/// - CLI entry: Program.cs WasmDispatch[...] -> search() method
/// - Handler: OperationHost.Search(query, payload)
/// - Domain: CoreClient.SearchFromPayload(query, payload)
/// - Response: Serialized and returned to CLI
/// 
/// To add a new operation:
/// 1. Implement the domain method in CoreClient.cs
/// 2. Add handler method in this class (below)
/// 3. Register in the appropriate Configure* method
/// 4. Add CLI wrapper in Program.cs WasmDispatch dictionary
/// </summary>
internal sealed class WasmPluginOperationHost
{
    #region Fields

    private readonly WasmClient _client = new();
    private readonly PluginOperationDispatcher _invokeDispatcher;
    private readonly IReadOnlyDictionary<string, Func<string[], string, string>> _cliHandlers;

    #endregion

    #region Construction and Registration

    public WasmPluginOperationHost()
    {
        var builder = new PluginWasmHostBuilder();
        ConfigureDefaultOperations(builder);
        ConfigurePagedMediaOperations(builder);
        ConfigureCustomOperations(builder);
        ConfigureInvokeDispatcher(builder);

        var host = builder.Build();

        _invokeDispatcher = host.InvokeDispatcher;
        _cliHandlers = host.CliHandlers;
    }

    /// <summary>
    /// Configure mandatory plugin operations (Handshake, Capabilities).
    /// These are always present regardless of plugin function.
    /// </summary>
    private void ConfigureDefaultOperations(PluginWasmHostBuilder builder)
    {
        builder
            .AddCliJson(PluginOperationNames.Handshake, (_, _) => Handshake(), WasmJsonContext.Default.HandshakeResponse)
            .AddCliJson(PluginOperationNames.Capabilities, (_, _) => Capabilities(), WasmJsonContext.Default.CapabilityItemArray);
    }

    /// <summary>
    /// Configure paged media operations (Search, Chapters, Page, Pages).
    /// These support the plugin's main content retrieval flow.
    /// </summary>
    private void ConfigurePagedMediaOperations(PluginWasmHostBuilder builder)
    {
        builder
            .AddCliJson(PluginOperationNames.Search, (args, payload) => Search(args.Length > 0 ? args[0] : string.Empty, payload), WasmJsonContext.Default.SearchItemArray)
            .AddCliJson(PluginOperationNames.Chapters, (args, payload) => Chapters(args.Length > 0 ? args[0] : string.Empty, payload), WasmJsonContext.Default.ChapterItemArray)
            .AddCliHandler(PluginOperationNames.Page, SerializePageForCli)
            .AddCliHandler(PluginOperationNames.Pages, SerializePagesForCli)
            .AddCliHandler(PluginOperationNames.Invoke, SerializeInvokeForCli);
    }

    /// <summary>
    /// Configure plugin-specific operations (Benchmark, diagnostic utilities).
    /// These are not required by the host but may be useful for testing.
    /// </summary>
    private void ConfigureCustomOperations(PluginWasmHostBuilder builder)
    {
        builder
            .AddCliHandler(PluginOperationNames.Benchmark, (args, _) => Benchmark(args))
            .AddCliHandler(PluginOperationNames.BenchmarkNetwork, BenchmarkNetwork);
    }

    /// <summary>
    /// Configure operation dispatcher for the Invoke operation.
    /// This handles the generic "invoke with serialized arguments" operation,
    /// which allows dynamic operation calls with automatic payload resolution
    /// and error handling via PluginOperationDispatcher (from SDK helpers).
    /// 
    /// This is the backbone of the SDK's operation routing contract:
    /// - Resolve payload precedence (provided > fetched > host-provided)
    /// - Dispatch to registered handler for the operation
    /// - Catch and normalize errors
    /// - Serialize response for CLI output
    /// </summary>
    private void ConfigureInvokeDispatcher(PluginWasmHostBuilder builder)
    {
        builder.ConfigureInvoke(dispatcher => dispatcher
            .RegisterPagedOperations(
                search: request =>
                {
                    var payloadJson = RequestPayload(request);
                    var searchArgs = PluginSearchQuery.Parse(request.argsJson);
                    return BuildOperationJsonResult(
                        JsonSerializer.Serialize(
                            Search(searchArgs, payloadJson),
                            WasmJsonContext.Default.SearchItemArray));
                },
                chapters: request =>
                {
                    var payloadJson = RequestPayload(request);
                    return BuildOperationJsonResult(
                        JsonSerializer.Serialize(
                            BuildChapterOperationItems(request.ResolveMediaId(), payloadJson),
                            WasmJsonContext.Default.WasmChapterOperationItemArray));
                },
                page: request => InvokeSinglePage(request, RequestPayload(request)),
                pages: request => InvokePages(request, RequestPayload(request)))
            .Register("benchmark", request =>
            {
                var iterations = Math.Max(1, PluginJsonArgs.GetInt32(request.argsJson, "iterations") ?? 5000);
                return BuildOperationJsonResult(Benchmark([iterations.ToString()]));
            })
            .Register("benchmark-network", request =>
            {
                var query = PluginJsonArgs.GetString(request.argsJson, "query");
                return BuildOperationJsonResult(BenchmarkNetwork([query], RequestPayload(request)));
            })
            .Register("enrich-search-metadata", request =>
            {
                var enriched = _client.EnrichSearchItemsWithStatistics(request.argsJson ?? string.Empty).ToArray();
                return BuildOperationJsonResult(
                    JsonSerializer.Serialize(enriched, WasmJsonContext.Default.SearchItemArray));
            }));
    }

    #endregion

    #region CLI Entry Points

    public string ExecuteOperationForCli(string operation, string[] args, string inputPayload)
    {
        return PluginWasmCliOperationDispatcher.Execute(operation, args, inputPayload, _cliHandlers);
    }

    public HandshakeResponse Handshake()
    {
        return new HandshakeResponse("1.0.0", "EMMA wasm component ready");
    }

    public CapabilityItem[] Capabilities()
    {
        return PluginCapabilityProfiles.Create(PluginCapabilityProfile.PagedOnly);
    }

    #endregion

    #region Paged Media Operations

    public SearchItem[] Search(string query, string payloadJson)
    {
        var parsedQuery = PluginSearchQuery.Parse(query, fallbackQuery: query);
        return Search(parsedQuery, payloadJson);
    }

    private SearchItem[] Search(PluginSearchQuery parsedQuery, string payloadJson)
    {
        DevLog($"[SEARCH] Called with query='{parsedQuery.Query}' (empty={string.IsNullOrWhiteSpace(parsedQuery.Query)})");

        if (string.IsNullOrWhiteSpace(parsedQuery.Query)
            && parsedQuery.Filters.Count == 0
            && parsedQuery.QueryAdditions.Count == 0)
        {
            DevLog("[SEARCH] Returning empty results because query and filters are empty");
            return [];
        }

        var totalStopwatch = Stopwatch.StartNew();
        var fetchMs = 0L;
        var payloadWasFetched = false;

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            payloadWasFetched = true;
            var fetchStopwatch = Stopwatch.StartNew();
            payloadJson = PluginPayloadResolvers.ResolveProvidedOrFetched(
                payloadJson,
                () => _client.FetchSearchPayload(parsedQuery));
            fetchStopwatch.Stop();
            fetchMs = fetchStopwatch.ElapsedMilliseconds;
            DevLog($"[SEARCH] Fetched payload in {fetchMs}ms, length={payloadJson.Length}");
        }

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            DevLog("[SEARCH] Payload is empty after fetch, returning []");

            totalStopwatch.Stop();
            EmitSearchSplitTiming(parsedQuery.Query, payloadJson, fetchMs, 0, 0, 0, payloadWasFetched, totalStopwatch.ElapsedMilliseconds);
            return [];
        }

        DevLog($"[SEARCH] Parsing payload for query='{parsedQuery.Query}'");

        var parseMapResult = _client.SearchFromPayloadWithTimings(payloadJson);
        DevLog($"[SEARCH] Parse completed, got {parseMapResult.Results.Count} results");

        totalStopwatch.Stop();

        EmitSearchSplitTiming(
            parsedQuery.Query,
            payloadJson,
            fetchMs,
            parseMapResult.ParseMs,
            parseMapResult.MapMs,
            parseMapResult.Results.Count,
            payloadWasFetched,
            totalStopwatch.ElapsedMilliseconds);

        return [.. parseMapResult.Results];
    }

    public ChapterItem[] Chapters(string mediaId, string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(mediaId))
        {
            return [];
        }

        payloadJson = PluginPayloadResolvers.ResolveProvidedOrFetched(
            payloadJson,
            () => _client.FetchChaptersPayload(mediaId));
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return [];
        }

        return [.. _client.GetChaptersFromPayload(mediaId, payloadJson)];
    }

    public PageItem? Page(string mediaId, string chapterId, uint pageIndex, string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(mediaId) || string.IsNullOrWhiteSpace(chapterId))
        {
            return null;
        }

        payloadJson = PluginPayloadResolvers.ResolveProvidedOrFetched(
            payloadJson,
            () => _client.FetchAtHomePayload(chapterId));
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        return _client.GetPageFromPayload(chapterId, checked((int)pageIndex), payloadJson);
    }

    public PageItem[] Pages(string mediaId, string chapterId, uint startIndex, uint count, string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(mediaId) || string.IsNullOrWhiteSpace(chapterId) || count == 0)
        {
            return [];
        }

        payloadJson = PluginPayloadResolvers.ResolveProvidedOrFetched(
            payloadJson,
            () => _client.FetchAtHomePayload(chapterId));
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return [];
        }

        return [.. _client.GetPagesFromPayload(chapterId, checked((int)startIndex), checked((int)count), payloadJson)];
    }

    #endregion

    #region Generic Invoke and Mapping

    public OperationResult Invoke(OperationRequest request)
    {
        var operation = request.NormalizedOperation();
        if (operation == "search" && PluginEnvironment.IsDevelopmentMode())
        {
            var searchArgs = PluginSearchQuery.Parse(request.argsJson);
            DevLog($"[DEBUG] Invoke search: argsJson={request.argsJson}");
            DevLog($"[DEBUG] Parsed searchArgs.Query={searchArgs.Query}");
        }

        return _invokeDispatcher.Dispatch(request);
    }

    private IReadOnlyList<WasmChapterOperationItem> BuildChapterOperationItems(string mediaId, string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(mediaId))
        {
            return [];
        }

        payloadJson = PluginPayloadResolvers.ResolveProvidedOrFetched(
            payloadJson,
            () => _client.FetchChaptersPayload(mediaId));
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return [];
        }

        var operationItems = _client.GetChapterOperationItemsFromPayload(mediaId, payloadJson);
        if (operationItems.Count == 0)
        {
            return [];
        }

        return PluginWasmPagingJsonHelpers.MapChapterOperationItems(
            operationItems,
            item => new WasmChapterOperationItem(
                item.id,
                item.number,
                item.title,
                [.. item.uploaderGroups ?? []]));
    }

    #endregion

    #region CLI Serialization Helpers

    private string SerializePageForCli(string[] args, string stdinPayload)
    {
        return PluginWasmPagingJsonHelpers.SerializePageForCli(
            args,
            stdinPayload,
            Page,
            WasmJsonContext.Default.PageItem);
    }

    private string SerializePagesForCli(string[] args, string stdinPayload)
    {
        return PluginWasmPagingJsonHelpers.SerializePagesForCli(
            args,
            stdinPayload,
            Pages,
            WasmJsonContext.Default.PageItemArray);
    }

    private string SerializeInvokeForCli(string[] args, string stdinPayload)
    {
        return PluginWasmInvokeScaffold.SerializeInvokeForCli(
            args,
            stdinPayload,
            Invoke,
            WasmJsonContext.Default.OperationResult);
    }

    #endregion

    #region Invoke Response Helpers

    private OperationResult InvokeSinglePage(OperationRequest request, string payloadJson)
    {
        var chapterId = request.ResolveChapterId();
        var pageIndex = request.ResolvePageIndex();
        var pageResult = Page(request.ResolveMediaId(), chapterId, pageIndex, payloadJson);
        var json = pageResult is null
            ? "null"
            : JsonSerializer.Serialize(pageResult, WasmJsonContext.Default.PageItem);

        return BuildOperationJsonResult(json);
    }

    private OperationResult InvokePages(OperationRequest request, string payloadJson)
    {
        var chapterId = request.ResolveChapterId();
        var startIndex = request.ResolveStartIndex();
        var count = request.ResolveCount();
        var pagesResult = Pages(request.ResolveMediaId(), chapterId, startIndex, count, payloadJson);
        var json = JsonSerializer.Serialize(pagesResult, WasmJsonContext.Default.PageItemArray);
        return BuildOperationJsonResult(json);
    }

    private static OperationResult BuildOperationJsonResult(string payloadJson)
    {
        return PluginWasmInvokeScaffold.BuildJsonResult(payloadJson);
    }

    private static string RequestPayload(OperationRequest request)
    {
        return request.payloadJson ?? string.Empty;
    }

    #endregion

    #region Diagnostics and Benchmarks

    private static void DevLog(string message)
    {
        if (PluginEnvironment.IsDevelopmentMode())
        {
            Console.WriteLine(message);
        }
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
            var text = $"bench:{i}:{i * 31 % 97}";
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
            WasmJsonContext.Default.BenchmarkResult);
    }

    private string BenchmarkNetwork(string[] args, string stdinPayload)
    {
        var query = args.Length > 0 ? args[0] : "one piece";
        var parsedQuery = PluginSearchQuery.Parse(query, fallbackQuery: query);
        var payloadJson = PluginPayloadResolvers.ResolveProvidedOrFetched(
            WasmClient.ResolvePayloadContent(stdinPayload),
            () => _client.FetchSearchPayload(parsedQuery));

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
            parsedQuery.Query,
            payloadBytes,
            itemCount,
            stopwatch.ElapsedMilliseconds);

        return JsonSerializer.Serialize(
            result,
            WasmJsonContext.Default.NetworkBenchmarkResult);
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

    #endregion

}
#endif
