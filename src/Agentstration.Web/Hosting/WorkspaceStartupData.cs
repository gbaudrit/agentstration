using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;

namespace Agentstration.Web.Hosting;

public static class WorkspaceStartupData
{
    public static async Task InitializeAsync(
        IServiceProvider services,
        RequestContext bootstrapContext,
        bool includeInteractiveDemo,
        CancellationToken cancellationToken)
    {
        var identityStore = services.GetRequiredService<IIdentityStore>();
        var scopeFactory = services.GetRequiredService<IRequestContextScopeFactory>();
        var standardData = services.GetRequiredService<StandardRuntimeProfileSeeder>();
        var activeWorkspaces = (await identityStore.ListWorkspacesAsync(bootstrapContext.TenantId, cancellationToken))
            .Where(workspace => workspace.Status == WorkspaceStatus.Active)
            .ToArray();

        foreach (var workspace in activeWorkspaces)
        {
            using var workspaceScope = scopeFactory.Push(bootstrapContext with { WorkspaceId = workspace.Id });
            await standardData.EnsureAsync(cancellationToken);
        }

        if (!activeWorkspaces.Any(workspace => workspace.Id == bootstrapContext.WorkspaceId)) return;

        using var bootstrapWorkspaceScope = scopeFactory.Push(bootstrapContext);
        await ManagementDemoData.SeedAsync(services, cancellationToken);
        if (includeInteractiveDemo)
            await InteractiveFlowDemoData.SeedAsync(services, cancellationToken);
        await WorkplaceDemoData.SeedAsync(services, cancellationToken);
    }
}
