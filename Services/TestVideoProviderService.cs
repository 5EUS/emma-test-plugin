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
        return Task.FromResult(new StreamResponse());
    }

    public override Task<SegmentResponse> GetSegment(SegmentRequest request, ServerCallContext context)
    {
        return Task.FromResult(new SegmentResponse());
    }
}
