using EMMA.Plugin.Common;
using EMMA.Contracts.Plugins;

namespace EMMA.TestPlugin.Core;

/// <summary>
/// Enriches MediaSummary objects with statistics metadata on-demand.
/// Demonstrates the PluginDeferredMetadataEnricher pattern for Mangadex statistics.
/// </summary>
internal sealed class MediaSummaryStatisticsEnricher : PluginDeferredMetadataEnricher<MediaSummary, List<MetadataItem>>
{
    internal static void AddMetadata(MediaSummary target, IEnumerable<MetadataItem>? metadata)
    {
        if (metadata is null)
        {
            return;
        }

        foreach (var item in metadata)
        {
            target.Metadata.Add(new KeyValue { Key = item.key, Value = item.value });
        }
    }

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
        AddMetadata(enriched, metadata);

        return await Task.FromResult(enriched);
    }

    /// <summary>
    /// Singleton instance.
    /// </summary>
    public static readonly MediaSummaryStatisticsEnricher Instance = new();
}
