using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Management.Core;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Security;
using Agentstration.Tools;
using Agentstration.Web.Security;

namespace Agentstration.Web.Api.Models;

internal static class ToolDefinitionEndpoints
{
    public static void Map(RouteGroupBuilder definitions)
    {
        definitions.MapGet("/", ListAsync)
            .Produces<ValueResponse<ToolDefinitionResource>>()
            .WithSummary("List ToolDefinitions")
            .RequireAuthorization(AgentstrationPolicies.CanReadResources);
        definitions.MapGet("/{name}", GetAsync)
            .Produces<ToolDefinitionResource>()
            .WithSummary("Get a ToolDefinition")
            .RequireAuthorization(AgentstrationPolicies.CanReadResources);
        definitions.MapPost("/", CreateAsync)
            .Produces<ToolDefinitionResource>(StatusCodes.Status201Created)
            .WithSummary("Create a ToolDefinition")
            .RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        definitions.MapPut("/{name}", PutAsync)
            .Produces<ToolDefinitionResource>()
            .WithSummary("Update a ToolDefinition")
            .RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        definitions.MapPut("/{name}/enabled", SetEnabledAsync)
            .Produces<ToolDefinitionResource>()
            .WithSummary("Enable or disable a ToolDefinition")
            .RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        definitions.MapDelete("/{name}", DeleteAsync)
            .Produces(StatusCodes.Status204NoContent)
            .WithSummary("Delete a ToolDefinition")
            .RequireAuthorization(AgentstrationPolicies.CanWriteResources);
    }

    private static Task<IResult> ListAsync(string? @namespace, ToolDefinitionService service, CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            var values = (await service.ListAsync(cancellationToken)).Select(value => value.Value);
            if (!string.IsNullOrWhiteSpace(@namespace))
            {
                var parsed = ResourceNamespace.Parse(@namespace);
                values = values.Where(value => value.Namespace == parsed);
            }
            return Results.Ok(new ValueResponse<ToolDefinitionResource>(values.ToArray()));
        });

    private static Task<IResult> GetAsync(string name, string? @namespace, HttpResponse response, ToolDefinitionService service, CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            var ns = ResourceNamespace.Parse(@namespace);
            var stored = await service.GetAsync(name, ns, cancellationToken)
                ?? throw new ResourceNotFoundException(new(ResourceKinds.ToolDefinition, name, ns));
            return ModelManagementHttp.ResourceResult(stored, response, 200);
        });

    private static Task<IResult> CreateAsync(
        CreateToolDefinitionRequest body,
        HttpResponse response,
        ToolDefinitionService service,
        ICurrentRequestContext context,
        CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            var ns = ResourceNamespace.Parse(body.Namespace);
            var resource = Resource(body.Name, ns, body.Properties, ResourceScopeRef.Workspace(context.Current.WorkspaceId));
            var stored = await service.PutAsync(resource, null, true, cancellationToken);
            response.Headers.Location = $"/api/tooldefinitions/{Uri.EscapeDataString(body.Name)}?namespace={Uri.EscapeDataString(ns.Value)}";
            return ModelManagementHttp.ResourceResult(stored, response, 201);
        });

    private static Task<IResult> PutAsync(
        string name,
        string? @namespace,
        PutToolDefinitionRequest body,
        HttpRequest request,
        HttpResponse response,
        ToolDefinitionService service,
        ICurrentRequestContext context,
        CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            var ns = ResourceNamespace.Parse(@namespace);
            var resource = Resource(name, ns, body.Properties, ResourceScopeRef.Workspace(context.Current.WorkspaceId));
            return ModelManagementHttp.ResourceResult(
                await service.PutAsync(resource, ModelManagementHttp.IfMatch(request), false, cancellationToken), response, 200);
        });

    private static Task<IResult> SetEnabledAsync(
        string name,
        string? @namespace,
        SetToolDefinitionEnabledRequest body,
        HttpRequest request,
        HttpResponse response,
        ToolDefinitionService service,
        CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () => ModelManagementHttp.ResourceResult(
            await service.SetEnabledAsync(name, ResourceNamespace.Parse(@namespace), body.Enabled, ModelManagementHttp.IfMatch(request), cancellationToken), response, 200));

    private static Task<IResult> DeleteAsync(
        string name,
        string? @namespace,
        HttpRequest request,
        ToolDefinitionService service,
        CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            await service.DeleteAsync(name, ResourceNamespace.Parse(@namespace), ModelManagementHttp.IfMatch(request), cancellationToken);
            return Results.NoContent();
        });

    private static ToolDefinitionResource Resource(string name, ResourceNamespace @namespace, ToolDefinitionProperties properties, ResourceScopeRef scopeRef) => new()
    {
        ApiVersion = ManagementApiVersions.CoreV1,
        Kind = ResourceKinds.ToolDefinition,
        Metadata = new ResourceMetadata { Name = name, Namespace = @namespace },
        ScopeRef = scopeRef,
        Definition = properties
    };
}
