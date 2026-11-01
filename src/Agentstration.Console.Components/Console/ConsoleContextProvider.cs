using Agentstration.Web.Components.State;

namespace Agentstration.Web.Console;

public sealed class ConsoleContextProvider(IIdentityAdministrationApiClient client) : IConsoleContextProvider
{
    public async Task<ConsoleContextSnapshot> GetAsync(CancellationToken cancellationToken)
    {
        var view = await client.GetContextAsync(cancellationToken);
        return new ConsoleContextSnapshot(
            view.Context.PrincipalId,
            view.UserDisplayName,
            view.SelectedScopeRef,
            Enum.Parse<ConsoleContextKind>(view.SelectedKind.ToString()),
            view.Context.TenantId,
            view.TenantName,
            view.TenantDisplayName,
            view.Context.WorkspaceId,
            view.WorkspaceName,
            view.WorkspaceDisplayName,
            new HashSet<string>(view.Permissions, StringComparer.Ordinal),
            view.AvailableWorkspaces.Select(workspace => new ConsoleWorkspaceOption(
                workspace.Id,
                workspace.TenantId,
                workspace.TenantName,
                workspace.TenantDisplayName,
                workspace.Name,
                workspace.DisplayName)).ToArray(),
            view.AvailableContexts.Select(context => new ConsoleContextOption(
                context.ScopeRef,
                Enum.Parse<ConsoleContextKind>(context.Kind.ToString()),
                context.TenantId,
                context.TenantName,
                context.TenantDisplayName,
                context.WorkspaceId,
                context.WorkspaceName,
                context.DisplayName)).ToArray());
    }
}
