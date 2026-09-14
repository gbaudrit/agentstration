using Agentstration.Api.Contracts;
using Agentstration.Tools.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Tools;
using Agentstration.Web.Security;

namespace Agentstration.Web.Api.Models;

internal static class ToolExecutionHookEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/", ListAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        group.MapGet("/{hookName}", GetAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        group.MapPost("/", CreateAsync).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        group.MapPut("/{hookName}", PutAsync).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        group.MapDelete("/{hookName}", DeleteAsync).RequireAuthorization(AgentstrationPolicies.CanDeleteResources);
    }

    private static Task<IResult> ListAsync(
        ToolExecutionHookManagementService service,
        CancellationToken cancellationToken) =>
        ToolsApiHttp.ExecuteAsync(async () => Results.Ok(new ValueResponse<ToolExecutionHookResource>(
            (await service.ListAsync(cancellationToken)).Select(value => value.Value).ToArray())));

    private static Task<IResult> GetAsync(
        string hookName,
        string? resourceNamespace,
        HttpResponse response,
        ToolExecutionHookManagementService service,
        CancellationToken cancellationToken) =>
        ToolsApiHttp.ExecuteAsync(async () =>
        {
            var @namespace = ToolsApiHttp.Namespace(resourceNamespace);
            var stored = await service.GetAsync(@namespace, hookName, cancellationToken)
                ?? throw new ResourceNotFoundException(new(ToolResourceKinds.ToolExecutionHook, hookName, @namespace));
            return ToolsApiHttp.ResourceResult(stored, response, StatusCodes.Status200OK);
        });

    private static Task<IResult> CreateAsync(
        CreateToolExecutionHookRequest body,
        HttpResponse response,
        ToolExecutionHookManagementService service,
        CancellationToken cancellationToken) =>
        ToolsApiHttp.ExecuteAsync(async () =>
        {
            var stored = await service.CreateAsync(new ToolExecutionHookResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = ToolResourceKinds.ToolExecutionHook,
                Metadata = new ResourceMetadata
                {
                    Name = body.Name,
                    Namespace = ToolsApiHttp.Namespace(body.Namespace)
                },
                Definition = body.Properties
            }, cancellationToken);
            response.Headers.Location = $"/api/toolexecutionhooks/{Uri.EscapeDataString(stored.Value.Name)}?resourceNamespace={Uri.EscapeDataString(stored.Value.Namespace.Value)}";
            return ToolsApiHttp.ResourceResult(stored, response, StatusCodes.Status201Created);
        });

    private static Task<IResult> PutAsync(
        string hookName,
        string? resourceNamespace,
        PutToolExecutionHookRequest body,
        HttpRequest request,
        HttpResponse response,
        ToolExecutionHookManagementService service,
        CancellationToken cancellationToken) =>
        ToolsApiHttp.ExecuteAsync(async () => ToolsApiHttp.ResourceResult(
            await service.PutAsync(
                ToolsApiHttp.Namespace(resourceNamespace),
                hookName,
                body.Properties,
                ToolsApiHttp.IfMatch(request),
                cancellationToken),
            response,
            StatusCodes.Status200OK));

    private static Task<IResult> DeleteAsync(
        string hookName,
        string? resourceNamespace,
        HttpRequest request,
        ToolExecutionHookManagementService service,
        CancellationToken cancellationToken) =>
        ToolsApiHttp.ExecuteAsync(async () =>
        {
            await service.DeleteAsync(
                ToolsApiHttp.Namespace(resourceNamespace),
                hookName,
                ToolsApiHttp.IfMatch(request),
                cancellationToken);
            return Results.NoContent();
        });
}
