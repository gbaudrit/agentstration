using Agentstration.Api.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Tools;
using Agentstration.Tools.Contracts;
using Agentstration.Web.Security;

namespace Agentstration.Web.Api.Models;

internal static class ToolCategoryEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/", ListAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        group.MapGet("/{categoryName}", GetAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        group.MapGet("/{categoryName}/members", ListMembersAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        group.MapPost("/", CreateAsync).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        group.MapPut("/{categoryName}", PutAsync).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        group.MapDelete("/{categoryName}", DeleteAsync).RequireAuthorization(AgentstrationPolicies.CanDeleteResources);
    }

    private static Task<IResult> ListAsync(
        string? resourceNamespace,
        ToolCategoryService service,
        CancellationToken cancellationToken) =>
        ToolsApiHttp.ExecuteAsync(async () =>
        {
            var values = (await service.ListAsync(cancellationToken)).Select(value => value.Value);
            if (!string.IsNullOrWhiteSpace(resourceNamespace))
            {
                var @namespace = ResourceNamespace.Parse(resourceNamespace);
                values = values.Where(value => value.Namespace == @namespace);
            }
            return Results.Ok(new ValueResponse<ToolCategoryResource>(values.ToArray()));
        });

    private static Task<IResult> GetAsync(
        string categoryName,
        string? resourceNamespace,
        HttpResponse response,
        ToolCategoryService service,
        CancellationToken cancellationToken) =>
        ToolsApiHttp.ExecuteAsync(async () =>
        {
            var @namespace = ResourceNamespace.Parse(resourceNamespace);
            var stored = await service.GetAsync(@namespace, categoryName, cancellationToken)
                ?? throw new ResourceNotFoundException(new(ToolResourceKinds.ToolCategory, categoryName, @namespace));
            return ToolsApiHttp.ResourceResult(stored, response, StatusCodes.Status200OK);
        });

    private static Task<IResult> ListMembersAsync(
        string categoryName,
        string? resourceNamespace,
        ToolCategoryService service,
        CancellationToken cancellationToken) =>
        ToolsApiHttp.ExecuteAsync(async () => Results.Ok(new ValueResponse<ToolCategoryMember>(
            await service.GetMembersAsync(ResourceNamespace.Parse(resourceNamespace), categoryName, cancellationToken))));

    private static Task<IResult> CreateAsync(
        CreateToolCategoryRequest body,
        HttpResponse response,
        ToolCategoryService service,
        CancellationToken cancellationToken) =>
        ToolsApiHttp.ExecuteAsync(async () =>
        {
            var @namespace = ResourceNamespace.Parse(body.Namespace);
            var resource = new ToolCategoryResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = ToolResourceKinds.ToolCategory,
                Metadata = new ResourceMetadata { Name = body.Name, Namespace = @namespace },
                ScopeRef = body.ScopeRef,
                Definition = body.Properties
            };
            var stored = await service.CreateAsync(resource, cancellationToken);
            response.Headers.Location = @namespace.IsDefault
                ? $"/api/toolcategories/{Uri.EscapeDataString(body.Name)}"
                : $"/api/toolcategories/{Uri.EscapeDataString(body.Name)}?resourceNamespace={Uri.EscapeDataString(@namespace.Value)}";
            return ToolsApiHttp.ResourceResult(stored, response, StatusCodes.Status201Created);
        });

    private static Task<IResult> PutAsync(
        string categoryName,
        string? resourceNamespace,
        PutToolCategoryRequest body,
        HttpRequest request,
        HttpResponse response,
        ToolCategoryService service,
        CancellationToken cancellationToken) =>
        ToolsApiHttp.ExecuteAsync(async () => ToolsApiHttp.ResourceResult(
            await service.PutAsync(
                ResourceNamespace.Parse(resourceNamespace),
                categoryName,
                body.Properties,
                ToolsApiHttp.IfMatch(request),
                cancellationToken),
            response,
            StatusCodes.Status200OK));

    private static Task<IResult> DeleteAsync(
        string categoryName,
        string? resourceNamespace,
        HttpRequest request,
        ToolCategoryService service,
        CancellationToken cancellationToken) =>
        ToolsApiHttp.ExecuteAsync(async () =>
        {
            await service.DeleteAsync(
                ResourceNamespace.Parse(resourceNamespace),
                categoryName,
                ToolsApiHttp.IfMatch(request),
                cancellationToken);
            return Results.NoContent();
        });
}
