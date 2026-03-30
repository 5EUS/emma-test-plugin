using System.Diagnostics;
using System.Text.Json;
using EMMA.Plugin.Common;

namespace EMMA.TestPlugin.Infrastructure;

internal sealed class CoreClient
{
    public SearchParseMapResult SearchFromPayloadWithTimings(string payloadJson)
    {
        var normalizedPayload = ResolvePayloadContent(payloadJson);
        if (string.IsNullOrWhiteSpace(normalizedPayload))
        {
            return new SearchParseMapResult([], 0, 0);
        }

        var parseStopwatch = Stopwatch.StartNew();
        using var doc = JsonDocument.Parse(normalizedPayload);
        parseStopwatch.Stop();

        var mapStopwatch = Stopwatch.StartNew();
        var entries = PayloadMapper.ParseSearchEntries(doc.RootElement);
        var results = new List<SearchItem>(entries.Count);
        foreach (var entry in entries)
        {
            results.Add(new SearchItem(
                entry.Id,
                PayloadMapper.SourceId,
                entry.Title,
                PayloadMapper.MediaTypePaged,
                entry.ThumbnailUrl,
                entry.Description));
        }

        mapStopwatch.Stop();
        return new SearchParseMapResult(results, parseStopwatch.ElapsedMilliseconds, mapStopwatch.ElapsedMilliseconds);
    }

    public IReadOnlyList<ChapterItem> GetChaptersFromPayload(string payloadJson)
    {
        var entries = ParseChapterEntries(payloadJson);
        var results = new List<ChapterItem>(entries.Count);
        foreach (var entry in entries)
        {
            results.Add(new ChapterItem(entry.Id, entry.Number, entry.Title, entry.UploaderGroups));
        }

        return results;
    }

    public IReadOnlyList<ChapterOperationItem> GetChapterOperationItemsFromPayload(string payloadJson)
    {
        var entries = ParseChapterEntries(payloadJson);
        var results = new List<ChapterOperationItem>(entries.Count);
        foreach (var entry in entries)
        {
            results.Add(new ChapterOperationItem(entry.Id, entry.Number, entry.Title, entry.UploaderGroups));
        }

        return results;
    }

    public PageItem? GetPageFromPayload(string chapterId, int pageIndex, string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(chapterId) || pageIndex < 0)
        {
            return null;
        }

        if (!TryGetAtHomePayload(payloadJson, out var atHomePayload))
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

        return new PageItem(
            $"{chapterId}:{pageIndex}",
            pageIndex,
            $"{atHomePayload.BaseUrl}/{atHomePayload.DataPathSegment}/{atHomePayload.Hash}/{fileName}");
    }

    public IReadOnlyList<PageItem> GetPagesFromPayload(
        string chapterId,
        int startIndex,
        int count,
        string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(chapterId) || startIndex < 0 || count <= 0)
        {
            return [];
        }

        if (!TryGetAtHomePayload(payloadJson, out var atHomePayload))
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

    public static string ResolvePayloadContent(string payload)
    {
        return PluginJsonPayload.Normalize(payload);
    }

    private static IReadOnlyList<MangadexChapterEntry> ParseChapterEntries(string payloadJson)
    {
        var normalizedPayload = ResolvePayloadContent(payloadJson);
        if (string.IsNullOrWhiteSpace(normalizedPayload))
        {
            return [];
        }

        using var doc = JsonDocument.Parse(normalizedPayload);
        return PayloadMapper.ParseChapterEntries(doc.RootElement);
    }

    private static bool TryGetAtHomePayload(string payloadJson, out MangadexAtHomePayload payload)
    {
        var normalizedPayload = ResolvePayloadContent(payloadJson);
        if (string.IsNullOrWhiteSpace(normalizedPayload))
        {
            payload = default;
            return false;
        }

        return PayloadMapper.TryParseAtHomePayload(normalizedPayload, out payload);
    }
}

internal readonly record struct SearchParseMapResult(
    IReadOnlyList<SearchItem> Results,
    long ParseMs,
    long MapMs);