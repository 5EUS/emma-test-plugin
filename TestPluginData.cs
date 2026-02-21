using EMMA.Contracts.Plugins;

namespace EMMA.TestPlugin;

/// <summary>
/// Shared demo data for the test plugin stubs.
/// </summary>
public static class TestPluginData
{
    public const string DemoMediaId = "demo-1";
    public const string DemoMediaIdAlt = "demo-2";
    public const string DemoChapterId = "ch-1";
    public const string DemoVideoId = "demo-video-1";
    public const string DemoStreamId = "stream-1";

    public static IReadOnlyList<MediaSummary> SearchResults =>
    [
        new MediaSummary
        {
            Id = DemoMediaId,
            Source = "test",
            Title = "Demo Paged Media",
            MediaType = "paged"
        },
        new MediaSummary
        {
            Id = DemoMediaIdAlt,
            Source = "test",
            Title = "Demo Paged Media Two",
            MediaType = "paged"
        }
    ];

    public static IReadOnlyList<MediaChapter> Chapters =>
    [
        new MediaChapter
        {
            Id = DemoChapterId,
            Number = 1,
            Title = "Chapter One"
        }
    ];

    public static MediaPage Page => new()
    {
        Id = "page-1",
        Index = 0,
        ContentUri = "https://example.invalid/demo-1/page-1.jpg"
    };

    public static IReadOnlyList<StreamInfo> Streams =>
    [
        new StreamInfo
        {
            Id = DemoStreamId,
            Label = "Test Stream",
            PlaylistUri = "https://example.invalid/demo/playlist.m3u8"
        }
    ];

    public static SegmentResponse Segment => new()
    {
        Payload = Google.Protobuf.ByteString.CopyFromUtf8("segment-0"),
        ContentType = "video/mp2t"
    };
}
