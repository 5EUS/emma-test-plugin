using System.Text.Json;
using EMMA.Contracts.Plugins;
using Google.Protobuf;

namespace EMMA.TestPlugin;

/// <summary>
/// Shared demo data for the test plugin stubs.
/// </summary>
public static class TestPluginData
{
    private const string DefaultDemoMediaId = "demo-1";
    private const string DefaultDemoMediaIdAlt = "demo-2";
    private const string DefaultDemoChapterId = "ch-1";
    private const string DefaultDemoVideoId = "demo-video-1";
    private const string DefaultDemoStreamId = "stream-1";
    private const string DefaultSource = "test";
    private const string DefaultPagedMediaType = "paged";
    private const string DefaultPlaylistUri = "https://example.invalid/demo/playlist.m3u8";

    public static string DemoMediaId { get; private set; } = DefaultDemoMediaId;
    public static string DemoMediaIdAlt { get; private set; } = DefaultDemoMediaIdAlt;
    public static string DemoChapterId { get; private set; } = DefaultDemoChapterId;
    public static string DemoVideoId { get; private set; } = DefaultDemoVideoId;
    public static string DemoStreamId { get; private set; } = DefaultDemoStreamId;

    private static IReadOnlyList<MediaSummary> _searchResults = DefaultSearchResults();
    private static IReadOnlyList<MediaChapter> _chapters = DefaultChapters();
    private static MediaPage _page = DefaultPage();
    private static IReadOnlyList<StreamInfo> _streams = DefaultStreams();
    private static SegmentResponse _segment = DefaultSegment();

    public static IReadOnlyList<MediaSummary> SearchResults => _searchResults;
    public static IReadOnlyList<MediaChapter> Chapters => _chapters;
    public static MediaPage Page => _page;
    public static IReadOnlyList<StreamInfo> Streams => _streams;
    public static SegmentResponse Segment => _segment;

    private static readonly JsonSerializerOptions _jsonSerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static void LoadFromFile(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(path);
            var fixture = JsonSerializer.Deserialize<TestPluginFixture>(json, _jsonSerializerOptions);

            if (fixture is null)
            {
                return;
            }

            DemoMediaId = string.IsNullOrWhiteSpace(fixture.DemoMediaId) ? DemoMediaId : fixture.DemoMediaId;
            DemoMediaIdAlt = string.IsNullOrWhiteSpace(fixture.DemoMediaIdAlt) ? DemoMediaIdAlt : fixture.DemoMediaIdAlt;
            DemoChapterId = string.IsNullOrWhiteSpace(fixture.DemoChapterId) ? DemoChapterId : fixture.DemoChapterId;
            DemoVideoId = string.IsNullOrWhiteSpace(fixture.DemoVideoId) ? DemoVideoId : fixture.DemoVideoId;
            DemoStreamId = string.IsNullOrWhiteSpace(fixture.DemoStreamId) ? DemoStreamId : fixture.DemoStreamId;

            _searchResults = NormalizeSearchResults(fixture.SearchResults) ?? DefaultSearchResults();
            _chapters = NormalizeChapters(fixture.Chapters) ?? DefaultChapters();
            _page = NormalizePage(fixture.Page) ?? DefaultPage();
            _streams = NormalizeStreams(fixture.Streams) ?? DefaultStreams();
            _segment = fixture.Segment?.ToSegmentResponse() ?? DefaultSegment();
        }
        catch
        {
        }
    }

    private static IReadOnlyList<MediaSummary> DefaultSearchResults()
    {
        return
        [
            new MediaSummary
            {
                Id = DefaultDemoMediaId,
                Source = "test",
                Title = "Demo Paged Media",
                MediaType = "paged"
            },
            new MediaSummary
            {
                Id = DefaultDemoMediaIdAlt,
                Source = "test",
                Title = "Demo Paged Media Two",
                MediaType = "paged"
            }
        ];
    }

    private static IReadOnlyList<MediaChapter> DefaultChapters()
    {
        return
        [
            new MediaChapter
            {
                Id = DefaultDemoChapterId,
                Number = 1,
                Title = "Chapter One"
            }
        ];
    }

    private static MediaPage DefaultPage()
    {
        return new MediaPage
        {
            Id = "page-1",
            Index = 0,
            ContentUri = "https://example.invalid/demo-1/page-1.jpg"
        };
    }

    private static IReadOnlyList<StreamInfo> DefaultStreams()
    {
        return
        [
            new StreamInfo
            {
                Id = DefaultDemoStreamId,
                Label = "Test Stream",
                PlaylistUri = "https://example.invalid/demo/playlist.m3u8"
            }
        ];
    }

    private static List<MediaSummary>? NormalizeSearchResults(List<MediaSummary>? results)
    {
        if (results is null || results.Count == 0)
        {
            return null;
        }

        var normalized = new List<MediaSummary>(results.Count);

        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i] ?? new MediaSummary();
            var id = string.IsNullOrWhiteSpace(result.Id) ? $"demo-{i + 1}" : result.Id;
            var title = string.IsNullOrWhiteSpace(result.Title) ? $"Demo Paged Media {i + 1}" : result.Title;
            var source = string.IsNullOrWhiteSpace(result.Source) ? DefaultSource : result.Source;
            var mediaType = string.IsNullOrWhiteSpace(result.MediaType) ? DefaultPagedMediaType : result.MediaType;

            normalized.Add(new MediaSummary
            {
                Id = id,
                Title = title,
                Source = source,
                MediaType = mediaType
            });
        }

        return normalized;
    }

    private static List<MediaChapter>? NormalizeChapters(List<MediaChapter>? chapters)
    {
        if (chapters is null || chapters.Count == 0)
        {
            return null;
        }

        var normalized = new List<MediaChapter>(chapters.Count);

        for (var i = 0; i < chapters.Count; i++)
        {
            var chapter = chapters[i] ?? new MediaChapter();
            var number = chapter.Number == 0 ? i + 1 : chapter.Number;
            var id = string.IsNullOrWhiteSpace(chapter.Id) ? $"ch-{number}" : chapter.Id;
            var title = string.IsNullOrWhiteSpace(chapter.Title) ? $"Chapter {number}" : chapter.Title;

            normalized.Add(new MediaChapter
            {
                Id = id,
                Number = number,
                Title = title
            });
        }

        return normalized;
    }

    private static MediaPage? NormalizePage(MediaPage? page)
    {
        if (page is null)
        {
            return null;
        }

        var index = page.Index < 0 ? 0 : page.Index;
        var id = string.IsNullOrWhiteSpace(page.Id) ? $"page-{index + 1}" : page.Id;
        var contentUri = string.IsNullOrWhiteSpace(page.ContentUri)
            ? $"https://example.invalid/{DemoMediaId}/page-{index + 1}.jpg"
            : page.ContentUri;

        return new MediaPage
        {
            Id = id,
            Index = index,
            ContentUri = contentUri
        };
    }

    private static List<StreamInfo>? NormalizeStreams(List<StreamInfo>? streams)
    {
        if (streams is null || streams.Count == 0)
        {
            return null;
        }

        var normalized = new List<StreamInfo>(streams.Count);

        for (var i = 0; i < streams.Count; i++)
        {
            var stream = streams[i] ?? new StreamInfo();
            var id = string.IsNullOrWhiteSpace(stream.Id) ? $"stream-{i + 1}" : stream.Id;
            var label = string.IsNullOrWhiteSpace(stream.Label) ? $"Stream {i + 1}" : stream.Label;
            var playlistUri = string.IsNullOrWhiteSpace(stream.PlaylistUri) ? DefaultPlaylistUri : stream.PlaylistUri;

            normalized.Add(new StreamInfo
            {
                Id = id,
                Label = label,
                PlaylistUri = playlistUri
            });
        }

        return normalized;
    }

    private static SegmentResponse DefaultSegment()
    {
        return new SegmentResponse
        {
            Payload = ByteString.CopyFromUtf8("segment-0"),
            ContentType = "video/mp2t"
        };
    }

    private sealed class TestPluginFixture
    {
        public string? DemoMediaId { get; init; }
        public string? DemoMediaIdAlt { get; init; }
        public string? DemoChapterId { get; init; }
        public string? DemoVideoId { get; init; }
        public string? DemoStreamId { get; init; }
        public List<MediaSummary>? SearchResults { get; init; }
        public List<MediaChapter>? Chapters { get; init; }
        public MediaPage? Page { get; init; }
        public List<StreamInfo>? Streams { get; init; }
        public SegmentFixture? Segment { get; init; }
    }

    private sealed class SegmentFixture
    {
        public string? ContentType { get; init; }
        public string? PayloadText { get; init; }
        public string? PayloadBase64 { get; init; }

        public SegmentResponse ToSegmentResponse()
        {
            var payload = string.IsNullOrWhiteSpace(PayloadBase64)
                ? ByteString.CopyFromUtf8(PayloadText ?? string.Empty)
                : ByteString.CopyFrom(Convert.FromBase64String(PayloadBase64));

            return new SegmentResponse
            {
                Payload = payload,
                ContentType = string.IsNullOrWhiteSpace(ContentType) ? "video/mp2t" : ContentType
            };
        }
    }
}
