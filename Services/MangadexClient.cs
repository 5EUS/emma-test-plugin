using System.Text.Json;
using EMMA.Contracts.Plugins;
using Microsoft.Extensions.Logging;

namespace EMMA.TestPlugin.Services;

public sealed class MangadexClient(HttpClient httpClient, ILogger<MangadexClient> logger)
{
    private const string SourceId = "mangadex";
    private const string MediaTypePaged = "paged";
    private readonly HttpClient _httpClient = httpClient;
    private readonly ILogger<MangadexClient> _logger = logger;

    public async Task<IReadOnlyList<MediaSummary>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var path = $"/manga?title={Uri.EscapeDataString(query)}&limit=20&contentRating[]=safe&contentRating[]=suggestive&includes[]=cover_art";
        using var response = await _httpClient.GetAsync(path, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var data = GetArray(doc.RootElement, "data");
        if (data is null)
        {
            return [];
        }

        var results = new List<MediaSummary>();
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

            results.Add(new MediaSummary
            {
                Id = id,
                Source = SourceId,
                Title = title,
                MediaType = MediaTypePaged
            });
        }

        _logger.LogInformation("Mangadex search query={Query} results={Count}", query, results.Count);
        return results;
    }

    public async Task<IReadOnlyList<MediaChapter>> GetChaptersAsync(string mediaId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(mediaId))
        {
            return [];
        }

        var path = $"/manga/{Uri.EscapeDataString(mediaId)}/feed?limit=100&order[chapter]=asc&translatedLanguage[]=en";
        using var response = await _httpClient.GetAsync(path, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var data = GetArray(doc.RootElement, "data");
        if (data is null)
        {
            return [];
        }

        var results = new List<MediaChapter>();
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

            results.Add(new MediaChapter
            {
                Id = id,
                Number = number,
                Title = title
            });
            index++;
        }

        _logger.LogInformation("Mangadex chapters mediaId={MediaId} count={Count}", mediaId, results.Count);
        return results;
    }

    public async Task<MediaPage?> GetPageAsync(string chapterId, int pageIndex, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(chapterId) || pageIndex < 0)
        {
            return null;
        }

        var path = $"/at-home/server/{Uri.EscapeDataString(chapterId)}";
        using var response = await _httpClient.GetAsync(path, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

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

        var uri = new Uri($"{baseUrl}/data/{hash}/{fileName}", UriKind.Absolute);
        var pageId = $"{chapterId}:{pageIndex}";

        return new MediaPage
        {
            Id = pageId,
            Index = pageIndex,
            ContentUri = uri.ToString()
        };
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
}
