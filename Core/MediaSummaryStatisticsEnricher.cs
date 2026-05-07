using EMMA.Plugin.Common;
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

        var enriched = new MediaSummary
        {
            Id = item.Id,
            Source = item.Source,
            Title = item.Title,
            MediaType = item.MediaType,
            ThumbnailUrl = item.ThumbnailUrl,
            Description = item.Description
        };

        enriched.Metadata.AddRange(item.Metadata);
        foreach (var metaItem in metadata)
        {
            enriched.Metadata.Add(new KeyValue { Key = metaItem.key, Value = metaItem.value });
        }

        return await Task.FromResult(enriched);
    }

    /// <summary>
    /// Singleton instance.
    /// </summary>
    public static readonly MediaSummaryStatisticsEnricher Instance = new();
}
