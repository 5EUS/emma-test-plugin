using EMMA.Contracts.Plugins;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace EMMA.TestPlugin.Services;

/// <summary>
/// Minimal video provider stub for testing.
/// </summary>
public sealed class TestVideoProviderService(ILogger<TestVideoProviderService> logger) : VideoProvider.VideoProviderBase
{
    private const string DemoVideoId = "demo-video-1";
    private const string DemoStreamId = "stream-1";
    private const string DemoPlaylistUri = "https://example.invalid/demo/playlist.m3u8";
    private readonly ILogger<TestVideoProviderService> _logger = logger;

    public override Task<StreamResponse> GetStreams(StreamRequest request, ServerCallContext context)
    {
        TestPluginRpcGuard.EnsureActive(context);
        var correlationId = TestPluginRpcGuard.GetCorrelationId(context, request.Context?.CorrelationId);

        _logger.LogInformation(
            "Streams request {CorrelationId} mediaId={MediaId}",
            correlationId,
            request.MediaId);

        var response = new StreamResponse();

        if (string.Equals(request.MediaId, DemoVideoId, StringComparison.OrdinalIgnoreCase))
        {
            response.Streams.Add(new StreamInfo
            {
                Id = DemoStreamId,
                Label = "Test Stream",
                PlaylistUri = DemoPlaylistUri
            });
        }

        return Task.FromResult(response);
    }

    public override Task<SegmentResponse> GetSegment(SegmentRequest request, ServerCallContext context)
    {
        TestPluginRpcGuard.EnsureActive(context);
        var correlationId = TestPluginRpcGuard.GetCorrelationId(context, request.Context?.CorrelationId);

        _logger.LogInformation(
            "Segment request {CorrelationId} mediaId={MediaId} streamId={StreamId} sequence={Sequence}",
            correlationId,
            request.MediaId,
            request.StreamId,
            request.Sequence);

        if (string.Equals(request.MediaId, DemoVideoId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(request.StreamId, DemoStreamId, StringComparison.OrdinalIgnoreCase)
            && request.Sequence == 0)
        {
            return Task.FromResult(new SegmentResponse
            {
                ContentType = "video/mp2t",
                PayloadText = "segment-0"
            });
        }

        return Task.FromResult(new SegmentResponse());
    }
}
