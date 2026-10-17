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
    private static Task<IResult> CreateAsync(CreateFlowRequest body, HttpResponse response, FlowService service, IResourceScopeOperations scopes, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var ownerScope = RequestedOwnerScope(body.ScopeRef, requestContext);
        var stored = await WriteFlowAsync(scopes, ownerScope, operationToken => service.CreateExactAsync(ownerScope, new CreateFlowCommand(body.Name, body.Description, body.Version, body.Enabled, body.Definition, body.Metadata, DisplayName: body.DisplayName), body.Namespace, operationToken), token);
        response.Headers.ETag = stored.ETag;
        response.Headers.Location = $"/api/flows/{stored.Value.Id}";
        return Results.Json(ToResponse(stored.Value), statusCode: StatusCodes.Status201Created);
    });

    private static Task<IResult> CreateNamespacedAsync(string @namespace, CreateFlowRequest body, HttpResponse response, FlowService service, IResourceScopeOperations scopes, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var parsed = ResourceNamespace.Parse(@namespace);
        if (body.Namespace != parsed) throw new FlowValidationException("route_namespace_mismatch", "The Flow namespace must match the route namespace.");
        var ownerScope = RequestedOwnerScope(body.ScopeRef, requestContext);
        var stored = await WriteFlowAsync(scopes, ownerScope, operationToken => service.CreateExactAsync(ownerScope, new CreateFlowCommand(body.Name, body.Description, body.Version, body.Enabled, body.Definition, body.Metadata, DisplayName: body.DisplayName), parsed, operationToken), token);
        response.Headers.ETag = stored.ETag;
        response.Headers.Location = $"/api/namespaces/{parsed.Value}/flows/{stored.Value.Id.Value}";
        return Results.Json(ToResponse(stored.Value), statusCode: StatusCodes.Status201Created);
    });

    private static Task<IResult> GetNamespacedAsync(string @namespace, string id, string? scopeRef, HttpResponse response, FlowService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var flowId = new FlowId(id, ResourceNamespace.Parse(@namespace));
        var stored = await RequiredVisibleAsync(flowId, scopeRef, service, requestContext, token);
        response.Headers.ETag = stored.ETag;
        return Results.Ok(ToResponse(stored.Value));
    });

    private static Task<IResult> UpdateNamespacedAsync(string @namespace, string id, string? scopeRef, UpdateFlowRequest body, HttpRequest request, HttpResponse response, FlowService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        EnsureWorkspaceMutationScope(scopeRef, requestContext);
        var flowId = new FlowId(id, ResourceNamespace.Parse(@namespace));
        var workspaceId = CurrentWorkspace(requestContext);
        var current = await service.GetAsync(workspaceId, flowId, token) ?? throw new FlowNotFoundException(flowId);
        var stored = await service.UpdateAsync(workspaceId, flowId, new UpdateFlowCommand(body.Description, body.Version, body.Enabled, body.Definition, body.Metadata, DisplayName: body.DisplayName), request.Headers.IfMatch.FirstOrDefault() ?? current.ETag, token);
        response.Headers.ETag = stored.ETag;
        return Results.Ok(ToResponse(stored.Value));
    });

    private static Task<IResult> DeleteNamespacedAsync(string @namespace, string id, string? scopeRef, bool? deleteSystemManaged, HttpRequest request, FlowService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        EnsureWorkspaceMutationScope(scopeRef, requestContext);
        await service.DeleteAsync(CurrentWorkspace(requestContext), new FlowId(id, ResourceNamespace.Parse(@namespace)), request.Headers.IfMatch.FirstOrDefault(), deleteSystemManaged is true, token);
        return Results.NoContent();
    });

    private static Task<IResult> GetNamespacedVersionAsync(string @namespace, string id, string version, string? scopeRef, HttpResponse response, FlowService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var flowId = new FlowId(id, ResourceNamespace.Parse(@namespace));
        var flow = await RequiredVisibleAsync(flowId, scopeRef, service, requestContext, token);
        var stored = await service.GetVersionExactAsync(flow.Value.ScopeRef, flowId, version, token) ?? throw new FlowNotFoundException(flowId);
        response.Headers.ETag = stored.ETag;
        return Results.Ok(ToVersion(stored.Value));
    });

    private static Task<IResult> ListAsync(bool? allNamespaces, int? skip, int? top, FlowService service, ICurrentRequestContext requestContext, CancellationToken token) =>
        ListCoreAsync(allNamespaces is true ? null : ResourceNamespace.Default, skip, top, service, requestContext, token);

    private static Task<IResult> ListNamespacedAsync(string @namespace, int? skip, int? top, FlowService service, ICurrentRequestContext requestContext, CancellationToken token) =>
        ListCoreAsync(ResourceNamespace.Parse(@namespace), skip, top, service, requestContext, token);

    private static Task<IResult> ListCoreAsync(ResourceNamespace? @namespace, int? skip, int? top, FlowService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var actualSkip = Math.Max(0, skip ?? 0);
        var actualTop = Math.Clamp(top ?? 50, 1, 200);
        var page = @namespace is null
            ? await service.ListVisibleAsync(CurrentConsumerScope(requestContext), null, actualSkip, actualTop, token)
            : await service.ListVisibleAsync(CurrentConsumerScope(requestContext), @namespace.Value, actualSkip, actualTop, token);
        var prefix = @namespace is null
            ? "/api/flows?allNamespaces=true&"
            : @namespace.Value.IsDefault
                ? "/api/flows?"
                : $"/api/namespaces/{@namespace.Value.Value}/flows?";
        var next = page.HasMore ? $"{prefix}skip={actualSkip + actualTop}&top={actualTop}" : null;
        return Results.Ok(new FlowPageResponse(page.Items.Select(item => ToSummary(item.Value)).ToArray(), next));
    });

    private static Task<IResult> GetAsync(string id, string? scopeRef, HttpResponse response, FlowService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var stored = await RequiredVisibleAsync(new FlowId(id), scopeRef, service, requestContext, token);
        response.Headers.ETag = stored.ETag;
        return Results.Ok(ToResponse(stored.Value));
    });

    private static Task<IResult> UpdateAsync(string id, string? scopeRef, UpdateFlowRequest body, HttpRequest request, HttpResponse response, FlowService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        EnsureWorkspaceMutationScope(scopeRef, requestContext);
        var workspaceId = CurrentWorkspace(requestContext);
        var current = await RequiredAsync(workspaceId, id, service, token);
        var etag = request.Headers.IfMatch.FirstOrDefault() ?? current.ETag;
        var stored = await service.UpdateAsync(workspaceId, new FlowId(id), new UpdateFlowCommand(body.Description, body.Version, body.Enabled, body.Definition, body.Metadata, DisplayName: body.DisplayName), etag, token);
        response.Headers.ETag = stored.ETag;
        return Results.Ok(ToResponse(stored.Value));
    });

    private static Task<IResult> DeleteAsync(string id, string? scopeRef, bool? deleteSystemManaged, HttpRequest request, FlowService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        EnsureWorkspaceMutationScope(scopeRef, requestContext);
        await service.DeleteAsync(CurrentWorkspace(requestContext), new FlowId(id), request.Headers.IfMatch.FirstOrDefault(), deleteSystemManaged is true, token);
        return Results.NoContent();
    });

    private static Task<IResult> CreateVersionAsync(string id, string? scopeRef, CreateFlowVersionRequest body, HttpResponse response, FlowService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        EnsureWorkspaceMutationScope(scopeRef, requestContext);
        var stored = await service.PublishVersionAsync(CurrentWorkspace(requestContext), new FlowId(id), body.Version, body.Activate, token);
        response.Headers.ETag = stored.ETag;
        response.Headers.Location = $"/api/flows/{id}/versions/{body.Version}";
        return Results.Json(ToVersion(stored.Value), statusCode: StatusCodes.Status201Created);
    });

    private static Task<IResult> GetVersionAsync(string id, string version, string? scopeRef, HttpResponse response, FlowService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var flowId = new FlowId(id);
        var flow = await RequiredVisibleAsync(flowId, scopeRef, service, requestContext, token);
        var stored = await service.GetVersionExactAsync(flow.Value.ScopeRef, flowId, version, token) ?? throw new FlowNotFoundException(new FlowId($"{id}:{version}"));
        response.Headers.ETag = stored.ETag;
        return Results.Ok(ToVersion(stored.Value));
    });

    private static Task<IResult> ListVersionsAsync(string id, string? scopeRef, FlowService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var flowId = new FlowId(id);
        var flow = await RequiredVisibleAsync(flowId, scopeRef, service, requestContext, token);
        return Results.Ok((await service.ListVersionsExactAsync(flow.Value.ScopeRef, flowId, token)).Select(item => ToVersion(item.Value)));
    });

    private static Task<IResult> ListNamespacedVersionsAsync(string @namespace, string id, string? scopeRef, FlowService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var flowId = new FlowId(id, ResourceNamespace.Parse(@namespace));
        var flow = await RequiredVisibleAsync(flowId, scopeRef, service, requestContext, token);
        return Results.Ok((await service.ListVersionsExactAsync(flow.Value.ScopeRef, flowId, token)).Select(item => ToVersion(item.Value)));
    });
}
