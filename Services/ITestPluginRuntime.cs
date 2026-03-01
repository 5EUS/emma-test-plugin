using EMMA.Contracts.Plugins;

namespace EMMA.TestPlugin.Services;

public interface ITestPluginRuntime
{
    Task<HealthResponse> GetHealthAsync(CancellationToken cancellationToken);
    Task<CapabilitiesResponse> GetCapabilitiesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<MediaSummary>> SearchAsync(string query, CancellationToken cancellationToken);
    Task<IReadOnlyList<MediaChapter>> GetChaptersAsync(string mediaId, CancellationToken cancellationToken);
    Task<MediaPage?> GetPageAsync(string chapterId, int pageIndex, CancellationToken cancellationToken);
    Task<StreamResponse> GetStreamsAsync(string mediaId, CancellationToken cancellationToken);
    Task<SegmentResponse> GetSegmentAsync(string mediaId, string streamId, int sequence, CancellationToken cancellationToken);
}
