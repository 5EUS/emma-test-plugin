using EMMA.Plugin.Common;

namespace EMMA.TestPlugin.Core;

internal static class MangadexPluginBundle
{
    public static readonly PluginProviderBundle<MangadexProviderClient, ProviderSearchQueryResolver, MangadexSearchSuggestionProvider> Instance =
        new(
            MangadexProviderClient.Instance,
            ProviderSearchQueryResolver.Instance,
            MangadexSearchSuggestionProvider.Instance);
}