using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Flows.Contracts;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Web.Security;

namespace Agentstration.Web;

public static partial class FlowEndpoints
{
    private static Task<IResult> CreateDraftAsync(CreateFlowDraftRequest body, HttpResponse response, FlowDraftService service, IResourceScopeOperations scopes, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var workspaceId = CurrentWorkspace(requestContext);
        var ownerScope = RequestedOwnerScope(body.ScopeRef, requestContext);
        var stored = await WriteFlowAsync(scopes, ownerScope, operationToken => service.CreateExactAsync(workspaceId, ownerScope, new CreateFlowDraftCommand(body.Name, body.DisplayName, body.Description, body.Tags, body.Template), operationToken), token);
        response.Headers.ETag = stored.ETag;
        response.Headers.Location = $"/api/flows/{stored.Value.FlowId.Value}/draft";
        return Results.Json(new FlowDraftResponse(stored.Value, stored.ETag), statusCode: StatusCodes.Status201Created);
    });

    private static Task<IResult> GetDraftAsync(string id, string? scopeRef, HttpResponse response, FlowDraftService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var stored = await service.GetExactAsync(CurrentWorkspace(requestContext), RequestedOwnerScope(scopeRef, requestContext), new FlowId(id), token) ?? throw new FlowNotFoundException(new FlowId(id));
        response.Headers.ETag = stored.ETag;
        return Results.Ok(new FlowDraftResponse(stored.Value, stored.ETag));
    });

    private static Task<IResult> SaveDraftAsync(string id, string? scopeRef, UpdateFlowDraftRequest body, HttpRequest request, HttpResponse response, FlowDraftService service, IResourceScopeOperations scopes, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var etag = RequiredIfMatch(request);
        var workspaceId = CurrentWorkspace(requestContext);
        var ownerScope = RequestedOwnerScope(scopeRef, requestContext);
        var stored = await WriteFlowAsync(scopes, ownerScope, operationToken => service.SaveExactAsync(workspaceId, ownerScope, new FlowId(id), new UpdateFlowDraftCommand(body.DisplayName, body.Description, body.Tags, body.Definition, body.UpdatedBy), etag, operationToken), token);
        response.Headers.ETag = stored.ETag;
        return Results.Ok(new FlowDraftResponse(stored.Value, stored.ETag));
    });

    private static Task<IResult> ValidateDraftAsync(string id, string? scopeRef, FlowDraftService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var validation = await service.ValidateExactAsync(CurrentWorkspace(requestContext), RequestedOwnerScope(scopeRef, requestContext), new FlowId(id), token);
        return Results.Ok(new FlowValidationResponse(validation.IsValid, validation.Issues));
    });

    private static Task<IResult> GetDraftSourceAsync(string id, string? scopeRef, string? format, FlowDraftService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var actualFormat = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase) ? "json" : "yaml";
        var workspaceId = CurrentWorkspace(requestContext);
        var ownerScope = RequestedOwnerScope(scopeRef, requestContext);
        var draft = await service.GetExactAsync(workspaceId, ownerScope, new FlowId(id), token) ?? throw new FlowNotFoundException(new FlowId(id));
        return Results.Ok(new FlowSourceResponse(await service.GetSourceExactAsync(workspaceId, ownerScope, new FlowId(id), actualFormat, token), actualFormat, draft.Value.Revision));
    });

    private static Task<IResult> ReplaceDraftSourceAsync(string id, string? scopeRef, ReplaceFlowSourceRequest body, HttpRequest request, HttpResponse response, FlowDraftService service, IResourceScopeOperations scopes, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var workspaceId = CurrentWorkspace(requestContext);
        var ownerScope = RequestedOwnerScope(scopeRef, requestContext);
        var current = await service.GetExactAsync(workspaceId, ownerScope, new FlowId(id), token) ?? throw new FlowNotFoundException(new FlowId(id));
        var definition = service.ParseSource(body.Source, body.Format);
        var stored = await WriteFlowAsync(scopes, ownerScope, operationToken => service.SaveExactAsync(workspaceId, ownerScope, new FlowId(id), new UpdateFlowDraftCommand(current.Value.DisplayName, current.Value.Description, current.Value.Tags, definition, body.UpdatedBy), RequiredIfMatch(request), operationToken), token);
        response.Headers.ETag = stored.ETag;
        return Results.Ok(new FlowDraftResponse(stored.Value, stored.ETag));
    });

    private static Task<IResult> PublishDraftAsync(string id, string? scopeRef, PublishFlowDraftRequest body, HttpResponse response, FlowDraftService service, IResourceScopeOperations scopes, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var workspaceId = CurrentWorkspace(requestContext);
        var ownerScope = RequestedOwnerScope(scopeRef, requestContext);
        var stored = await WriteFlowAsync(scopes, ownerScope, operationToken => service.PublishExactAsync(workspaceId, ownerScope, new FlowId(id), body.Version, body.ReleaseNotes, body.Activate, operationToken), token);
        response.Headers.ETag = stored.ETag;
        response.Headers.Location = $"/api/flows/{id}/versions/{body.Version}";
        return Results.Json(ToVersion(stored.Value), statusCode: StatusCodes.Status201Created);
    });

    private static Task<IResult> CreateDraftRunAsync(string id, string? scopeRef, CreateFlowRunRequest body, HttpContext context, HttpResponse response, FlowDraftService drafts, FlowRunService runs, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var workspaceId = CurrentWorkspace(requestContext);
        var ownerScope = RequestedOwnerScope(scopeRef, requestContext);
        var draft = await drafts.GetExactAsync(workspaceId, ownerScope, new FlowId(id), token) ?? throw new FlowNotFoundException(new FlowId(id));
        var validation = await drafts.ValidateExactAsync(workspaceId, ownerScope, new FlowId(id), token);
        if (!validation.IsValid) throw new FlowValidationException("flow_validation_failed", "The Flow Draft contains validation errors and cannot run.");
        var scope = CurrentScope(requestContext);
        var startedBy = context.Features.Get<ResolvedPrincipalFeature>()?.DisplayName ?? scope.PrincipalId.ToString("D");
        var stored = await runs.CreateDraftAsync(draft.Value, body.Trigger, startedBy, body.CorrelationId, body.Input, scope, token);
        response.Headers.Location = $"/api/flowRuns/{stored.Value.Id}";
        return Results.Accepted($"/api/flowRuns/{stored.Value.Id}", stored.Value);
    });

    private static Task<IResult> CreateDraftFromVersionAsync(string id, string version, string? scopeRef, HttpResponse response, FlowDraftService service, IResourceScopeOperations scopes, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var workspaceId = CurrentWorkspace(requestContext);
        var ownerScope = RequestedOwnerScope(scopeRef, requestContext);
        var stored = await WriteFlowAsync(scopes, ownerScope, operationToken => service.CreateFromVersionExactAsync(workspaceId, ownerScope, new FlowId(id), version, "local-user", operationToken), token);
        response.Headers.ETag = stored.ETag;
        return Results.Ok(new FlowDraftResponse(stored.Value, stored.ETag));
    });

    private static Task<IResult> GetNamespacedDraftAsync(string @namespace, string id, string? scopeRef, HttpResponse response, FlowDraftService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var flowId = NamespacedFlowId(@namespace, id);
        var stored = await service.GetExactAsync(CurrentWorkspace(requestContext), RequestedOwnerScope(scopeRef, requestContext), flowId, token) ?? throw new FlowNotFoundException(flowId);
        response.Headers.ETag = stored.ETag;
        return Results.Ok(new FlowDraftResponse(stored.Value, stored.ETag));
    });

    private static Task<IResult> SaveNamespacedDraftAsync(string @namespace, string id, string? scopeRef, UpdateFlowDraftRequest body, HttpRequest request, HttpResponse response, FlowDraftService service, IResourceScopeOperations scopes, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var workspaceId = CurrentWorkspace(requestContext);
        var ownerScope = RequestedOwnerScope(scopeRef, requestContext);
        var stored = await WriteFlowAsync(scopes, ownerScope, operationToken => service.SaveExactAsync(workspaceId, ownerScope, NamespacedFlowId(@namespace, id), new UpdateFlowDraftCommand(body.DisplayName, body.Description, body.Tags, body.Definition, body.UpdatedBy), RequiredIfMatch(request), operationToken), token);
        response.Headers.ETag = stored.ETag;
        return Results.Ok(new FlowDraftResponse(stored.Value, stored.ETag));
    });

    private static Task<IResult> ValidateNamespacedDraftAsync(string @namespace, string id, string? scopeRef, FlowDraftService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var validation = await service.ValidateExactAsync(CurrentWorkspace(requestContext), RequestedOwnerScope(scopeRef, requestContext), NamespacedFlowId(@namespace, id), token);
        return Results.Ok(new FlowValidationResponse(validation.IsValid, validation.Issues));
    });

    private static Task<IResult> GetNamespacedDraftSourceAsync(string @namespace, string id, string? scopeRef, string? format, FlowDraftService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var actualFormat = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase) ? "json" : "yaml";
        var workspaceId = CurrentWorkspace(requestContext);
        var flowId = NamespacedFlowId(@namespace, id);
        var ownerScope = RequestedOwnerScope(scopeRef, requestContext);
        var draft = await service.GetExactAsync(workspaceId, ownerScope, flowId, token) ?? throw new FlowNotFoundException(flowId);
        return Results.Ok(new FlowSourceResponse(await service.GetSourceExactAsync(workspaceId, ownerScope, flowId, actualFormat, token), actualFormat, draft.Value.Revision));
    });

    private static Task<IResult> ReplaceNamespacedDraftSourceAsync(string @namespace, string id, string? scopeRef, ReplaceFlowSourceRequest body, HttpRequest request, HttpResponse response, FlowDraftService service, IResourceScopeOperations scopes, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var workspaceId = CurrentWorkspace(requestContext);
        var flowId = NamespacedFlowId(@namespace, id);
        var ownerScope = RequestedOwnerScope(scopeRef, requestContext);
        var current = await service.GetExactAsync(workspaceId, ownerScope, flowId, token) ?? throw new FlowNotFoundException(flowId);
        var definition = service.ParseSource(body.Source, body.Format);
        var stored = await WriteFlowAsync(scopes, ownerScope, operationToken => service.SaveExactAsync(workspaceId, ownerScope, flowId, new UpdateFlowDraftCommand(current.Value.DisplayName, current.Value.Description, current.Value.Tags, definition, body.UpdatedBy), RequiredIfMatch(request), operationToken), token);
        response.Headers.ETag = stored.ETag;
        return Results.Ok(new FlowDraftResponse(stored.Value, stored.ETag));
    });

    private static Task<IResult> PublishNamespacedDraftAsync(string @namespace, string id, string? scopeRef, PublishFlowDraftRequest body, HttpResponse response, FlowDraftService service, IResourceScopeOperations scopes, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var flowId = NamespacedFlowId(@namespace, id);
        var workspaceId = CurrentWorkspace(requestContext);
        var ownerScope = RequestedOwnerScope(scopeRef, requestContext);
        var stored = await WriteFlowAsync(scopes, ownerScope, operationToken => service.PublishExactAsync(workspaceId, ownerScope, flowId, body.Version, body.ReleaseNotes, body.Activate, operationToken), token);
        response.Headers.ETag = stored.ETag;
        response.Headers.Location = $"/api/namespaces/{flowId.Namespace.Value}/flows/{id}/versions/{body.Version}";
        return Results.Json(ToVersion(stored.Value), statusCode: StatusCodes.Status201Created);
    });

    private static Task<IResult> CreateNamespacedDraftRunAsync(string @namespace, string id, string? scopeRef, CreateFlowRunRequest body, HttpContext context, HttpResponse response, FlowDraftService drafts, FlowRunService runs, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var workspaceId = CurrentWorkspace(requestContext);
        var flowId = NamespacedFlowId(@namespace, id);
        var ownerScope = RequestedOwnerScope(scopeRef, requestContext);
        var draft = await drafts.GetExactAsync(workspaceId, ownerScope, flowId, token) ?? throw new FlowNotFoundException(flowId);
        var validation = await drafts.ValidateExactAsync(workspaceId, ownerScope, flowId, token);
        if (!validation.IsValid) throw new FlowValidationException("flow_validation_failed", "The Flow Draft contains validation errors and cannot run.");
        var scope = CurrentScope(requestContext);
        var startedBy = context.Features.Get<ResolvedPrincipalFeature>()?.DisplayName ?? scope.PrincipalId.ToString("D");
        var stored = await runs.CreateDraftAsync(draft.Value, body.Trigger, startedBy, body.CorrelationId, body.Input, scope, token);
        response.Headers.Location = $"/api/flowRuns/{stored.Value.Id}";
        return Results.Accepted($"/api/flowRuns/{stored.Value.Id}", stored.Value);
    });

    private static Task<IResult> CreateNamespacedDraftFromVersionAsync(string @namespace, string id, string version, string? scopeRef, HttpResponse response, FlowDraftService service, IResourceScopeOperations scopes, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var workspaceId = CurrentWorkspace(requestContext);
        var ownerScope = RequestedOwnerScope(scopeRef, requestContext);
        var stored = await WriteFlowAsync(scopes, ownerScope, operationToken => service.CreateFromVersionExactAsync(workspaceId, ownerScope, NamespacedFlowId(@namespace, id), version, "local-user", operationToken), token);
        response.Headers.ETag = stored.ETag;
        return Results.Ok(new FlowDraftResponse(stored.Value, stored.ETag));
    });

    private static FlowId NamespacedFlowId(string @namespace, string id) => new(id, ResourceNamespace.Parse(@namespace));
}
