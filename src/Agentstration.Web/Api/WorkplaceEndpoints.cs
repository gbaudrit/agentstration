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
    public static IEndpointRouteBuilder MapAgentstrationWorkplaceApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/workplace/workspaces", ListWorkspacesAsync)
            .RequireAuthorization(AgentstrationPolicies.WorkspaceReader);
        var workspaces = endpoints.MapGroup("/api/workspaces/{workspaceName}")
            .RequireAuthorization(AgentstrationPolicies.Authenticated)
            .AddEndpointFilter(RequireCurrentWorkspaceAsync);
        workspaces.MapGet("", GetWorkspaceAsync).RequireAuthorization(AgentstrationPolicies.WorkspaceReader);
        workspaces.MapGet("/dashboards", ListDashboardsAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        workspaces.MapGet("/dashboards/{dashboardName}", GetDashboardAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        workspaces.MapGet("/dashboard", GetDefaultDashboardAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        endpoints.MapGet("/api/entries", ListEntriesAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        endpoints.MapGet("/api/entries/{entryName}", GetEntryAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        endpoints.MapGet("/api/management/entries", ListEntryDraftsAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        endpoints.MapGet("/api/management/entries/{entryName}", GetEntryDraftAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        endpoints.MapPut("/api/management/entries/{entryName}", PutEntryDraftAsync).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        endpoints.MapPost("/api/management/entries/{entryName}/validate", ValidateEntryDraftAsync).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        endpoints.MapPost("/api/management/entries/{entryName}/publish", PublishEntryDraftAsync).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        endpoints.MapGet("/api/namespaces/{namespace}/entries/{entryName}", GetNamespacedEntryAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        endpoints.MapGet("/api/namespaces/{namespace}/management/entries/{entryName}", GetNamespacedEntryDraftAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        endpoints.MapPut("/api/namespaces/{namespace}/management/entries/{entryName}", PutNamespacedEntryDraftAsync).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        endpoints.MapPost("/api/namespaces/{namespace}/management/entries/{entryName}/publish", PublishNamespacedEntryDraftAsync).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        endpoints.MapGet("/api/management/entries/{entryName}/dependencies", GetEntryDependenciesAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        endpoints.MapGet("/api/namespaces/{namespace}/management/entries/{entryName}/dependencies", GetNamespacedEntryDependenciesAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        endpoints.MapGet("/api/resources", ListResourcesAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        var dashboardManagement = endpoints.MapGroup("/api/management/workspaces/{workspaceName}/dashboards")
            .RequireAuthorization(AgentstrationPolicies.Authenticated)
            .AddEndpointFilter(RequireCurrentWorkspaceAsync);
        dashboardManagement.MapGet("", ListDashboardDraftsAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        dashboardManagement.MapGet("/{dashboardName}", GetDashboardDraftAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        dashboardManagement.MapPut("/{dashboardName}", PutDashboardDraftAsync).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        dashboardManagement.MapPost("/{dashboardName}/publish", PublishDashboardDraftAsync).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        dashboardManagement.MapDelete("/{dashboardName}", DeleteDashboardAsync).RequireAuthorization(AgentstrationPolicies.CanDeleteResources);
        workspaces.MapPost("/entries/{entryName}/interactions", SubmitEntryAsync).RequireAuthorization(AgentstrationPolicies.CanRunFlows);
        workspaces.MapPost("/namespaces/{namespace}/entries/{entryName}/interactions", SubmitNamespacedEntryAsync).RequireAuthorization(AgentstrationPolicies.CanRunFlows);
        workspaces.MapGet("/interactions", ListInteractionsAsync).RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        workspaces.MapGet("/interactions/{interactionId:guid}", GetInteractionAsync).RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        workspaces.MapGet("/interactions/{interactionId:guid}/messages", GetMessagesAsync).RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        workspaces.MapPost("/interactions/{interactionId:guid}/messages", AddMessageAsync).RequireAuthorization(AgentstrationPolicies.CanRunFlows);
        workspaces.MapGet("/interactions/{interactionId:guid}/pending-actions", GetPendingActionsAsync).RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        workspaces.MapPost("/interactions/{interactionId:guid}/pending-actions/{pendingActionId:guid}/responses", RespondPendingActionAsync).RequireAuthorization(AgentstrationPolicies.CanRunFlows);
        workspaces.MapGet("/tasks", ListTasksAsync).RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        workspaces.MapGet("/tasks/{taskId:guid}", GetTaskAsync).RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        workspaces.MapPost("/tasks/{taskId:guid}/pause", PauseTaskAsync).RequireAuthorization(AgentstrationPolicies.CanExecuteRuns);
        workspaces.MapPost("/tasks/{taskId:guid}/resume", ResumeTaskAsync).RequireAuthorization(AgentstrationPolicies.CanExecuteRuns);
        workspaces.MapPost("/tasks/{taskId:guid}/cancel", CancelTaskAsync).RequireAuthorization(AgentstrationPolicies.CanExecuteRuns);
        workspaces.MapGet("/tasks/{taskId:guid}/activities", GetActivitiesAsync).RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        workspaces.MapGet("/tasks/{taskId:guid}/results", GetResultsAsync).RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        workspaces.MapGet("/tasks/{taskId:guid}/artifacts", GetArtifactsAsync).RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        workspaces.MapGet("/tasks/{taskId:guid}/artifacts/{artifactId:guid}/content", GetArtifactContentAsync).RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        workspaces.MapGet("/notifications", GetNotificationsAsync).RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        workspaces.MapGet("/notifications/unread-count", GetUnreadCountAsync).RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        workspaces.MapPost("/notifications/{notificationId:guid}/read", MarkReadAsync).RequireAuthorization(AgentstrationPolicies.CanExecuteRuns);
        workspaces.MapPost("/notifications/read-all", MarkAllReadAsync).RequireAuthorization(AgentstrationPolicies.CanExecuteRuns);
        return endpoints;
    }









































































}
