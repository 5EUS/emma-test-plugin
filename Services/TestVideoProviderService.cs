using EMMA.Contracts.Plugins;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace EMMA.TestPlugin.Services;

/// <summary>
/// Minimal video provider stub for testing.
/// </summary>
public sealed class TestVideoProviderService(
    ITestPluginRuntime runtime,
    ILogger<TestVideoProviderService> logger) : VideoProvider.VideoProviderBase
{
    private readonly ITestPluginRuntime _runtime = runtime;
    private readonly ILogger<TestVideoProviderService> _logger = logger;

    public override Task<StreamResponse> GetStreams(StreamRequest request, ServerCallContext context)
    {
        TestPluginRpcGuard.EnsureActive(context);
        var correlationId = TestPluginRpcGuard.GetCorrelationId(context, request.Context?.CorrelationId);

        _logger.LogInformation(
            "Streams request {CorrelationId} mediaId={MediaId}",
            correlationId,
            request.MediaId);

        return _runtime.GetStreamsAsync(request.MediaId, context.CancellationToken);
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

        return _runtime.GetSegmentAsync(
            request.MediaId,
            request.StreamId,
            request.Sequence,
            context.CancellationToken);
    }
}
