using Agentstration.Management.Core;
using Agentstration.Web.Components.State;

namespace Agentstration.Web.Console;

public sealed class ConsoleContextProvider(IdentityExperienceService experience) : IConsoleContextProvider
{
    public async Task<ConsoleContextSnapshot> GetAsync(CancellationToken cancellationToken)
    {
        var view = await experience.GetContextAsync(cancellationToken);
        return new ConsoleContextSnapshot(
            view.Context.PrincipalId,
            view.UserDisplayName,
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
                workspace.DisplayName)).ToArray());
    }
}
