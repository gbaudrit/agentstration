using Agentstration.Tools;

namespace Agentstration.Web.Components.Pages;

internal sealed record AgentToolProviderGroup(
    string ProviderName,
    string DisplayName,
    bool HasProvider,
    IReadOnlyList<ToolResource> Tools)
{
    public static IReadOnlyList<AgentToolProviderGroup> Create(
        IReadOnlyList<ToolResource> tools,
        IReadOnlyList<ToolProviderResource> providers,
        string unknownProviderLabel)
    {
        var providersByName = providers.ToDictionary(provider => provider.Metadata.Name, StringComparer.Ordinal);

        return tools
            .GroupBy(tool => tool.Definition.Provider?.Name ?? string.Empty, StringComparer.Ordinal)
            .Select(group =>
            {
                var hasProvider = providersByName.TryGetValue(group.Key, out var provider);
                var displayName = hasProvider ? provider!.Definition.DisplayName : string.IsNullOrWhiteSpace(group.Key) ? unknownProviderLabel : group.Key;
                return new AgentToolProviderGroup(
                    group.Key,
                    displayName,
                    hasProvider,
                    group.OrderBy(tool => tool.Definition.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray());
            })
            .OrderBy(group => group.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }
}
