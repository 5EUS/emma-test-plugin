using EMMA.Plugin.Common;
using EMMA.Plugin.AspNetCore;
using EMMA.Contracts.Plugins;

namespace EMMA.TestPlugin.Core;

/// <summary>
/// Enriches MediaSummary objects with statistics metadata on-demand.
/// Demonstrates the PluginDeferredMetadataEnricher pattern for Mangadex statistics.
/// </summary>
internal sealed class MediaSummaryStatisticsEnricher : PluginDeferredMetadataEnricher<MediaSummary, List<MetadataItem>>
{
    protected override string ExtractId(MediaSummary item)
    {
        return item.Id;
    }

    protected override async Task<MediaSummary> EnrichItemAsync(
        MediaSummary item,
        List<MetadataItem> metadata,
        CancellationToken cancellationToken)
    {
        if (metadata == null || metadata.Count == 0)
        {
            return item;
        }

        return await Task.FromResult(PluginContractMapper.CloneMediaSummary(item, metadata));
    }

    /// <summary>
    /// Singleton instance.
    /// </summary>
    public static readonly MediaSummaryStatisticsEnricher Instance = new();
}
