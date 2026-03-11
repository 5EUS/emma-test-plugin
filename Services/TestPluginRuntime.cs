using EMMA.Contracts.Plugins;
using Google.Protobuf;
using Microsoft.Extensions.Logging;

namespace EMMA.TestPlugin.Services;

public sealed class TestPluginRuntime(
    MangadexClient mangadexClient,
    ILogger<TestPluginRuntime> logger) : ITestPluginRuntime
{
    private const string DemoVideoId = "demo-video-1";
    private const string DemoStreamId = "stream-1";
    private const string DemoPlaylistUri = "https://example.invalid/demo/playlist.m3u8";
    private readonly MangadexClient _mangadexClient = mangadexClient;
    private readonly ILogger<TestPluginRuntime> _logger = logger;

    public Task<IReadOnlyList<MediaSummary>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        return _mangadexClient.SearchAsync(query, cancellationToken);
    }

    public Task<IReadOnlyList<MediaChapter>> GetChaptersAsync(string mediaId, CancellationToken cancellationToken)
    {
        return _mangadexClient.GetChaptersAsync(mediaId, cancellationToken);
    }

    public Task<MediaPage?> GetPageAsync(string chapterId, int pageIndex, CancellationToken cancellationToken)
    {
        return _mangadexClient.GetPageAsync(chapterId, pageIndex, cancellationToken);
    }

    public Task<(IReadOnlyList<MediaPage> Pages, bool ReachedEnd)> GetPagesAsync(
        string chapterId,
        int startIndex,
        int count,
        CancellationToken cancellationToken)
    {
        return _mangadexClient.GetPagesAsync(chapterId, startIndex, count, cancellationToken);
    }

    public Task<StreamResponse> GetStreamsAsync(string mediaId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var response = new StreamResponse();

        if (string.Equals(mediaId, DemoVideoId, StringComparison.OrdinalIgnoreCase))
        {
            response.Streams.Add(new StreamInfo
            {
                Id = DemoStreamId,
                Label = "Test Stream",
                PlaylistUri = DemoPlaylistUri
            });
        }

        _logger.LogInformation("Streams mediaId={MediaId} count={Count}", mediaId, response.Streams.Count);
        return Task.FromResult(response);
    }

    public Task<SegmentResponse> GetSegmentAsync(string mediaId, string streamId, int sequence, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.Equals(mediaId, DemoVideoId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(streamId, DemoStreamId, StringComparison.OrdinalIgnoreCase)
            && sequence == 0)
        {
            return Task.FromResult(new SegmentResponse
            {
                ContentType = "video/mp2t",
                Payload = ByteString.CopyFromUtf8("segment-0")
            });
        }

        return Task.FromResult(new SegmentResponse());
    }
}
