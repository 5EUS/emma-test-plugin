#if PLUGIN_TRANSPORT_ASPNET
using EMMA.Plugin.AspNetCore;
using EMMA.TestPlugin.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
#else
using System.Text.Json;
using System.Text.Json.Serialization;
#endif

namespace EMMA.TestPlugin;

// TODO IMPORTANT: again, potentially an attack surface. ideally this is all setup by PluginHost
public static partial class Program
{
#if PLUGIN_TRANSPORT_ASPNET
    public static void Main(string[] args)
    {
        var hostOptions = new PluginAspNetHostOptions(
            DefaultPort: 5005,
            PortEnvironmentVariables: ["EMMA_PLUGIN_PORT", "EMMA_TEST_PLUGIN_PORT"],
            RootMessage: "EMMA test plugin is running.");

        var app = PluginAspNetHost.Create(args, hostOptions, services =>
        {
            services.AddGrpc();
            services.AddHttpClient<MangadexClient>(client =>
            {
                client.BaseAddress = new Uri("https://api.mangadex.org");
                client.DefaultRequestHeaders.UserAgent.ParseAdd("EMMA-TestPlugin/1.0");
                client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            });
            services.AddScoped<ITestPluginRuntime, TestPluginRuntime>();
        });

        PluginAspNetHost.MapDefaultEndpoints(app, hostOptions);
        app.MapGrpcService<TestPluginControlService>();
        app.MapGrpcService<TestSearchProviderService>();
        app.MapGrpcService<TestPageProviderService>();
        app.MapGrpcService<TestVideoProviderService>();

        app.Run();
    }
#else
    private static readonly WasmMangadexClient Mangadex = CreateMangadexClient();

    public static void Main(string[] args)
    {
        var operation = args.Length > 0 ? args[0].ToLowerInvariant() : string.Empty;
        var json = ExecuteOperation(operation, args);

        if (string.IsNullOrWhiteSpace(json))
        {
            Environment.ExitCode = 2;
            Console.Error.WriteLine("Unsupported or invalid operation.");
            return;
        }

        Console.WriteLine(json);
    }

    private static string ExecuteOperation(string operation, string[] args)
    {
        try
        {
            return operation switch
            {
                "handshake" => JsonSerializer.Serialize(
                    new HandshakeResponse("1.0.0", "EMMA test wasm component ready"),
                    TestPluginWasmJsonContext.Default.HandshakeResponse),
                "capabilities" => JsonSerializer.Serialize(
                    new[] { "health", "search", "paged" },
                    TestPluginWasmJsonContext.Default.StringArray),
                "search" => Search(args),
                "chapters" => Chapters(args),
                "page" => Page(args),
                _ => string.Empty
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"WASM operation '{operation}' failed: {ex}");
            return string.Empty;
        }
    }

    private static string Search(string[] args)
    {
        var query = args.Length > 1 ? args[1] : string.Empty;
        if (string.IsNullOrWhiteSpace(query))
        {
            return JsonSerializer.Serialize(Array.Empty<SearchItem>(), TestPluginWasmJsonContext.Default.SearchItemArray);
        }

        var payloadJson = args.Length > 2 ? args[2] : string.Empty;

        var results = Mangadex.SearchFromPayload(payloadJson);
        var items = results.ToArray();

        return JsonSerializer.Serialize(items, TestPluginWasmJsonContext.Default.SearchItemArray);
    }

    private static string Chapters(string[] args)
    {
        var mediaId = args.Length > 1 ? args[1] : string.Empty;
        if (string.IsNullOrWhiteSpace(mediaId))
        {
            return JsonSerializer.Serialize(Array.Empty<ChapterItem>(), TestPluginWasmJsonContext.Default.ChapterItemArray);
        }

        var payloadJson = args.Length > 2 ? args[2] : string.Empty;

        var results = Mangadex.GetChaptersFromPayload(payloadJson);
        var chapters = results.ToArray();

        return JsonSerializer.Serialize(chapters, TestPluginWasmJsonContext.Default.ChapterItemArray);
    }

    private static string Page(string[] args)
    {
        if (args.Length < 4)
        {
            return string.Empty;
        }

        var chapterId = args[2];
        if (!int.TryParse(args[3], out var pageIndex) || pageIndex < 0 || string.IsNullOrWhiteSpace(chapterId))
        {
            return string.Empty;
        }

        var payloadJson = args.Length > 4 ? args[4] : string.Empty;

        var page = Mangadex.GetPageFromPayload(chapterId, pageIndex, payloadJson);
        if (page is null)
        {
            return string.Empty;
        }

        return JsonSerializer.Serialize(
            page,
            TestPluginWasmJsonContext.Default.PageItem);
    }

    private static WasmMangadexClient CreateMangadexClient()
    {
        return new WasmMangadexClient();
    }

    private sealed class WasmMangadexClient
    {
        public IReadOnlyList<SearchItem> SearchFromPayload(string payloadJson)
        {
            payloadJson = ResolvePayloadContent(payloadJson);
            if (string.IsNullOrWhiteSpace(payloadJson))
            {
                return [];
            }

            // TODO(dotnet-wasm-http): Remove host-supplied payload bridge once outbound HttpClient is supported for .NET WASM components.
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

                results.Add(new SearchItem(
                    id,
                    "mangadex",
                    title,
                    "paged"));
            }

            return results;
        }

        public IReadOnlyList<ChapterItem> GetChaptersFromPayload(string payloadJson)
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

            using var doc = JsonDocument.Parse(payloadJson);

            var baseUrl = GetString(doc.RootElement, "baseUrl");
            var chapter = GetObject(doc.RootElement, "chapter");
            if (string.IsNullOrWhiteSpace(baseUrl) || chapter is null)
            {
                return null;
            }

            var hash = GetString(chapter.Value, "hash");
            var files = GetArray(chapter.Value, "data");
            if (string.IsNullOrWhiteSpace(hash) || files is null)
            {
                return null;
            }

            var items = files.Value.EnumerateArray().ToList();
            if (pageIndex >= items.Count)
            {
                return null;
            }

            var fileName = items[pageIndex].GetString();
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return null;
            }

            var pageId = $"{chapterId}:{pageIndex}";

            return new PageItem(
                pageId,
                pageIndex,
                $"{baseUrl}/data/{hash}/{fileName}");
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

        private static JsonElement? GetArray(JsonElement element, string name)
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

        private static string ResolvePayloadContent(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                return string.Empty;
            }

            var trimmed = payload.Trim();
            if (trimmed.StartsWith("{") || trimmed.StartsWith("["))
            {
                return payload;
            }

            var candidates = new List<string>();
            candidates.Add(trimmed);

            var withoutLeadingSlash = trimmed.TrimStart('/');
            if (!string.Equals(withoutLeadingSlash, trimmed, StringComparison.Ordinal))
            {
                candidates.Add(withoutLeadingSlash);
                candidates.Add(Path.Combine(".", withoutLeadingSlash));
            }

            if (trimmed.StartsWith("/.hostbridge/", StringComparison.OrdinalIgnoreCase))
            {
                var relativeHostBridge = ".hostbridge/" + trimmed["/.hostbridge/".Length..];
                candidates.Add(relativeHostBridge);
                candidates.Add(Path.Combine(".", relativeHostBridge));
            }

            foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(candidate))
                {
                    continue;
                }

                return File.ReadAllText(candidate);
            }

            return payload;
        }
    }

    private sealed record HandshakeResponse(string version, string message);

    private sealed record SearchItem(string id, string source, string title, string mediaType);

    private sealed record ChapterItem(string id, int number, string title);

    private sealed record PageItem(string id, int index, string contentUri);

    [JsonSerializable(typeof(HandshakeResponse))]
    [JsonSerializable(typeof(string[]))]
    [JsonSerializable(typeof(SearchItem[]))]
    [JsonSerializable(typeof(ChapterItem[]))]
    [JsonSerializable(typeof(PageItem))]
    private sealed partial class TestPluginWasmJsonContext : JsonSerializerContext
    {
    }
#endif
}
