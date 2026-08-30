using System.Text.Json;
using Agentstration.Flow;
using Agentstration.Flow.Application;
using Agentstration.Flow.Contracts;
using Agentstration.Flow.Storage.Abstractions;
using Agentstration.Management.Abstractions;
using Agentstration.Resources;
using Agentstration.Web.Security;

namespace Agentstration.Web;

public static partial class FlowEndpoints
{
    private static Task<IResult> CreateAsync(CreateFlowRequest body, HttpResponse response, FlowService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var stored = await service.CreateAsync(CurrentWorkspace(requestContext), new CreateFlowCommand(body.Name, body.Description, body.Version, body.Enabled, body.Definition, body.Metadata), body.Namespace, token);
        response.Headers.ETag = stored.ETag;
        response.Headers.Location = $"/api/flows/{stored.Value.Id}";
        return Results.Json(ToResponse(stored.Value), statusCode: StatusCodes.Status201Created);
    });

    private static Task<IResult> CreateNamespacedAsync(string @namespace, CreateFlowRequest body, HttpResponse response, FlowService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var parsed = ResourceNamespace.Parse(@namespace);
        if (body.Namespace != parsed) throw new FlowValidationException("route_namespace_mismatch", "The Flow namespace must match the route namespace.");
        var stored = await service.CreateAsync(CurrentWorkspace(requestContext), new CreateFlowCommand(body.Name, body.Description, body.Version, body.Enabled, body.Definition, body.Metadata), parsed, token);
        response.Headers.ETag = stored.ETag;
        response.Headers.Location = $"/api/namespaces/{parsed.Value}/flows/{stored.Value.Id.Value}";
        return Results.Json(ToResponse(stored.Value), statusCode: StatusCodes.Status201Created);
    });

    private static Task<IResult> GetNamespacedAsync(string @namespace, string id, HttpResponse response, FlowService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var flowId = new FlowId(id, ResourceNamespace.Parse(@namespace));
        var stored = await service.GetAsync(CurrentWorkspace(requestContext), flowId, token) ?? throw new FlowNotFoundException(flowId);
        response.Headers.ETag = stored.ETag;
        return Results.Ok(ToResponse(stored.Value));
    });

    private static Task<IResult> UpdateNamespacedAsync(string @namespace, string id, UpdateFlowRequest body, HttpRequest request, HttpResponse response, FlowService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var flowId = new FlowId(id, ResourceNamespace.Parse(@namespace));
        var workspaceId = CurrentWorkspace(requestContext);
        var current = await service.GetAsync(workspaceId, flowId, token) ?? throw new FlowNotFoundException(flowId);
        var stored = await service.UpdateAsync(workspaceId, flowId, new UpdateFlowCommand(body.Description, body.Version, body.Enabled, body.Definition, body.Metadata), request.Headers.IfMatch.FirstOrDefault() ?? current.ETag, token);
        response.Headers.ETag = stored.ETag;
        return Results.Ok(ToResponse(stored.Value));
    });

    private static Task<IResult> DeleteNamespacedAsync(string @namespace, string id, HttpRequest request, FlowService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        await service.DeleteAsync(CurrentWorkspace(requestContext), new FlowId(id, ResourceNamespace.Parse(@namespace)), request.Headers.IfMatch.FirstOrDefault(), token);
        return Results.NoContent();
    });

    private static Task<IResult> GetNamespacedVersionAsync(string @namespace, string id, string version, HttpResponse response, FlowService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var flowId = new FlowId(id, ResourceNamespace.Parse(@namespace));
        var stored = await service.GetVersionAsync(CurrentWorkspace(requestContext), flowId, version, token) ?? throw new FlowNotFoundException(flowId);
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
        var workspaceId = CurrentWorkspace(requestContext);
        var page = @namespace is null
            ? await service.ListAllAsync(workspaceId, actualSkip, actualTop, token)
            : await service.ListAsync(workspaceId, @namespace.Value, actualSkip, actualTop, token);
        var prefix = @namespace is null
            ? "/api/flows?allNamespaces=true&"
            : @namespace.Value.IsDefault
                ? "/api/flows?"
                : $"/api/namespaces/{@namespace.Value.Value}/flows?";
        var next = page.HasMore ? $"{prefix}skip={actualSkip + actualTop}&top={actualTop}" : null;
        return Results.Ok(new FlowPageResponse(page.Items.Select(item => ToSummary(item.Value)).ToArray(), next));
    });

    private static Task<IResult> GetAsync(string id, HttpResponse response, FlowService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var stored = await RequiredAsync(CurrentWorkspace(requestContext), id, service, token);
        response.Headers.ETag = stored.ETag;
        return Results.Ok(ToResponse(stored.Value));
    });

    private static Task<IResult> UpdateAsync(string id, UpdateFlowRequest body, HttpRequest request, HttpResponse response, FlowService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var workspaceId = CurrentWorkspace(requestContext);
        var current = await RequiredAsync(workspaceId, id, service, token);
        var etag = request.Headers.IfMatch.FirstOrDefault() ?? current.ETag;
        var stored = await service.UpdateAsync(workspaceId, new FlowId(id), new UpdateFlowCommand(body.Description, body.Version, body.Enabled, body.Definition, body.Metadata), etag, token);
        response.Headers.ETag = stored.ETag;
        return Results.Ok(ToResponse(stored.Value));
    });

    private static Task<IResult> DeleteAsync(string id, HttpRequest request, FlowService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        await service.DeleteAsync(CurrentWorkspace(requestContext), new FlowId(id), request.Headers.IfMatch.FirstOrDefault(), token);
        return Results.NoContent();
    });

    private static Task<IResult> CreateVersionAsync(string id, CreateFlowVersionRequest body, HttpResponse response, FlowService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var stored = await service.PublishVersionAsync(CurrentWorkspace(requestContext), new FlowId(id), body.Version, body.Activate, token);
        response.Headers.ETag = stored.ETag;
        response.Headers.Location = $"/api/flows/{id}/versions/{body.Version}";
        return Results.Json(ToVersion(stored.Value), statusCode: StatusCodes.Status201Created);
    });

    private static Task<IResult> GetVersionAsync(string id, string version, HttpResponse response, FlowService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var stored = await service.GetVersionAsync(CurrentWorkspace(requestContext), new FlowId(id), version, token) ?? throw new FlowNotFoundException(new FlowId($"{id}:{version}"));
        response.Headers.ETag = stored.ETag;
        return Results.Ok(ToVersion(stored.Value));
    });

    private static Task<IResult> ListVersionsAsync(string id, FlowService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var workspaceId = CurrentWorkspace(requestContext);
        _ = await RequiredAsync(workspaceId, id, service, token);
        return Results.Ok((await service.ListVersionsAsync(workspaceId, new FlowId(id), token)).Select(item => ToVersion(item.Value)));
    });

    private static Task<IResult> ListNamespacedVersionsAsync(string @namespace, string id, FlowService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var flowId = new FlowId(id, ResourceNamespace.Parse(@namespace));
        var workspaceId = CurrentWorkspace(requestContext);
        _ = await service.GetAsync(workspaceId, flowId, token) ?? throw new FlowNotFoundException(flowId);
        return Results.Ok((await service.ListVersionsAsync(workspaceId, flowId, token)).Select(item => ToVersion(item.Value)));
    });
}
