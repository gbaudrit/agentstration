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
    private static async Task<IResult> ListEntryDraftsAsync(EntryAdministrationService service, WorkplaceService workplace, CancellationToken token)
    {
        var values = new List<EntryDraftResponse>();
        foreach (var draft in await service.ListAsync(token))
        {
            EntryResource? published = null;
            try { published = await workplace.GetEntryAsync(draft.Id, token); } catch (KeyNotFoundException) { }
            values.Add(new EntryDraftResponse(draft, published));
        }
        return Results.Ok(values);
    }

    private static Task<IResult> GetEntryDraftAsync(string entryName, EntryAdministrationService service, WorkplaceService workplace, CancellationToken token) => ExecuteAsync(async () =>
    {
        var draft = await service.GetAsync(EntryResourceId(entryName), token);
        EntryResource? published = null;
        try { published = await workplace.GetEntryAsync(draft.Id, token); } catch (KeyNotFoundException) { }
        return Results.Ok(new EntryDraftResponse(draft, published));
    });

    private static Task<IResult> GetNamespacedEntryDraftAsync(string @namespace, string entryName, EntryAdministrationService service, WorkplaceService workplace, CancellationToken token) => ExecuteAsync(async () =>
    {
        var id = NamespacedEntryId(@namespace, entryName);
        var draft = await service.GetAsync(id, token);
        EntryResource? published = null;
        try { published = await workplace.GetEntryAsync(id, token); } catch (KeyNotFoundException) { }
        return Results.Ok(new EntryDraftResponse(draft, published));
    });

    private static Task<IResult> PutEntryDraftAsync(string entryName, EntryDraft draft, EntryAdministrationService service, CancellationToken token) => ExecuteAsync(async () =>
    {
        if (!string.Equals(draft.Name, entryName, StringComparison.Ordinal) || draft.Id != EntryResourceId(entryName))
            throw new WorkValidationException("entry_identity_mismatch", "The Entry route, name and resource id must match.");
        return Results.Ok(await service.SaveAsync(draft, token));
    });

    private static Task<IResult> PutNamespacedEntryDraftAsync(string @namespace, string entryName, EntryDraft draft, EntryAdministrationService service, CancellationToken token) => ExecuteAsync(async () =>
    {
        var id = NamespacedEntryId(@namespace, entryName);
        if (!string.Equals(draft.Name, entryName, StringComparison.Ordinal) || draft.Id != id)
            throw new WorkValidationException("entry_identity_mismatch", "The Entry route, namespace, name and resource id must match.");
        return Results.Ok(await service.SaveAsync(draft, token));
    });

    private static Task<IResult> ValidateEntryDraftAsync(string entryName, EntryAdministrationService service, CancellationToken token) => ExecuteAsync(async () =>
    {
        var result = await service.ValidateAsync(EntryResourceId(entryName), token);
        return Results.Ok(new EntryValidationResponse(result.IsValid, result.Issues.Select(value => new EntryValidationIssueContract(value.Code, value.Message)).ToArray()));
    });

    private static Task<IResult> PublishEntryDraftAsync(string entryName, EntryAdministrationService service, CancellationToken token) =>
        ExecuteAsync(async () => Results.Ok(await service.PublishAsync(EntryResourceId(entryName), token)));

    private static Task<IResult> PublishNamespacedEntryDraftAsync(string @namespace, string entryName, EntryAdministrationService service, CancellationToken token) =>
        ExecuteAsync(async () => Results.Ok(await service.PublishAsync(NamespacedEntryId(@namespace, entryName), token)));

    private static Task<IResult> GetEntryDependenciesAsync(string entryName, EntryAdministrationService service, CancellationToken token) => ExecuteAsync(async () =>
        Results.Ok((await service.GetDependenciesAsync(EntryResourceId(entryName), token)).Select(value => new EntryDependencyResponse(value.ResourceId, value.ResourceType, value.Relationship))));

    private static Task<IResult> GetNamespacedEntryDependenciesAsync(string @namespace, string entryName, EntryAdministrationService service, CancellationToken token) => ExecuteAsync(async () =>
        Results.Ok((await service.GetDependenciesAsync(NamespacedEntryId(@namespace, entryName), token)).Select(value => new EntryDependencyResponse(value.ResourceId, value.ResourceType, value.Relationship))));

    private static async Task<IResult> ListResourcesAsync(string kind, AgentManagementService agents, FlowService flows, IWorkplaceContext workplaceContext, CancellationToken token)
    {
        if (string.Equals(kind, ResourceKinds.Agent, StringComparison.Ordinal))
        {
            var values = await agents.ListAgentsAsync(0, 500, token);
            return Results.Ok(values.Select(value => new ResourcePickerItem(value.Value.Metadata.Name, value.Value.Definition.DisplayName, value.Value.Definition.Description, value.Value.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture), value.Value.Status.ProvisioningState.ToString(), kind,
                new Dictionary<string, string> { ["modelProfile"] = value.Value.Definition.ModelProfile.ResourceId })
            { Namespace = value.Value.Namespace }));
        }
        if (string.Equals(kind, ResourceKinds.Flow, StringComparison.Ordinal))
        {
            var page = await flows.ListAsync(workplaceContext.WorkspaceId, 0, 500, token);
            return Results.Ok(page.Items.Where(value => !value.Value.Metadata.TryGetValue("systemManaged", out var system) || !bool.TryParse(system, out var hidden) || !hidden)
                .Select(value => new ResourcePickerItem(value.Value.Id.Value, value.Value.DisplayName ?? value.Value.Name, value.Value.Description, value.Value.ActiveVersion ?? value.Value.Version, value.Value.Enabled ? "Active" : "Disabled", kind) { Namespace = value.Value.Id.Namespace }));
        }
        return Results.Problem(statusCode: 400, title: "resource_kind_not_supported", detail: "Only Agent and Flow resources can be selected for an Entry.");
    }

    private static async Task<IResult> ListDashboardDraftsAsync(string workspaceName, DashboardAdministrationService service, WorkplaceService workplace, CancellationToken token)
    {
        var workspaceId = WorkspaceId(workspaceName);
        if ((await service.ListAsync(workspaceId, token)).Count == 0)
            await service.EnsureHomeAsync(workspaceId, token);
        var values = new List<WorkplaceDashboardDraftResponse>();
        foreach (var draft in await service.ListAsync(workspaceId, token))
        {
            WorkplaceDashboard? published = null;
            try { published = await workplace.GetDashboardAsync(workspaceId, draft.Id, token); } catch (KeyNotFoundException) { }
            values.Add(new WorkplaceDashboardDraftResponse(draft, published));
        }
        return Results.Ok(values);
    }

    private static Task<IResult> GetDashboardDraftAsync(string workspaceName, string dashboardName, DashboardAdministrationService service, WorkplaceService workplace, CancellationToken token) => ExecuteAsync(async () =>
    {
        var workspaceId = WorkspaceId(workspaceName);
        var draft = await service.GetAsync(workspaceId, DashboardResourceId(dashboardName), token);
        WorkplaceDashboard? published = null;
        try { published = await workplace.GetDashboardAsync(workspaceId, draft.Id, token); } catch (KeyNotFoundException) { }
        return Results.Ok(new WorkplaceDashboardDraftResponse(draft, published));
    });

    private static Task<IResult> PutDashboardDraftAsync(string workspaceName, string dashboardName, WorkplaceDashboardDraft draft, DashboardAdministrationService service, CancellationToken token) => ExecuteAsync(async () =>
    {
        var workspaceId = WorkspaceId(workspaceName);
        var dashboardId = DashboardResourceId(dashboardName);
        if (draft.WorkspaceId != workspaceId || draft.Id != dashboardId || !string.Equals(draft.Name, dashboardName, StringComparison.Ordinal))
            throw new WorkValidationException("dashboard_identity_mismatch", "The Dashboard route, Workspace, name and resource id must match.");
        return Results.Ok(await service.SaveAsync(draft, token));
    });

    private static Task<IResult> PublishDashboardDraftAsync(string workspaceName, string dashboardName, DashboardAdministrationService service, CancellationToken token) =>
        ExecuteAsync(async () => Results.Ok(await service.PublishAsync(WorkspaceId(workspaceName), DashboardResourceId(dashboardName), token)));

    private static Task<IResult> DeleteDashboardAsync(string workspaceName, string dashboardName, DashboardAdministrationService service, CancellationToken token) => ExecuteAsync(async () =>
    {
        await service.DeleteAsync(WorkspaceId(workspaceName), DashboardResourceId(dashboardName), token);
        return Results.NoContent();
    });
}
