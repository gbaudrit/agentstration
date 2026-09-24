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

internal sealed record AgentToolCategoryGroup(
    string Name,
    string DisplayName,
    IReadOnlyList<ToolResource> Tools,
    IReadOnlyList<ToolResource> AssignableTools)
{
    public static IReadOnlyList<AgentToolCategoryGroup> Create(
        IReadOnlyList<ToolCategoryResource> categories,
        IReadOnlyList<ToolResource> tools)
    {
        var toolsByName = tools.ToDictionary(tool => tool.Name, StringComparer.Ordinal);
        return categories.Select(category =>
        {
            var members = category.Definition.Tools
                .Select(reference => toolsByName.GetValueOrDefault(reference.Name))
                .Where(tool => tool is not null)
                .Cast<ToolResource>()
                .DistinctBy(tool => tool.Name, StringComparer.Ordinal)
                .OrderBy(tool => tool.Definition.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            return new AgentToolCategoryGroup(
                category.Name,
                category.Definition.DisplayName,
                members,
                members.Where(tool => tool.Definition.Enabled && tool.Definition.Discovery?.Available == true).ToArray());
        }).Where(category => category.Tools.Count > 0)
          .OrderBy(category => category.DisplayName, StringComparer.CurrentCultureIgnoreCase)
          .ToArray();
    }
}
