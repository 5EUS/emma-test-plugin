using EMMA.Contracts.Plugins;
using Grpc.Core;

namespace EMMA.TestPlugin.Services;

/// <summary>
/// Minimal video provider stub for testing.
/// </summary>
public sealed class TestVideoProviderService : VideoProvider.VideoProviderBase
{
    public override Task<StreamResponse> GetStreams(StreamRequest request, ServerCallContext context)
    {
        var response = new StreamResponse();

        if (string.Equals(request.MediaId, TestPluginData.DemoVideoId, StringComparison.OrdinalIgnoreCase))
        {
            response.Streams.AddRange(TestPluginData.Streams);
        }

        return Task.FromResult(response);
    }

    public override Task<SegmentResponse> GetSegment(SegmentRequest request, ServerCallContext context)
    {
        if (string.Equals(request.MediaId, TestPluginData.DemoVideoId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(request.StreamId, TestPluginData.DemoStreamId, StringComparison.OrdinalIgnoreCase)
            && request.Sequence == 0)
        {
            return Task.FromResult(TestPluginData.Segment);
        }

        return Task.FromResult(new SegmentResponse());
    }
}
