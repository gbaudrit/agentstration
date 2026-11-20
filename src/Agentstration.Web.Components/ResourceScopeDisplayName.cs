using Agentstration.Resources;
using Agentstration.Web.Components.State;

namespace Agentstration.Web.Components;

internal static class ResourceScopeDisplayName
{
    // Resolve only names already authorized in a supplied inventory or context snapshot.
    public static string? Resolve(ResourceScopeRef? scopeRef, string? displayName, ConsoleContextSnapshot? console, WorkplaceContextSnapshot? workplace)
    {
        if (!string.IsNullOrWhiteSpace(displayName)) return displayName;
        if (scopeRef is not { } scope || scope.Kind == ResourceScopeKind.Instance) return null;
        var option = console?.Contexts.FirstOrDefault(value => value.ScopeRef == scope.Value);
        if (option is not null) return option.DisplayName;
        if (console is not null && scope.Value == console.SelectedScopeRef)
            return scope.Kind == ResourceScopeKind.Tenant ? console.TenantDisplayName : console.WorkspaceDisplayName;
        if (workplace is { WorkspaceId: { } workspaceId } && scope == ResourceScopeRef.Workspace(workspaceId))
            return workplace.WorkspaceDisplayName;
        return null;
    }
}
