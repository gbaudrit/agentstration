using Agentstration.Resources;
using Agentstration.Tools;

namespace Agentstration.Web.Components.Pages;

internal sealed record AgentToolProviderGroup(
    string ProviderName,
    ResourceNamespace ProviderNamespace,
    ResourceScopeRef? ProviderScopeRef,
    string DisplayName,
    bool HasProvider,
    IReadOnlyList<ToolResource> Tools)
{
    public static IReadOnlyList<AgentToolProviderGroup> Create(
        IReadOnlyList<ToolResource> tools,
        IReadOnlyList<ToolProviderResource> providers,
        string unknownProviderLabel)
    {
        var providersByIdentity = providers
            .GroupBy(ProviderIdentity, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        return tools
            .GroupBy(ToolProviderIdentity, StringComparer.Ordinal)
            .Select(group =>
            {
                var firstTool = group.First();
                var reference = firstTool.Definition.Provider;
                var providerName = reference?.Name ?? string.Empty;
                var providerNamespace = reference?.Namespace ?? firstTool.Namespace;
                var providerScopeRef = reference?.ScopeRef ?? firstTool.ScopeRef;
                var hasProvider = providersByIdentity.TryGetValue(Identity(providerScopeRef, providerNamespace, providerName), out var provider);
                var displayName = hasProvider ? provider!.Definition.DisplayName : string.IsNullOrWhiteSpace(providerName) ? unknownProviderLabel : providerName;
                return new AgentToolProviderGroup(
                    providerName,
                    providerNamespace,
                    providerScopeRef,
                    displayName,
                    hasProvider,
                    group.OrderBy(tool => tool.Definition.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray());
            })
            .OrderBy(group => group.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static string ProviderIdentity(ToolProviderResource provider) => Identity(provider.ScopeRef, provider.Namespace, provider.Name);
    private static string ToolProviderIdentity(ToolResource tool)
    {
        var reference = tool.Definition.Provider;
        return Identity(reference?.ScopeRef ?? tool.ScopeRef, reference?.Namespace ?? tool.Namespace, reference?.Name ?? string.Empty);
    }
    private static string Identity(ResourceScopeRef? scopeRef, ResourceNamespace @namespace, string name) =>
        $"{scopeRef?.Value ?? string.Empty}|{@namespace.Value}|{name}";
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
        return categories.Select(category =>
        {
            var members = category.Definition.Tools
                .Select(reference => Resolve(reference, category, tools))
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

    private static ToolResource? Resolve(ResourceReference reference, ToolCategoryResource category, IReadOnlyList<ToolResource> tools)
    {
        var @namespace = reference.Namespace ?? category.Namespace;
        var candidates = tools.Where(tool => tool.Namespace == @namespace
            && string.Equals(tool.Name, reference.Name, StringComparison.Ordinal));
        if (reference.ScopeRef is { } scopeRef) return candidates.FirstOrDefault(tool => tool.ScopeRef == scopeRef);
        return candidates.FirstOrDefault(tool => tool.ScopeRef == category.ScopeRef) ?? candidates.FirstOrDefault();
    }
}
