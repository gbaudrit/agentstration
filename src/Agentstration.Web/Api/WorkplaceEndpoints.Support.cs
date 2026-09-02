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
    private static WorkplaceDashboardResponse ToResponse(WorkplaceDashboard value) => new(value.Id.Value, value.WorkspaceId.Value, value.Name, value.Type, value.ApiVersion, value.DisplayName, value.Description, value.IsDefault, value.Entries.Select(reference => new DashboardEntryReferenceResponse(reference.EntryResourceId.Value, reference.Role, reference.Order) { Namespace = reference.EntryResourceId.Namespace }).ToArray(), value.Version, value.PublishedAt, value.Icon);

    private static EntryResponse ToResponse(EntryResource value) => new(value.WorkspaceId.Value, value.Id.Value, value.Name, value.Type, value.ApiVersion, value.DisplayName, value.Description, value.Presentation, value.ResolvedTarget, value.Behavior, value.Version, value.PublishedAt) { Namespace = value.Id.Namespace };

    private static InteractionResponse ToResponse(WorkplaceInteraction value) => new(value.Id.Value, value.WorkspaceId.Value, value.EntryId.Value, value.Status, value.StartedAt, value.LastActivityAt, value.InputValues, value.Attachments, value.Messages, value.PendingActionId?.Value, value.TaskId?.Value, value.ImmediateResult, value.Version, value.LastFlowRunId, value.LastTriggerMessageId) { EntryNamespace = value.EntryId.Namespace };

    private static WorkTaskResponse ToResponse(WorkTask value) => new(value.Id.Value, value.WorkspaceId.Value, value.EntryId?.Value, value.InteractionId?.Value, value.Title, value.Description, value.Status, value.CreatedAt, value.UpdatedAt, value.FlowRunId, value.Conversation, value.Activities, value.Artifacts, value.Result, value.Error, WorkplaceService.CurrentAction(value), value.Version);

    private static WorkspaceId WorkspaceId(string value) => new(Guid.Parse(value));

    private static DashboardId DashboardResourceId(string name) => new(name);

    private static EntryId EntryResourceId(string name) => new(name);

    private static EntryId NamespacedEntryId(string @namespace, string name) => new(name, ResourceNamespace.Parse(@namespace));

    private static string WorkspaceName(WorkspaceId id) => id.ToString();

    private static async ValueTask<object?> RequireCurrentWorkspaceAsync(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var routeValue = invocation.HttpContext.Request.RouteValues["workspaceName"]?.ToString();
        var requestContext = invocation.HttpContext.RequestServices.GetRequiredService<ICurrentRequestContext>();
        var store = invocation.HttpContext.RequestServices.GetRequiredService<IIdentityStore>();
        var workspace = await store.GetWorkspaceAsync(
            requestContext.Current.TenantId,
            requestContext.Current.WorkspaceId,
            invocation.HttpContext.RequestAborted);
        if (workspace is null ||
            (!string.Equals(routeValue, workspace.Name, StringComparison.OrdinalIgnoreCase) &&
             (!Guid.TryParse(routeValue, out var workspaceId) || workspaceId != workspace.Id)))
            return Results.NotFound();

        // Keep application handlers identifier-based while allowing stable, readable Workspace routes.
        var workspaceIdValue = workspace.Id.ToString("D");
        invocation.HttpContext.Request.RouteValues["workspaceName"] = workspaceIdValue;
        for (var index = 0; index < invocation.Arguments.Count; index++)
        {
            if (invocation.Arguments[index] is string argument && string.Equals(argument, routeValue, StringComparison.Ordinal))
            {
                invocation.Arguments[index] = workspaceIdValue;
                break;
            }
        }
        return await next(invocation);
    }

    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action) { try { return await action(); } catch (KeyNotFoundException exception) { return Results.Problem(statusCode: 404, title: "workplace_resource_not_found", detail: exception.Message); } catch (WorkValidationException exception) when (exception.Code is "entry_in_use" or "entry_interactions_active") { return Results.Problem(statusCode: 409, title: exception.Code, detail: exception.Message); } catch (WorkValidationException exception) { return Results.Problem(statusCode: 400, title: exception.Code, detail: exception.Message); } catch (InputRequestAlreadyResolvedException exception) { return Results.Problem(statusCode: 409, title: "input_request_already_resolved", detail: exception.Message); } catch (WorkTransitionException exception) { return Results.Problem(statusCode: 409, title: exception.Code, detail: exception.Message); } catch (WorkplaceConcurrencyException exception) { return Results.Problem(statusCode: 412, title: "precondition_failed", detail: exception.Message); } }
}
