#if PLUGIN_TRANSPORT_WASM
using System.Diagnostics;
using System.Text.Json;
using EMMA.Plugin.Common;

namespace EMMA.TestPlugin.Infrastructure;

internal sealed class WasmPluginOperationHost
{
    private readonly WasmClient _client = new();
    private readonly PluginOperationDispatcher _invokeDispatcher;
    private readonly IReadOnlyDictionary<string, Func<string[], string, string>> _cliHandlers;

    public WasmPluginOperationHost()
    {
        var host = new PluginWasmHostBuilder()
            .AddCliJson(
                PluginOperationNames.Handshake,
                (_, _) => Handshake(),
                WasmJsonContext.Default.HandshakeResponse)
            .AddCliJson(
                PluginOperationNames.Capabilities,
                (_, _) => Capabilities(),
                WasmJsonContext.Default.CapabilityItemArray)
            .AddCliJson(
                PluginOperationNames.Search,
                (args, payload) => Search(args.Length > 0 ? args[0] : string.Empty, payload),
                WasmJsonContext.Default.SearchItemArray)
            .AddCliJson(
                PluginOperationNames.Chapters,
                (args, payload) => Chapters(args.Length > 0 ? args[0] : string.Empty, payload),
                WasmJsonContext.Default.ChapterItemArray)
            .AddCliHandler(PluginOperationNames.Page, SerializePageForCli)
            .AddCliHandler(PluginOperationNames.Pages, SerializePagesForCli)
            .AddCliHandler(PluginOperationNames.Invoke, SerializeInvokeForCli)
            .AddCliHandler(PluginOperationNames.Benchmark, (args, _) => Benchmark(args))
            .AddCliHandler(PluginOperationNames.BenchmarkNetwork, BenchmarkNetwork)
            .ConfigureInvoke(dispatcher => dispatcher
                .RegisterPagedOperations(
                    search: request =>
                    {
                        var payloadJson = request.payloadJson ?? string.Empty;
                        var searchArgs = PluginSearchQuery.Parse(request.argsJson);
                        return BuildOperationJsonResult(
                            JsonSerializer.Serialize(
                                Search(searchArgs, payloadJson),
                                WasmJsonContext.Default.SearchItemArray));
                    },
                    chapters: request =>
                    {
                        var payloadJson = request.payloadJson ?? string.Empty;
                        return BuildOperationJsonResult(
                            JsonSerializer.Serialize(
                                BuildChapterOperationItems(request.ResolveMediaId(), payloadJson),
                                WasmJsonContext.Default.WasmChapterOperationItemArray));
                    },
                    page: request => InvokeSinglePage(request, request.payloadJson ?? string.Empty),
                    pages: request => InvokePages(request, request.payloadJson ?? string.Empty))
                .Register("benchmark", request =>
                {
                    var iterations = Math.Max(1, PluginJsonArgs.GetInt32(request.argsJson, "iterations") ?? 5000);
                    return BuildOperationJsonResult(Benchmark([iterations.ToString()]));
                })
                .Register("benchmark-network", request =>
                {
                    var query = PluginJsonArgs.GetString(request.argsJson, "query");
                    return BuildOperationJsonResult(BenchmarkNetwork([query], request.payloadJson ?? string.Empty));
                }))
            .Build();

        _invokeDispatcher = host.InvokeDispatcher;
        _cliHandlers = host.CliHandlers;
    }

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

    public SearchItem[] Search(string query, string payloadJson)
    {
        var parsedQuery = PluginSearchQuery.Parse(query, fallbackQuery: query);
        return Search(parsedQuery, payloadJson);
    }

    private SearchItem[] Search(PluginSearchQuery parsedQuery, string payloadJson)
    {

        if (PluginEnvironment.IsDevelopmentMode())
        {
            Console.WriteLine($"[SEARCH] Called with query='{parsedQuery.Query}' (empty={string.IsNullOrWhiteSpace(parsedQuery.Query)})");
        }

        if (string.IsNullOrWhiteSpace(parsedQuery.Query)
            && parsedQuery.Filters.Count == 0
            && parsedQuery.QueryAdditions.Count == 0)
        {
            if (PluginEnvironment.IsDevelopmentMode())
            {
                Console.WriteLine("[SEARCH] Returning empty results because query and filters are empty");
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
            payloadJson = _client.FetchSearchPayload(parsedQuery) ?? string.Empty;
            fetchStopwatch.Stop();
            fetchMs = fetchStopwatch.ElapsedMilliseconds;
            if (PluginEnvironment.IsDevelopmentMode())
            {
                Console.WriteLine($"[SEARCH] Fetched payload in {fetchMs}ms, length={payloadJson.Length}");
            }
        }

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            if (PluginEnvironment.IsDevelopmentMode())
            {
                Console.WriteLine("[SEARCH] Payload is empty after fetch, returning []");
            }

            totalStopwatch.Stop();
            EmitSearchSplitTiming(parsedQuery.Query, payloadJson, fetchMs, 0, 0, 0, payloadWasFetched, totalStopwatch.ElapsedMilliseconds);
            return [];
        }

        if (PluginEnvironment.IsDevelopmentMode())
        {
            Console.WriteLine($"[SEARCH] Parsing payload for query='{parsedQuery.Query}'");
        }

        var parseMapResult = _client.SearchFromPayloadWithTimings(payloadJson);
        if (PluginEnvironment.IsDevelopmentMode())
        {
            Console.WriteLine($"[SEARCH] Parse completed, got {parseMapResult.Results.Count} results");
        }

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

        payloadJson = PluginPayloadResolvers.ResolveProvidedOrFetched(payloadJson, () => _client.FetchChaptersPayload(mediaId));
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

        payloadJson = PluginPayloadResolvers.ResolveProvidedOrFetched(payloadJson, () => _client.FetchAtHomePayload(chapterId));
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

        payloadJson = PluginPayloadResolvers.ResolveProvidedOrFetched(payloadJson, () => _client.FetchAtHomePayload(chapterId));
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return [];
        }

        return [.. _client.GetPagesFromPayload(chapterId, checked((int)startIndex), checked((int)count), payloadJson)];
    }

    public OperationResult Invoke(OperationRequest request)
    {
        var operation = request.NormalizedOperation();
        if (operation == "search" && PluginEnvironment.IsDevelopmentMode())
        {
            var searchArgs = PluginSearchQuery.Parse(request.argsJson);
            Console.WriteLine($"[DEBUG] Invoke search: argsJson={request.argsJson}");
            Console.WriteLine($"[DEBUG] Parsed searchArgs.Query={searchArgs.Query}");
        }

        return _invokeDispatcher.Dispatch(request);
    }

    private IReadOnlyList<WasmChapterOperationItem> BuildChapterOperationItems(string mediaId, string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(mediaId))
        {
            return [];
        }

        payloadJson = PluginPayloadResolvers.ResolveProvidedOrFetched(payloadJson, () => _client.FetchChaptersPayload(mediaId));
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

}
#endif
