using EMMA.Contracts.Plugins;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace EMMA.TestPlugin.Services;

/// <summary>
/// Minimal video provider stub for testing.
/// </summary>
public sealed class TestVideoProviderService(ILogger<TestVideoProviderService> logger) : VideoProvider.VideoProviderBase
{
    private readonly ILogger<TestVideoProviderService> _logger = logger;

    public override Task<StreamResponse> GetStreams(StreamRequest request, ServerCallContext context)
    {
        TestPluginRpcGuard.EnsureActive(context);
        var correlationId = TestPluginRpcGuard.GetCorrelationId(context);

        _logger.LogInformation(
            "Streams request {CorrelationId} mediaId={MediaId}",
            correlationId,
            request.MediaId);

        var response = new StreamResponse();

        if (string.Equals(request.MediaId, TestPluginData.DemoVideoId, StringComparison.OrdinalIgnoreCase))
        {
            response.Streams.AddRange(TestPluginData.Streams);
        }

        return Task.FromResult(response);
    }

    public override Task<SegmentResponse> GetSegment(SegmentRequest request, ServerCallContext context)
    {
        TestPluginRpcGuard.EnsureActive(context);
        var correlationId = TestPluginRpcGuard.GetCorrelationId(context);

        _logger.LogInformation(
            "Segment request {CorrelationId} mediaId={MediaId} streamId={StreamId} sequence={Sequence}",
            correlationId,
            request.MediaId,
            request.StreamId,
            request.Sequence);

        if (string.Equals(request.MediaId, TestPluginData.DemoVideoId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(request.StreamId, TestPluginData.DemoStreamId, StringComparison.OrdinalIgnoreCase)
            && request.Sequence == 0)
        {
            return Task.FromResult(TestPluginData.Segment);
        }

        return Task.FromResult(new SegmentResponse());
    }
}
