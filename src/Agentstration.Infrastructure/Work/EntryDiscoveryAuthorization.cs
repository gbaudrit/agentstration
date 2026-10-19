using Agentstration.Application.Work;
using Agentstration.Identity;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;

namespace Agentstration.Infrastructure.Work;

public sealed class EntryDiscoveryAuthorization(IdentityExperienceService identities) : IEntryDiscoveryAuthorization
{
    public async Task<IReadOnlyList<EntryDiscoveryWorkspace>> ListReadableWorkspacesInCurrentTenantAsync(
        CancellationToken cancellationToken)
    {
        var context = await identities.GetContextAsync(cancellationToken);
        return context.AvailableWorkspaces
            .Where(value => value.TenantId == context.Context.TenantId
                && value.Permissions.Contains(AuthorizationPermissions.ResourcesRead, StringComparer.Ordinal))
            .Select(value => new EntryDiscoveryWorkspace(
                new WorkspaceId(value.Id),
                value.Id == context.Context.WorkspaceId))
            .ToArray();
    }
}
