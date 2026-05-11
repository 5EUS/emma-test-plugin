using EMMA.Plugin.Common;

namespace EMMA.TestPlugin.Core;

internal sealed class MangadexSearchSuggestionProvider : PluginSearchSuggestionProviderBase
{
    public static readonly MangadexSearchSuggestionProvider Instance = new();

    protected override async Task<IReadOnlyList<SearchSuggestionItem>> GetSuggestionsCoreAsync(
        SearchSuggestionRequest request,
        PluginPayloadSource payloadSource,
        int limit,
        HashSet<string> excludedValues,
        CancellationToken cancellationToken)
    {
        return request.ControlId switch
        {
            "core.tags" or "core.tags.exclude" => FilterLookupSuggestions(
                await ProviderSearchQueryResolver.Instance.GetTagLookupAsync(payloadSource, cancellationToken),
                request.Query,
                limit,
                excludedValues),

            "core.author" or "core.artist" => await ProviderSearchQueryResolver.Instance.GetAuthorOrArtistSuggestionsAsync(
                request.Query,
                limit,
                excludedValues,
                payloadSource,
                cancellationToken),

            _ => []
        };
    }
}