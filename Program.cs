#if PLUGIN_TRANSPORT_ASPNET
using EMMA.Plugin.AspNetCore;
using EMMA.TestPlugin.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
#else
using System.Net.Http;
using System.Net.Http.Headers;
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
                    new[] { "health", "search", "paged", "pages" },
                    TestPluginWasmJsonContext.Default.StringArray),
                "search" => Search(args),
                "chapters" => Chapters(args),
                "page" => Page(args),
                "pages" => Pages(args),
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

        var results = Mangadex.SearchFromPayload(query, payloadJson);
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

        var results = Mangadex.GetChaptersFromPayload(mediaId, payloadJson);
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
            return "null";
        }

        return JsonSerializer.Serialize(
            page,
            TestPluginWasmJsonContext.Default.PageItem);
    }

    private static string Pages(string[] args)
    {
        if (args.Length < 5)
        {
            return string.Empty;
        }

        var chapterId = args[2];
        if (!int.TryParse(args[3], out var startIndex)
            || startIndex < 0
            || !int.TryParse(args[4], out var count)
            || count <= 0
            || string.IsNullOrWhiteSpace(chapterId))
        {
            return string.Empty;
        }

        var payloadJson = args.Length > 5 ? args[5] : string.Empty;
        var pages = Mangadex.GetPagesFromPayload(chapterId, startIndex, count, payloadJson);

        return JsonSerializer.Serialize(
            pages.ToArray(),
            TestPluginWasmJsonContext.Default.PageItemArray);
    }

    private static WasmMangadexClient CreateMangadexClient()
    {
        return new WasmMangadexClient();
    }

    private sealed class WasmMangadexClient
    {
        private const string DirectHttpEnvVar = "EMMA_WASM_DIRECT_HTTP";
        private const string SearchTemplate = "https://api.mangadex.org/manga?title={0}&limit=20&contentRating[]=safe&contentRating[]=suggestive&includes[]=cover_art";
        private const string ChaptersTemplate = "https://api.mangadex.org/manga/{0}/feed?limit=100&order[chapter]=asc&translatedLanguage[]=en&includeUnavailable=1";
        private const string AtHomeTemplate = "https://api.mangadex.org/at-home/server/{0}";
        private static readonly HttpClient DirectHttpClient = CreateDirectHttpClient();

        public IReadOnlyList<SearchItem> SearchFromPayload(string query, string payloadJson)
        {
            payloadJson = ResolvePayloadContent(payloadJson);

            if (string.IsNullOrWhiteSpace(payloadJson) && IsDirectHttpEnabled())
            {
                var directPayload = TryFetchJson(string.Format(SearchTemplate, Uri.EscapeDataString(query ?? string.Empty)));
                if (!string.IsNullOrWhiteSpace(directPayload))
                {
                    payloadJson = directPayload;
                }
            }

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

            if (string.IsNullOrWhiteSpace(payloadJson)
                && IsDirectHttpEnabled()
                && !string.IsNullOrWhiteSpace(mediaId))
            {
                var directPayload = TryFetchJson(string.Format(ChaptersTemplate, Uri.EscapeDataString(mediaId)));
                if (!string.IsNullOrWhiteSpace(directPayload))
                {
                    payloadJson = directPayload;
                }
            }

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

            if (string.IsNullOrWhiteSpace(payloadJson)
                && IsDirectHttpEnabled()
                && !string.IsNullOrWhiteSpace(chapterId))
            {
                var directPayload = TryFetchJson(string.Format(AtHomeTemplate, Uri.EscapeDataString(chapterId)));
                if (!string.IsNullOrWhiteSpace(directPayload))
                {
                    payloadJson = directPayload;
                }
            }

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
            var dataPathSegment = "data";
            if (files is null || files.Value.GetArrayLength() == 0)
            {
                files = GetArray(chapter.Value, "dataSaver");
                dataPathSegment = "data-saver";
            }

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
                $"{baseUrl}/{dataPathSegment}/{hash}/{fileName}");
        }

        public IReadOnlyList<PageItem> GetPagesFromPayload(
            string chapterId,
            int startIndex,
            int count,
            string payloadJson)
        {
            payloadJson = ResolvePayloadContent(payloadJson);

            if (string.IsNullOrWhiteSpace(payloadJson)
                && IsDirectHttpEnabled()
                && !string.IsNullOrWhiteSpace(chapterId))
            {
                var directPayload = TryFetchJson(string.Format(AtHomeTemplate, Uri.EscapeDataString(chapterId)));
                if (!string.IsNullOrWhiteSpace(directPayload))
                {
                    payloadJson = directPayload;
                }
            }

            if (string.IsNullOrWhiteSpace(chapterId)
                || startIndex < 0
                || count <= 0
                || string.IsNullOrWhiteSpace(payloadJson))
            {
                return [];
            }

            using var doc = JsonDocument.Parse(payloadJson);

            var baseUrl = GetString(doc.RootElement, "baseUrl");
            var chapter = GetObject(doc.RootElement, "chapter");
            if (string.IsNullOrWhiteSpace(baseUrl) || chapter is null)
            {
                return [];
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
                return [];
            }

            var items = files.Value.EnumerateArray().ToList();
            if (startIndex >= items.Count)
            {
                return [];
            }

            var endExclusive = Math.Min(items.Count, startIndex + count);
            var pages = new List<PageItem>(Math.Max(0, endExclusive - startIndex));

            for (var pageIndex = startIndex; pageIndex < endExclusive; pageIndex++)
            {
                var fileName = items[pageIndex].GetString();
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    continue;
                }

                pages.Add(new PageItem(
                    $"{chapterId}:{pageIndex}",
                    pageIndex,
                    $"{baseUrl}/{dataPathSegment}/{hash}/{fileName}"));
            }

            return pages;
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

        private static string ResolvePayloadContent(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                return string.Empty;
            }

            var trimmed = payload.Trim();
            const string inlinePayloadPrefix = "emma-inline-json-b64:";
            if (trimmed.StartsWith(inlinePayloadPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var encoded = trimmed[inlinePayloadPrefix.Length..];
                if (string.IsNullOrWhiteSpace(encoded))
                {
                    return string.Empty;
                }

                try
                {
                    var bytes = Convert.FromBase64String(encoded);
                    return System.Text.Encoding.UTF8.GetString(bytes);
                }
                catch
                {
                    return string.Empty;
                }
            }

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

        private static HttpClient CreateDirectHttpClient()
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(2)
            };
            client.DefaultRequestHeaders.UserAgent.Clear();
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("EMMA-TestPlugin", "1.0"));
            client.DefaultRequestHeaders.Accept.Clear();
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            return client;
        }

        private static string TryFetchJson(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return string.Empty;
            }

            try
            {
                return DirectHttpClient.GetStringAsync(url).GetAwaiter().GetResult();
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool IsDirectHttpEnabled()
        {
            var value = Environment.GetEnvironmentVariable(DirectHttpEnvVar);
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            if (bool.TryParse(value, out var parsed))
            {
                return parsed;
            }

            return value.Trim() is "1" or "yes" or "on";
        }
    }

    private sealed record HandshakeResponse(string version, string message);

    private sealed record SearchItem(string id, string source, string title, string mediaType, string? thumbnailUrl = null, string? description = null);

    private sealed record ChapterItem(string id, int number, string title);

    private sealed record PageItem(string id, int index, string contentUri);

    [JsonSerializable(typeof(HandshakeResponse))]
    [JsonSerializable(typeof(string[]))]
    [JsonSerializable(typeof(SearchItem[]))]
    [JsonSerializable(typeof(ChapterItem[]))]
    [JsonSerializable(typeof(PageItem))]
    [JsonSerializable(typeof(PageItem[]))]
    private sealed partial class TestPluginWasmJsonContext : JsonSerializerContext
    {
    }
#endif
}
