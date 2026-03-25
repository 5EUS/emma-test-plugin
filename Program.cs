#if PLUGIN_TRANSPORT_ASPNET
using EMMA.Plugin.AspNetCore;
using EMMA.TestPlugin.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
#else
using System.Text.Json;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json.Serialization;
#endif

namespace EMMA.TestPlugin;

public static partial class Program
{
    private const string DevModeEnvVar = "EMMA_PLUGIN_DEV_MODE";

#if PLUGIN_TRANSPORT_ASPNET
    private static readonly PluginManifestDefaults ControlDefaults = LoadManifestDefaults();

    public static void Main(string[] args)
    {
        var devMode = IsDevelopmentMode();
        var hostOptions = new PluginAspNetHostOptions(
            DefaultPort: 5005,
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

    private static bool IsDevelopmentMode()
    {
        var value = Environment.GetEnvironmentVariable(DevModeEnvVar);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (bool.TryParse(value, out var parsedBool))
        {
            return parsedBool;
        }

        return value.Trim() switch
        {
            "1" or "yes" or "on" => true,
            _ => false
        };
    }

    private static PluginManifestDefaults LoadManifestDefaults()
    {
        var fallback = new PluginManifestDefaults(
            250,
            512,
            ["api.mangadex.org", "uploads.mangadex.org"],
            []);

        foreach (var manifestPath in EnumerateManifestCandidates())
        {
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
                var root = doc.RootElement;

                var capabilities = root.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Object
                    ? caps
                    : default;

                var cpu = capabilities.ValueKind == JsonValueKind.Object
                    && capabilities.TryGetProperty("cpuBudgetMs", out var cpuElement)
                    && cpuElement.TryGetInt32(out var parsedCpu)
                        ? parsedCpu
                        : fallback.CpuBudgetMs;

                var memory = capabilities.ValueKind == JsonValueKind.Object
                    && capabilities.TryGetProperty("memoryMb", out var memElement)
                    && memElement.TryGetInt32(out var parsedMem)
                        ? parsedMem
                        : fallback.MemoryMb;

                var permissions = root.TryGetProperty("permissions", out var perms) && perms.ValueKind == JsonValueKind.Object
                    ? perms
                    : default;

                var domains = ReadStringArray(permissions, "domains", fallback.Domains);
                var paths = ReadStringArray(permissions, "paths", fallback.Paths);

                return new PluginManifestDefaults(cpu, memory, domains, paths);
            }
            catch
            {
            }
        }

        return fallback;
    }

    private static IEnumerable<string> EnumerateManifestCandidates()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "EMMA.TestPlugin.plugin.json");
        yield return Path.Combine(Directory.GetCurrentDirectory(), "EMMA.TestPlugin.plugin.json");
        yield return Path.Combine(Directory.GetCurrentDirectory(), "src", "EMMA.TestPlugin", "EMMA.TestPlugin.plugin.json");
    }

    private static string[] ReadStringArray(JsonElement permissions, string propertyName, IReadOnlyList<string> fallback)
    {
        if (permissions.ValueKind != JsonValueKind.Object
            || !permissions.TryGetProperty(propertyName, out var element)
            || element.ValueKind != JsonValueKind.Array)
        {
            return [.. fallback];
        }

        return [.. element.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)];
    }

    private readonly record struct PluginManifestDefaults(
        int CpuBudgetMs,
        int MemoryMb,
        string[] Domains,
        string[] Paths);
#else
    private static readonly WasmMangadexClient Mangadex = CreateMangadexClient();

    public static void Main(string[] args)
    {
        var (operation, operationArgs) = NormalizeOperationArgs(args);
        var inputPayload = ReadInputPayload();
        EmitPayloadDiagnostics(operation, inputPayload);
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
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            payloadJson = Mangadex.FetchSearchPayload(query) ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return [];
        }

        return [.. Mangadex.SearchFromPayload(query, payloadJson)];
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

        try
        {
            return operation switch
            {
                "search" => BuildOperationJsonResult(
                    JsonSerializer.Serialize(
                        search(GetJsonArgString(request.argsJson, "query"), payloadJson),
                        TestPluginWasmJsonContext.Default.SearchItemArray)),
                "chapters" when string.Equals(mediaType, "paged", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(mediaType) => BuildOperationJsonResult(
                    JsonSerializer.Serialize(
                        chapters(request.mediaId ?? GetJsonArgString(request.argsJson, "mediaId"), payloadJson),
                        TestPluginWasmJsonContext.Default.ChapterItemArray)),
                "page" when string.Equals(mediaType, "paged", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(mediaType) => InvokeSinglePage(request, payloadJson),
                "pages" when string.Equals(mediaType, "paged", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(mediaType) => InvokePages(request, payloadJson),
                "benchmark" => BuildOperationJsonResult(
                    Benchmark([Math.Max(1, GetJsonArgInt32(request.argsJson, "iterations") ?? 5000).ToString()])),
                "benchmark-network" => BuildOperationJsonResult(
                    BenchmarkNetwork(
                        [GetJsonArgString(request.argsJson, "query")],
                        payloadJson)),
                _ => OperationResult.Error($"unsupported-operation:{operation}")
            };
        }
        catch (Exception ex)
        {
            return OperationResult.Error($"failed:{ex.Message}");
        }
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
                OperationResult.Error("invalid-arguments:missing operation"),
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

    private static (string Operation, string[] Args) NormalizeOperationArgs(string[] args)
    {
        if (args.Length == 0)
        {
            return (string.Empty, Array.Empty<string>());
        }

        var maxProbe = Math.Min(args.Length, 5);
        for (var index = 0; index < maxProbe; index++)
        {
            var candidate = args[index].ToLowerInvariant();
            if (IsKnownOperation(candidate))
            {
                return (candidate, args.Length > index + 1 ? args[(index + 1)..] : Array.Empty<string>());
            }
        }

        var fallback = args[0].ToLowerInvariant();
        return (fallback, args.Length > 1 ? args[1..] : Array.Empty<string>());
    }

    private static bool IsKnownOperation(string operation)
    {
        return operation is "handshake"
            or "capabilities"
            or "search"
            or "chapters"
            or "page"
            or "pages"
            or "invoke"
            or "benchmark"
            or "benchmark-network";
    }

    private static OperationResult InvokeSinglePage(OperationRequest request, string payloadJson)
    {
        var chapterId = GetJsonArgString(request.argsJson, "chapterId");
        var pageIndex = GetJsonArgUInt(request.argsJson, "pageIndex") ?? 0;
        var pageResult = page(request.mediaId ?? GetJsonArgString(request.argsJson, "mediaId"), chapterId, pageIndex, payloadJson);
        var json = pageResult is null
            ? "null"
            : JsonSerializer.Serialize(pageResult, TestPluginWasmJsonContext.Default.PageItem);

        return BuildOperationJsonResult(json);
    }

    private static OperationResult InvokePages(OperationRequest request, string payloadJson)
    {
        var chapterId = GetJsonArgString(request.argsJson, "chapterId");
        var startIndex = GetJsonArgUInt(request.argsJson, "startIndex") ?? 0;
        var count = GetJsonArgUInt(request.argsJson, "count") ?? 0;
        var pagesResult = pages(request.mediaId ?? GetJsonArgString(request.argsJson, "mediaId"), chapterId, startIndex, count, payloadJson);
        var json = JsonSerializer.Serialize(pagesResult, TestPluginWasmJsonContext.Default.PageItemArray);
        return BuildOperationJsonResult(json);
    }

    private static OperationResult BuildOperationJsonResult(string payloadJson)
    {
        return new OperationResult(false, null, "application/json", payloadJson);
    }

    private static string GetJsonArgString(string? argsJson, string key)
    {
        if (string.IsNullOrWhiteSpace(argsJson))
        {
            return string.Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(key, out var prop)
                && prop.ValueKind == JsonValueKind.String)
            {
                return prop.GetString() ?? string.Empty;
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static uint? GetJsonArgUInt(string? argsJson, string key)
    {
        if (string.IsNullOrWhiteSpace(argsJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty(key, out var prop))
            {
                return null;
            }

            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetUInt32(out var numeric))
            {
                return numeric;
            }

            if (prop.ValueKind == JsonValueKind.String
                && uint.TryParse(prop.GetString(), out var parsed))
            {
                return parsed;
            }
        }
        catch
        {
        }

        return null;
    }

    private static int? GetJsonArgInt32(string? argsJson, string key)
    {
        if (string.IsNullOrWhiteSpace(argsJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty(key, out var prop))
            {
                return null;
            }

            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var numeric))
            {
                return numeric;
            }

            if (prop.ValueKind == JsonValueKind.String
                && int.TryParse(prop.GetString(), out var parsed))
            {
                return parsed;
            }
        }
        catch
        {
        }

        return null;
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
            var data = WasmMangadexClient.GetArray(doc.RootElement, "data");
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

    private static WasmMangadexClient CreateMangadexClient()
    {
        return new WasmMangadexClient();
    }

    private static string NormalizeJsonPayload(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return string.Empty;
        }

        var trimmed = payload.Trim();
        if (trimmed.StartsWith("{") || trimmed.StartsWith("["))
        {
            return trimmed;
        }

        var objectIndex = trimmed.IndexOf('{');
        var arrayIndex = trimmed.IndexOf('[');

        var start = -1;
        if (objectIndex >= 0 && arrayIndex >= 0)
        {
            start = Math.Min(objectIndex, arrayIndex);
        }
        else if (objectIndex >= 0)
        {
            start = objectIndex;
        }
        else if (arrayIndex >= 0)
        {
            start = arrayIndex;
        }

        return start >= 0 ? trimmed[start..] : string.Empty;
    }

    private static string ReadInputPayload()
    {
        try
        {
            return Console.In.ReadToEnd();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void EmitPayloadDiagnostics(string operation, string payload)
    {
        if (!ShouldLogPayloadDiagnostics())
        {
            return;
        }

        var payloadBytes = System.Text.Encoding.UTF8.GetByteCount(payload ?? string.Empty);
        Console.Error.WriteLine($"[TEMP_TIMING_REMOVE] wasmPayload op={operation} source=stdin bytes={payloadBytes}");
    }

    private static bool ShouldLogPayloadDiagnostics()
    {
        var value = Environment.GetEnvironmentVariable("EMMA_WASM_PAYLOAD_DIAGNOSTICS");
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (bool.TryParse(value, out var parsedBool))
        {
            return parsedBool;
        }

        return value.Trim() is "1" or "yes" or "on";
    }

    private sealed class WasmMangadexClient
    {
        private static readonly HttpClient Http = CreateHttpClient();

        public IReadOnlyList<SearchItem> SearchFromPayload(string query, string payloadJson)
        {
            payloadJson = ResolvePayloadContent(payloadJson);

            if (string.IsNullOrWhiteSpace(payloadJson))
            {
                return [];
            }

            using var doc = JsonDocument.Parse(payloadJson);
            var data = GetArray(doc.RootElement, "data");
            if (data is null)
            {
                return [];
            }

            var results = new List<SearchItem>();
            foreach (var item in data.Value.EnumerateArray())
            {
                var id = GetString(item, "id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var title = GetTitle(item);
                if (string.IsNullOrWhiteSpace(title))
                {
                    title = "Untitled";
                }

                var thumbnailUrl = BuildThumbnailUrl(item);
                var description = GetDescription(item);

                results.Add(new SearchItem(
                    id,
                    "mangadex",
                    title,
                    "paged",
                    thumbnailUrl,
                    description));
            }

            return results;
        }

        public IReadOnlyList<ChapterItem> GetChaptersFromPayload(string mediaId, string payloadJson)
        {
            payloadJson = ResolvePayloadContent(payloadJson);

            if (string.IsNullOrWhiteSpace(payloadJson))
            {
                return [];
            }

            using var doc = JsonDocument.Parse(payloadJson);
            var data = GetArray(doc.RootElement, "data");
            if (data is null)
            {
                return [];
            }

            var results = new List<ChapterItem>();
            var index = 0;
            foreach (var item in data.Value.EnumerateArray())
            {
                var id = GetString(item, "id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var attributes = GetObject(item, "attributes");
                var pages = attributes is null ? null : GetInt32(attributes.Value, "pages");
                if (pages is not null && pages <= 0)
                {
                    continue;
                }

                var title = attributes is null ? null : GetString(attributes.Value, "title");
                var chapterText = attributes is null ? null : GetString(attributes.Value, "chapter");
                var number = index + 1;
                if (!string.IsNullOrWhiteSpace(chapterText) && int.TryParse(chapterText, out var parsed))
                {
                    number = parsed;
                }

                if (string.IsNullOrWhiteSpace(title))
                {
                    title = string.IsNullOrWhiteSpace(chapterText)
                        ? $"Chapter {number}"
                        : $"Chapter {chapterText}";
                }

                results.Add(new ChapterItem(id, number, title));
                index++;
            }

            return results;
        }

        public PageItem? GetPageFromPayload(string chapterId, int pageIndex, string payloadJson)
        {
            payloadJson = ResolvePayloadContent(payloadJson);

            if (string.IsNullOrWhiteSpace(chapterId) || pageIndex < 0 || string.IsNullOrWhiteSpace(payloadJson))
            {
                return null;
            }

            if (!TryParseAtHomePayload(payloadJson, out var atHomePayload))
            {
                return null;
            }

            if (pageIndex >= atHomePayload.Files.Count)
            {
                return null;
            }

            var fileName = atHomePayload.Files[pageIndex];
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return null;
            }

            var pageId = $"{chapterId}:{pageIndex}";

            return new PageItem(
                pageId,
                pageIndex,
                $"{atHomePayload.BaseUrl}/{atHomePayload.DataPathSegment}/{atHomePayload.Hash}/{fileName}");
        }

        public IReadOnlyList<PageItem> GetPagesFromPayload(
            string chapterId,
            int startIndex,
            int count,
            string payloadJson)
        {
            payloadJson = ResolvePayloadContent(payloadJson);

            if (string.IsNullOrWhiteSpace(chapterId)
                || startIndex < 0
                || count <= 0
                || string.IsNullOrWhiteSpace(payloadJson))
            {
                return [];
            }

            if (!TryParseAtHomePayload(payloadJson, out var atHomePayload))
            {
                return [];
            }

            if (startIndex >= atHomePayload.Files.Count)
            {
                return [];
            }

            var endExclusive = Math.Min(atHomePayload.Files.Count, startIndex + count);
            var pages = new List<PageItem>(Math.Max(0, endExclusive - startIndex));

            for (var pageIndex = startIndex; pageIndex < endExclusive; pageIndex++)
            {
                var fileName = atHomePayload.Files[pageIndex];
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    continue;
                }

                pages.Add(new PageItem(
                    $"{chapterId}:{pageIndex}",
                    pageIndex,
                    $"{atHomePayload.BaseUrl}/{atHomePayload.DataPathSegment}/{atHomePayload.Hash}/{fileName}"));
            }

            return pages;
        }

        public string? FetchSearchPayload(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return null;
            }

            var encodedQuery = Uri.EscapeDataString(query.Trim());
            return TryFetchPayload($"https://api.mangadex.org/manga?title={encodedQuery}&limit=20&contentRating[]=safe&contentRating[]=suggestive&includes[]=cover_art");
        }

        public string? FetchChaptersPayload(string mediaId)
        {
            if (string.IsNullOrWhiteSpace(mediaId))
            {
                return null;
            }

            var encodedMediaId = Uri.EscapeDataString(mediaId.Trim());
            return TryFetchPayload($"https://api.mangadex.org/manga/{encodedMediaId}/feed?limit=100&order[chapter]=asc&translatedLanguage[]=en&includeUnavailable=1");
        }

        public string? FetchAtHomePayload(string chapterId)
        {
            if (string.IsNullOrWhiteSpace(chapterId))
            {
                return null;
            }

            var encodedChapterId = Uri.EscapeDataString(chapterId.Trim());
            return TryFetchPayload($"https://api.mangadex.org/at-home/server/{encodedChapterId}");
        }

        private static string? TryFetchPayload(string absoluteUrl)
        {
            try
            {
                var payload = Http.GetStringAsync(absoluteUrl).GetAwaiter().GetResult();
                return ResolvePayloadContent(payload);
            }
            catch
            {
                return null;
            }
        }

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "EMMA-TestPlugin/1.0");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
            return client;
        }

        private static bool TryParseAtHomePayload(string payloadJson, out AtHomePayload payload)
        {
            payload = default;

            using var doc = JsonDocument.Parse(payloadJson);

            var baseUrl = GetString(doc.RootElement, "baseUrl");
            var chapter = GetObject(doc.RootElement, "chapter");
            if (string.IsNullOrWhiteSpace(baseUrl) || chapter is null)
            {
                return false;
            }

            var hash = GetString(chapter.Value, "hash");
            var files = GetArray(chapter.Value, "data");
            var dataPathSegment = "data";
            if (files is null || files.Value.GetArrayLength() == 0)
            {
                files = GetArray(chapter.Value, "dataSaver");
                dataPathSegment = "data-saver";
            }

            if (string.IsNullOrWhiteSpace(hash) || files is null)
            {
                return false;
            }

            var fileNames = files.Value.EnumerateArray()
                .Select(file => file.GetString())
                .Where(file => !string.IsNullOrWhiteSpace(file))
                .Select(file => file!)
                .ToList();

            payload = new AtHomePayload(baseUrl, hash, dataPathSegment, fileNames);
            return true;
        }

        private static string? GetTitle(JsonElement item)
        {
            var attributes = GetObject(item, "attributes");
            if (attributes is null)
            {
                return null;
            }

            var titleMap = GetObject(attributes.Value, "title");
            return PickMapString(titleMap);
        }

        private static string? GetDescription(JsonElement item)
        {
            var attributes = GetObject(item, "attributes");
            if (attributes is null)
            {
                return null;
            }

            var descriptionMap = GetObject(attributes.Value, "description");
            return PickMapString(descriptionMap);
        }

        private static string? BuildThumbnailUrl(JsonElement item)
        {
            var mangaId = GetString(item, "id");
            if (string.IsNullOrWhiteSpace(mangaId))
            {
                return null;
            }

            var relationships = GetArray(item, "relationships");
            if (relationships is null)
            {
                return null;
            }

            foreach (var relation in relationships.Value.EnumerateArray())
            {
                var relationType = GetString(relation, "type");
                if (!string.Equals(relationType, "cover_art", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var attributes = GetObject(relation, "attributes");
                if (attributes is null)
                {
                    continue;
                }

                var fileName = GetString(attributes.Value, "fileName");
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    continue;
                }

                return $"https://uploads.mangadex.org/covers/{mangaId}/{fileName}";
            }

            return null;
        }

        private static string? PickMapString(JsonElement? map)
        {
            if (map is null || map.Value.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (map.Value.TryGetProperty("en", out var en) && en.ValueKind == JsonValueKind.String)
            {
                var value = en.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            foreach (var property in map.Value.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    var value = property.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
            }

            return null;
        }

        private static JsonElement? GetObject(JsonElement element, string name)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object)
            {
                return value;
            }

            return null;
        }

        internal static JsonElement? GetArray(JsonElement element, string name)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array)
            {
                return value;
            }

            return null;
        }

        private static string? GetString(JsonElement element, string name)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }

            return null;
        }

        private static int? GetInt32(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                return null;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var numeric))
            {
                return numeric;
            }

            if (value.ValueKind == JsonValueKind.String
                && int.TryParse(value.GetString(), out var parsed))
            {
                return parsed;
            }

            return null;
        }

        internal static string ResolvePayloadContent(string payload)
        {
            return NormalizeJsonPayload(payload);
        }

        private readonly record struct AtHomePayload(
            string BaseUrl,
            string Hash,
            string DataPathSegment,
            IReadOnlyList<string> Files);
    }

    public sealed record HandshakeResponse(string version, string message);

    public sealed record SearchItem(string id, string source, string title, string mediaType, string? thumbnailUrl = null, string? description = null);

    public sealed record CapabilityItem(string name, string[] mediaTypes, string[] operations);

    public sealed record ChapterItem(string id, int number, string title);

    public sealed record PageItem(string id, int index, string contentUri);

    public sealed record OperationRequest(
        string operation,
        string? mediaId,
        string? mediaType,
        string? argsJson,
        string? payloadJson);

    public sealed record OperationResult(
        bool isError,
        string? error,
        string contentType,
        string payloadJson)
    {
        public static OperationResult Error(string error)
            => new(true, error, "application/problem+json", "");
    }

    private sealed record BenchmarkResult(int iterations, long checksum, int generatedBytes, long elapsedMs);
    private sealed record NetworkBenchmarkResult(string query, int payloadBytes, int itemCount, long elapsedMs);

    [JsonSerializable(typeof(HandshakeResponse))]
    [JsonSerializable(typeof(string[]))]
    [JsonSerializable(typeof(CapabilityItem[]))]
    [JsonSerializable(typeof(SearchItem[]))]
    [JsonSerializable(typeof(ChapterItem[]))]
    [JsonSerializable(typeof(PageItem))]
    [JsonSerializable(typeof(PageItem[]))]
    [JsonSerializable(typeof(OperationResult))]
    [JsonSerializable(typeof(BenchmarkResult))]
    [JsonSerializable(typeof(NetworkBenchmarkResult))]
    private sealed partial class TestPluginWasmJsonContext : JsonSerializerContext
    {
    }
#endif
}
