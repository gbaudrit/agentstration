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

public static class WorkplaceEndpoints
{
    public static IEndpointRouteBuilder MapAgentstrationWorkplaceApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/workplace/workspaces", ListWorkspacesAsync);
        endpoints.MapGet("/api/workspaces/{workspaceName}", GetWorkspaceAsync);
        endpoints.MapGet("/api/workspaces/{workspaceName}/dashboards", ListDashboardsAsync);
        endpoints.MapGet("/api/workspaces/{workspaceName}/dashboards/{dashboardName}", GetDashboardAsync);
        endpoints.MapGet("/api/workspaces/{workspaceName}/dashboard", GetDefaultDashboardAsync);
        endpoints.MapGet("/api/entries", ListEntriesAsync);
        endpoints.MapGet("/api/entries/{entryName}", GetEntryAsync);
        endpoints.MapGet("/api/management/entries", ListEntryDraftsAsync);
        endpoints.MapGet("/api/management/entries/{entryName}", GetEntryDraftAsync);
        endpoints.MapPut("/api/management/entries/{entryName}", PutEntryDraftAsync);
        endpoints.MapPost("/api/management/entries/{entryName}/validate", ValidateEntryDraftAsync);
        endpoints.MapPost("/api/management/entries/{entryName}/publish", PublishEntryDraftAsync);
        endpoints.MapGet("/api/namespaces/{namespace}/entries/{entryName}", GetNamespacedEntryAsync);
        endpoints.MapGet("/api/namespaces/{namespace}/management/entries/{entryName}", GetNamespacedEntryDraftAsync);
        endpoints.MapPut("/api/namespaces/{namespace}/management/entries/{entryName}", PutNamespacedEntryDraftAsync);
        endpoints.MapPost("/api/namespaces/{namespace}/management/entries/{entryName}/publish", PublishNamespacedEntryDraftAsync);
        endpoints.MapGet("/api/management/entries/{entryName}/dependencies", GetEntryDependenciesAsync);
        endpoints.MapGet("/api/namespaces/{namespace}/management/entries/{entryName}/dependencies", GetNamespacedEntryDependenciesAsync);
        endpoints.MapGet("/api/resources", ListResourcesAsync);
        endpoints.MapGet("/api/management/workspaces", ListWorkspaceDraftsAsync);
        endpoints.MapGet("/api/management/workspaces/{workspaceName}", GetWorkspaceDraftAsync);
        endpoints.MapPut("/api/management/workspaces/{workspaceName}", PutWorkspaceDraftAsync);
        endpoints.MapPost("/api/management/workspaces/{workspaceName}/publish", PublishWorkspaceDraftAsync);
        endpoints.MapGet("/api/management/workspaces/{workspaceName}/dashboards", ListDashboardDraftsAsync);
        endpoints.MapGet("/api/management/workspaces/{workspaceName}/dashboards/{dashboardName}", GetDashboardDraftAsync);
        endpoints.MapPut("/api/management/workspaces/{workspaceName}/dashboards/{dashboardName}", PutDashboardDraftAsync);
        endpoints.MapPost("/api/management/workspaces/{workspaceName}/dashboards/{dashboardName}/publish", PublishDashboardDraftAsync);
        endpoints.MapDelete("/api/management/workspaces/{workspaceName}/dashboards/{dashboardName}", DeleteDashboardAsync);
        endpoints.MapPost("/api/entries/{entryName}/interactions", SubmitEntryCompatibilityAsync).RequireAuthorization(AgentstrationPolicies.CanRunFlows);
        var workspaces = endpoints.MapGroup("/api/workspaces/{workspaceName}").RequireAuthorization(AgentstrationPolicies.Authenticated);
        workspaces.MapPost("/entries/{entryName}/interactions", SubmitEntryAsync).RequireAuthorization(AgentstrationPolicies.CanRunFlows);
        workspaces.MapPost("/namespaces/{namespace}/entries/{entryName}/interactions", SubmitNamespacedEntryAsync).RequireAuthorization(AgentstrationPolicies.CanRunFlows);
        workspaces.MapGet("/interactions", ListInteractionsAsync);
        workspaces.MapGet("/interactions/{interactionId:guid}", GetInteractionAsync);
        workspaces.MapGet("/interactions/{interactionId:guid}/messages", GetMessagesAsync);
        workspaces.MapPost("/interactions/{interactionId:guid}/messages", AddMessageAsync).RequireAuthorization(AgentstrationPolicies.CanRunFlows);
        workspaces.MapGet("/interactions/{interactionId:guid}/pending-actions", GetPendingActionsAsync);
        workspaces.MapPost("/interactions/{interactionId:guid}/pending-actions/{pendingActionId:guid}/responses", RespondPendingActionAsync).RequireAuthorization(AgentstrationPolicies.CanRunFlows);
        workspaces.MapGet("/tasks", ListTasksAsync);
        workspaces.MapGet("/tasks/{taskId:guid}", GetTaskAsync);
        workspaces.MapPost("/tasks/{taskId:guid}/pause", PauseTaskAsync);
        workspaces.MapPost("/tasks/{taskId:guid}/resume", ResumeTaskAsync);
        workspaces.MapPost("/tasks/{taskId:guid}/cancel", CancelTaskAsync);
        workspaces.MapGet("/tasks/{taskId:guid}/activities", GetActivitiesAsync);
        workspaces.MapGet("/tasks/{taskId:guid}/results", GetResultsAsync);
        workspaces.MapGet("/tasks/{taskId:guid}/artifacts", GetArtifactsAsync);
        workspaces.MapGet("/tasks/{taskId:guid}/artifacts/{artifactId:guid}/content", GetArtifactContentAsync);
        workspaces.MapGet("/notifications", GetNotificationsAsync);
        workspaces.MapGet("/notifications/unread-count", GetUnreadCountAsync);
        workspaces.MapPost("/notifications/{notificationId:guid}/read", MarkReadAsync);
        workspaces.MapPost("/notifications/read-all", MarkAllReadAsync);
        return endpoints;
    }

    public static async Task<IResult> ListWorkspacesAsync(WorkplaceService service, CancellationToken token) => Results.Ok((await service.ListWorkspacesAsync(token)).Select(ToResponse));
    private static Task<IResult> GetWorkspaceAsync(string workspaceName, WorkplaceService service, CancellationToken token) => ExecuteAsync(async () => Results.Ok(ToResponse(await service.GetWorkspaceAsync(WorkspaceId(workspaceName), token))));
    private static Task<IResult> ListDashboardsAsync(string workspaceName, WorkplaceService service, CancellationToken token) => ExecuteAsync(async () => Results.Ok((await service.ListDashboardsAsync(WorkspaceId(workspaceName), token)).Select(ToResponse)));
    private static Task<IResult> GetDashboardAsync(string workspaceName, string dashboardName, WorkplaceService service, CancellationToken token) => ExecuteAsync(async () => Results.Ok(ToResponse(await service.GetDashboardAsync(WorkspaceId(workspaceName), DashboardResourceId(dashboardName), token))));
    private static Task<IResult> GetDefaultDashboardAsync(string workspaceName, WorkplaceService service, CancellationToken token) => ExecuteAsync(async () => Results.Ok(ToResponse(await service.GetDefaultDashboardAsync(WorkspaceId(workspaceName), token))));
    private static async Task<IResult> ListEntriesAsync(WorkplaceService service, CancellationToken token) => Results.Ok((await service.ListEntriesAsync(token)).Select(ToResponse));
    private static Task<IResult> GetEntryAsync(string entryName, WorkplaceService service, CancellationToken token) => ExecuteAsync(async () => Results.Ok(ToResponse(await service.GetEntryAsync(EntryResourceId(entryName), token))));
    private static Task<IResult> GetNamespacedEntryAsync(string @namespace, string entryName, WorkplaceService service, CancellationToken token) => ExecuteAsync(async () => Results.Ok(ToResponse(await service.GetEntryAsync(NamespacedEntryId(@namespace, entryName), token))));
    private static Task<IResult> SubmitEntryCompatibilityAsync(string entryName, CreateInteractionRequest request, WorkplaceService service, CancellationToken token) => SubmitCoreAsync(ParseWorkspaceId(request.WorkspaceId), EntryResourceId(entryName), request, service, token);
    private static Task<IResult> SubmitEntryAsync(string workspaceName, string entryName, CreateInteractionRequest request, WorkplaceService service, CancellationToken token) => SubmitCoreAsync(WorkspaceId(workspaceName), EntryResourceId(entryName), request, service, token);
    private static Task<IResult> SubmitNamespacedEntryAsync(string workspaceName, string @namespace, string entryName, CreateInteractionRequest request, WorkplaceService service, CancellationToken token) => SubmitCoreAsync(WorkspaceId(workspaceName), NamespacedEntryId(@namespace, entryName), request, service, token);
    private static Task<IResult> SubmitCoreAsync(WorkplaceWorkspaceId workspaceId, EntryId entryId, CreateInteractionRequest request, WorkplaceService service, CancellationToken token) => ExecuteAsync(async () =>
    {
        var attachments = request.Attachments?.Select(WorkEndpoints.ToWorkAttachment).ToArray(); var result = await service.SubmitAsync(new SubmitEntryCommand(workspaceId, entryId, request.Values, attachments), token);
        return Results.Created($"/api/workspaces/{WorkspaceName(workspaceId)}/interactions/{result.Interaction.Id}", new EntrySubmissionResponse(ToResponse(result.Interaction), result.Action, result.Task is null ? null : ToResponse(result.Task)));
    });
    private static Task<IResult> GetInteractionAsync(string workspaceName, Guid interactionId, WorkplaceService service, CancellationToken token) => ExecuteAsync(async () => Results.Ok(ToResponse(await service.GetInteractionAsync(WorkspaceId(workspaceName), new(interactionId), token))));
    private static Task<IResult> ListInteractionsAsync(string workspaceName, int? take, WorkplaceService service, CancellationToken token) => ExecuteAsync(async () => Results.Ok(new InteractionPageResponse((await service.ListInteractionsAsync(WorkspaceId(workspaceName), take ?? 20, token)).Select(ToResponse).ToArray())));
    private static Task<IResult> GetMessagesAsync(string workspaceName, Guid interactionId, WorkplaceService service, CancellationToken token) => ExecuteAsync(async () => Results.Ok(await service.ListMessagesAsync(WorkspaceId(workspaceName), new(interactionId), token)));
    private static Task<IResult> AddMessageAsync(string workspaceName, Guid interactionId, AddConversationMessageRequest request, WorkplaceService service, CancellationToken token) => ExecuteAsync(async () =>
    {
        var result = await service.AddMessageAsync(WorkspaceId(workspaceName), new(interactionId), request.Content, token);
        return Results.Accepted($"/api/workspaces/{workspaceName}/interactions/{interactionId}", new AddConversationMessageResponse(result.Message, ToResponse(result.Interaction), result.Action, result.Task is null ? null : ToResponse(result.Task)));
    });
    private static Task<IResult> GetPendingActionsAsync(string workspaceName, Guid interactionId, WorkplaceService service, CancellationToken token) => ExecuteAsync(async () => Results.Ok((await service.ListPendingActionsAsync(WorkspaceId(workspaceName), new(interactionId), token)).Select(WorkplaceService.ToContract)));
    private static Task<IResult> RespondPendingActionAsync(string workspaceName, Guid interactionId, Guid pendingActionId, PendingActionResponseRequest request, WorkplaceService service, CancellationToken token) => ExecuteAsync(async () =>
    {
        var result = await service.RespondAsync(WorkspaceId(workspaceName), new(interactionId), new(pendingActionId), request.ResumeToken, request.Values, token);
        return Results.Ok(new PendingActionResolutionResponse(WorkplaceService.ToContract(result.PendingAction), result.NextAction, ToResponse(result.Interaction), result.Task is null ? null : ToResponse(result.Task)));
    });
    private static Task<IResult> ListTasksAsync(string workspaceName, WorkTaskStatus? status, WorkplaceService service, CancellationToken token) => ExecuteAsync(async () => Results.Ok(new WorkTaskPageResponse((await service.ListTasksAsync(WorkspaceId(workspaceName), status, token)).Select(ToResponse).ToArray())));
    private static Task<IResult> GetTaskAsync(string workspaceName, Guid taskId, WorkplaceService service, CancellationToken token) => ExecuteAsync(async () => Results.Ok(ToResponse(await service.GetTaskAsync(WorkspaceId(workspaceName), new(taskId), token))));
    private static Task<IResult> PauseTaskAsync(string workspaceName, Guid taskId, WorkplaceService service, CancellationToken token) => ExecuteAsync(async () => Results.Ok(ToResponse(await service.PauseTaskAsync(WorkspaceId(workspaceName), new(taskId), token))));
    private static Task<IResult> ResumeTaskAsync(string workspaceName, Guid taskId, WorkplaceService service, CancellationToken token) => ExecuteAsync(async () => Results.Ok(ToResponse(await service.ResumeTaskAsync(WorkspaceId(workspaceName), new(taskId), token))));
    private static Task<IResult> CancelTaskAsync(string workspaceName, Guid taskId, WorkplaceService service, CancellationToken token) => ExecuteAsync(async () => Results.Ok(ToResponse(await service.CancelTaskAsync(WorkspaceId(workspaceName), new(taskId), token))));
    private static Task<IResult> GetActivitiesAsync(string workspaceName, Guid taskId, WorkplaceService service, CancellationToken token) => ExecuteAsync(async () => { await service.GetTaskAsync(WorkspaceId(workspaceName), new(taskId), token); return Results.Ok(await service.ListActivitiesAsync(WorkspaceId(workspaceName), new(taskId), token)); });
    private static Task<IResult> GetResultsAsync(string workspaceName, Guid taskId, WorkplaceService service, CancellationToken token) => ExecuteAsync(async () => { await service.GetTaskAsync(WorkspaceId(workspaceName), new(taskId), token); return Results.Ok(await service.ListResultsAsync(WorkspaceId(workspaceName), new(taskId), token)); });
    private static Task<IResult> GetArtifactsAsync(string workspaceName, Guid taskId, WorkplaceService service, CancellationToken token) => ExecuteAsync(async () => { await service.GetTaskAsync(WorkspaceId(workspaceName), new(taskId), token); return Results.Ok(await service.ListArtifactsAsync(WorkspaceId(workspaceName), new(taskId), token)); });
    private static Task<IResult> GetArtifactContentAsync(string workspaceName, Guid taskId, Guid artifactId, WorkplaceService service, IArtifactStore store, CancellationToken token) => ExecuteAsync(async () => { var value = await service.GetArtifactAsync(WorkspaceId(workspaceName), new(taskId), new(artifactId), token); var stream = await store.OpenReadAsync(new ArtifactReference(value.StorageKey, value.ContentType, value.Length), token); return Results.File(stream, value.ContentType, value.Name, enableRangeProcessing: true); });
    private static Task<IResult> GetNotificationsAsync(string workspaceName, bool? unreadOnly, WorkplaceService service, CancellationToken token) => ExecuteAsync(async () => Results.Ok(new WorkNotificationPageResponse(await service.ListNotificationsAsync(WorkspaceId(workspaceName), unreadOnly, token))));
    private static Task<IResult> GetUnreadCountAsync(string workspaceName, WorkplaceService service, CancellationToken token) => ExecuteAsync(async () => Results.Ok(new UnreadNotificationCountResponse(await service.UnreadCountAsync(WorkspaceId(workspaceName), token))));
    private static Task<IResult> MarkReadAsync(string workspaceName, Guid notificationId, WorkplaceService service, CancellationToken token) => ExecuteAsync(async () => Results.Ok(await service.MarkNotificationReadAsync(WorkspaceId(workspaceName), new(notificationId), token)));
    private static Task<IResult> MarkAllReadAsync(string workspaceName, WorkplaceService service, CancellationToken token) => ExecuteAsync(async () => { await service.MarkAllNotificationsReadAsync(WorkspaceId(workspaceName), token); return Results.NoContent(); });

    private static WorkplaceWorkspaceResponse ToResponse(WorkplaceWorkspace value) => new(value.Id.Value, value.Name, value.Type, value.ApiVersion, value.DisplayName, value.Description, value.Version, value.PublishedAt) { Namespace = value.Id.Namespace };
    private static WorkplaceDashboardResponse ToResponse(WorkplaceDashboard value) => new(value.Id.Value, value.WorkspaceId.Value, value.Name, value.Type, value.ApiVersion, value.DisplayName, value.Description, value.IsDefault, value.Entries.Select(reference => new DashboardEntryReferenceResponse(reference.EntryResourceId.Value, reference.Role, reference.Order) { Namespace = reference.EntryResourceId.Namespace }).ToArray(), value.Version, value.PublishedAt);
    private static EntryResponse ToResponse(EntryResource value) => new(value.Id.Value, value.Name, value.Type, value.ApiVersion, value.DisplayName, value.Description, value.Presentation, value.ResolvedTarget, value.Behavior, value.Version, value.PublishedAt) { Namespace = value.Id.Namespace };

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

    private static async Task<IResult> ListResourcesAsync(string kind, AgentManagementService agents, FlowService flows, CancellationToken token)
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
            var page = await flows.ListAsync(0, 500, token);
            return Results.Ok(page.Items.Where(value => !value.Value.Metadata.TryGetValue("systemManaged", out var system) || !bool.TryParse(system, out var hidden) || !hidden)
                .Select(value => new ResourcePickerItem(value.Value.Id.Value, value.Value.DisplayName ?? value.Value.Name, value.Value.Description, value.Value.ActiveVersion ?? value.Value.Version, value.Value.Enabled ? "Active" : "Disabled", kind) { Namespace = value.Value.Id.Namespace }));
        }
        return Results.Problem(statusCode: 400, title: "resource_kind_not_supported", detail: "Only Agent and Flow resources can be selected for an Entry.");
    }

    private static async Task<IResult> ListWorkspaceDraftsAsync(WorkspaceAdministrationService service, WorkplaceService workplace, CancellationToken token)
    {
        var values = new List<WorkplaceWorkspaceDraftResponse>();
        foreach (var draft in await service.ListAsync(token))
        {
            WorkplaceWorkspace? published = null;
            try { published = await workplace.GetWorkspaceAsync(draft.Id, token); } catch (KeyNotFoundException) { }
            values.Add(new WorkplaceWorkspaceDraftResponse(draft, published));
        }
        return Results.Ok(values);
    }

    private static Task<IResult> GetWorkspaceDraftAsync(string workspaceName, WorkspaceAdministrationService service, WorkplaceService workplace, CancellationToken token) => ExecuteAsync(async () =>
    {
        var draft = await service.GetAsync(WorkspaceId(workspaceName), token);
        WorkplaceWorkspace? published = null;
        try { published = await workplace.GetWorkspaceAsync(draft.Id, token); } catch (KeyNotFoundException) { }
        return Results.Ok(new WorkplaceWorkspaceDraftResponse(draft, published));
    });

    private static Task<IResult> PutWorkspaceDraftAsync(string workspaceName, WorkplaceWorkspaceDraft draft, WorkspaceAdministrationService service, CancellationToken token) => ExecuteAsync(async () =>
    {
        if (!string.Equals(draft.Name, workspaceName, StringComparison.Ordinal) || draft.Id != WorkspaceId(workspaceName))
            throw new WorkValidationException("workspace_identity_mismatch", "The Workspace route, name and resource id must match.");
        return Results.Ok(await service.SaveAsync(draft, token));
    });

    private static Task<IResult> PublishWorkspaceDraftAsync(string workspaceName, WorkspaceAdministrationService service, CancellationToken token) =>
        ExecuteAsync(async () => Results.Ok(await service.PublishAsync(WorkspaceId(workspaceName), token)));

    private static async Task<IResult> ListDashboardDraftsAsync(string workspaceName, DashboardAdministrationService service, WorkplaceService workplace, CancellationToken token)
    {
        var workspaceId = WorkspaceId(workspaceName);
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
    private static InteractionResponse ToResponse(WorkplaceInteraction value) => new(value.Id.Value, value.WorkspaceId.Value, value.EntryId.Value, value.Status, value.StartedAt, value.LastActivityAt, value.InputValues, value.Attachments, value.Messages, value.PendingActionId?.Value, value.TaskId?.Value, value.ImmediateResult, value.Version, value.LastFlowRunId, value.LastTriggerMessageId);
    private static WorkTaskResponse ToResponse(WorkTask value) => new(value.Id.Value, value.WorkspaceId.Value, value.EntryId.Value, value.InteractionId.Value, value.Title, value.Description, value.Status, value.CreatedAt, value.UpdatedAt, value.FlowRunId, value.Conversation, value.Activities, value.Artifacts, value.Result, value.Error, WorkplaceService.CurrentAction(value), value.Version);
    private static WorkplaceWorkspaceId ParseWorkspaceId(string value) => WorkspaceId(value);
    private static WorkplaceWorkspaceId WorkspaceId(string name) => new(name);
    private static DashboardId DashboardResourceId(string name) => new(name);
    private static EntryId EntryResourceId(string name) => new(name);
    private static EntryId NamespacedEntryId(string @namespace, string name) => new(name, ResourceNamespace.Parse(@namespace));
    private static string WorkspaceName(WorkplaceWorkspaceId id) => id.Value;
    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action) { try { return await action(); } catch (KeyNotFoundException exception) { return Results.Problem(statusCode: 404, title: "workplace_resource_not_found", detail: exception.Message); } catch (WorkValidationException exception) { return Results.Problem(statusCode: 400, title: exception.Code, detail: exception.Message); } catch (WorkTransitionException exception) { return Results.Problem(statusCode: 409, title: exception.Code, detail: exception.Message); } catch (WorkplaceConcurrencyException exception) { return Results.Problem(statusCode: 412, title: "precondition_failed", detail: exception.Message); } }
}
