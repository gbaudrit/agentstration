using Agentstration.Application.Work;
using Agentstration.Flow.Application;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.Resources;
using Agentstration.Web.Security;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Agentstration.Work.Storage.Abstractions;

namespace Agentstration.Web;

public static partial class WorkplaceEndpoints
{
    public static async Task<IResult> ListWorkspacesAsync(IdentityExperienceService service, CancellationToken token)
    {
        var context = await service.GetContextAsync(token);
        return Results.Ok(context.AvailableWorkspaces
            .Where(value => value.Id == context.Context.WorkspaceId)
            .Select(value => new WorkplaceWorkspaceResponse(value.Id, value.Name, value.DisplayName, context.TenantName, context.TenantDisplayName, context.UserDisplayName)));
    }

    private static async Task<IResult> GetWorkspaceAsync(string workspaceName, IdentityExperienceService service, CancellationToken token)
    {
        var context = await service.GetContextAsync(token);
        var workspace = context.AvailableWorkspaces.FirstOrDefault(value =>
            value.Id == context.Context.WorkspaceId
            && (string.Equals(value.Id.ToString("D"), workspaceName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(value.Name, workspaceName, StringComparison.Ordinal)));
        if (workspace is null) return Results.NotFound();
        return Results.Ok(new WorkplaceWorkspaceResponse(
            workspace.Id,
            workspace.Name,
            workspace.DisplayName,
            context.TenantName,
            context.TenantDisplayName,
            context.UserDisplayName));
    }

    private static Task<IResult> ListDashboardsAsync(string workspaceName, WorkplaceService service, CancellationToken token) => ExecuteAsync(async () => Results.Ok((await service.ListDashboardsAsync(WorkspaceId(workspaceName), token)).Select(ToResponse)));

    private static Task<IResult> GetDashboardAsync(string workspaceName, string dashboardName, WorkplaceService service, CancellationToken token) => ExecuteAsync(async () => Results.Ok(ToResponse(await service.GetDashboardAsync(WorkspaceId(workspaceName), DashboardResourceId(dashboardName), token))));

    private static Task<IResult> GetDefaultDashboardAsync(string workspaceName, WorkplaceService service, DashboardAdministrationService administration, CancellationToken token) => ExecuteAsync(async () =>
    {
        var workspaceId = WorkspaceId(workspaceName);
        try { return Results.Ok(ToResponse(await service.GetDefaultDashboardAsync(workspaceId, token))); }
        catch (KeyNotFoundException) { return Results.Ok(ToResponse(await administration.EnsureHomeAsync(workspaceId, token))); }
    });

    private static async Task<IResult> ListEntriesAsync(WorkplaceService service, CancellationToken token) => Results.Ok((await service.ListEntriesAsync(token)).Select(ToResponse));

    private static Task<IResult> GetEntryAsync(string entryName, WorkplaceService service, CancellationToken token) => ExecuteAsync(async () => Results.Ok(ToResponse(await service.GetEntryAsync(EntryResourceId(entryName), token))));

    private static Task<IResult> GetNamespacedEntryAsync(string @namespace, string entryName, WorkplaceService service, CancellationToken token) => ExecuteAsync(async () => Results.Ok(ToResponse(await service.GetEntryAsync(NamespacedEntryId(@namespace, entryName), token))));
}
