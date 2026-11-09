using Agentstration.Api.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Security;
using Agentstration.Tools;
using Agentstration.Tools.Contracts;
using Agentstration.Web.Security;

namespace Agentstration.Web.Api.Models;

internal static class ToolDefinitionEndpoints
{
    public static void Map(RouteGroupBuilder definitions)
    {
        definitions.MapGet("/{name}/usages", UsagesAsync)
            .Produces<ValueResponse<ToolDefinitionUsage>>()
            .WithSummary("List published consumers and their pinned ToolDefinition versions")
            .RequireAuthorization(AgentstrationPolicies.CanReadResources);
        definitions.MapGet("/{name}/versions", VersionsAsync)
            .Produces<ValueResponse<ToolDefinitionVersionResource>>()
            .WithSummary("List immutable ToolDefinition publications")
            .RequireAuthorization(AgentstrationPolicies.CanReadResources);
        definitions.MapPost("/{name}/versions", PublishAsync)
            .Produces<ToolDefinitionVersionResource>(StatusCodes.Status201Created)
            .WithSummary("Publish the ToolDefinition draft without changing consumers")
            .RequireAuthorization(AgentstrationPolicies.CanWriteResources);
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
        definitions.MapPost("/from-acquisition/{id}", CreateFromAcquisitionAsync)
            .Produces<ToolDefinitionResource>(StatusCodes.Status201Created)
            .WithSummary("Create a ToolDefinition from a successful Data Source acquisition")
            .RequireAuthorization(AgentstrationPolicies.CanWriteResources, AgentstrationPolicies.CanReadRuns);
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

    private static Task<IResult> UsagesAsync(string name, string? @namespace, ToolDefinitionService service,
        CancellationToken cancellationToken) => ToolsApiHttp.ExecuteAsync(async () =>
            Results.Ok(new ValueResponse<ToolDefinitionUsage>(await service.ListUsagesAsync(name,
                ResourceNamespace.Parse(@namespace), cancellationToken))));

    private static Task<IResult> VersionsAsync(string name, string? @namespace, ToolDefinitionService service,
        CancellationToken cancellationToken) => ToolsApiHttp.ExecuteAsync(async () =>
            Results.Ok(new ValueResponse<ToolDefinitionVersionResource>(await service.ListVersionsAsync(name,
                ResourceNamespace.Parse(@namespace), cancellationToken))));

    private static Task<IResult> PublishAsync(string name, string? @namespace, PublishToolDefinitionRequest body,
        ToolDefinitionService service, CancellationToken cancellationToken) => ToolsApiHttp.ExecuteAsync(async () =>
            Results.Created($"/api/tooldefinitions/{Uri.EscapeDataString(name)}/versions/{Uri.EscapeDataString(body.Version)}",
                await service.PublishAsync(name, ResourceNamespace.Parse(@namespace), body.Version, cancellationToken, body.ExpectedETag)));

    private static Task<IResult> ListAsync(string? @namespace, ToolDefinitionService service, ICurrentRequestContext current,
        IRequestContextScopeFactory scopes, CancellationToken cancellationToken) =>
        ToolsApiHttp.ExecuteAsync(async () =>
        {
            var values = (await service.ListAsync(cancellationToken)).Select(value => value.Value)
                .Concat(await ToolsApiHttp.ListOperationalAcquisitionToolsAsync(service, current, scopes, cancellationToken));
            if (!string.IsNullOrWhiteSpace(@namespace))
            {
                var parsed = ResourceNamespace.Parse(@namespace);
                values = values.Where(value => value.Namespace == parsed);
            }
            return Results.Ok(new ValueResponse<ToolDefinitionResource>(values.ToArray()));
        });

    private static Task<IResult> GetAsync(string name, string? @namespace, string? scopeRef, HttpResponse response,
        ToolDefinitionService service, ICurrentRequestContext current, IRequestContextScopeFactory scopes, CancellationToken cancellationToken) =>
        ToolsApiHttp.ExecuteAsync(async () =>
        {
            var ns = ResourceNamespace.Parse(@namespace);
            var target = string.IsNullOrWhiteSpace(scopeRef) ? (ResourceScopeRef?)null : ResourceScopeRef.Parse(scopeRef);
            using var operationalScope = ToolsApiHttp.EnterOperationalWorkspace(target, current, scopes);
            var stored = await (target is { } exact ? service.GetExactAsync(name, ns, exact, cancellationToken)
                : service.GetAsync(name, ns, cancellationToken))
                ?? throw new ResourceNotFoundException(new(ToolResourceKinds.ToolDefinition, name, ns));
            return ToolsApiHttp.ResourceResult(stored, response, 200);
        });

    private static Task<IResult> CreateAsync(
        CreateToolDefinitionRequest body,
        HttpResponse response,
        ToolDefinitionService service,
        IResourceScopeOperations scopeOperations,
        CancellationToken cancellationToken) =>
        ToolsApiHttp.ExecuteAsync(async () =>
        {
            var ns = ResourceNamespace.Parse(body.Namespace);
            var resource = Resource(body.Name, ns, body.Properties, body.ScopeRef ?? scopeOperations.DefaultScopeRef(ToolResourceKinds.ToolDefinition));
            var stored = await service.PutAsync(resource, null, true, cancellationToken);
            response.Headers.Location = $"/api/tooldefinitions/{Uri.EscapeDataString(body.Name)}?namespace={Uri.EscapeDataString(ns.Value)}";
            return ToolsApiHttp.ResourceResult(stored, response, 201);
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
        ToolsApiHttp.ExecuteAsync(async () =>
        {
            var ns = ResourceNamespace.Parse(@namespace);
            var owner = context.AccessMode switch
            {
                ControlPlaneAccessMode.Instance => ResourceScopeRef.Instance,
                ControlPlaneAccessMode.TenantAdministration => ResourceScopeRef.Tenant(context.Current.TenantId),
                _ => (ResourceScopeRef?)null
            };
            var resource = Resource(name, ns, body.Properties, owner ?? ResourceScopeRef.Workspace(context.Current.WorkspaceId));
            return ToolsApiHttp.ResourceResult(
                await service.PutAsync(resource, ToolsApiHttp.IfMatch(request), false, cancellationToken), response, 200);
        });

    private static Task<IResult> CreateFromAcquisitionAsync(
        string id,
        string? @namespace,
        CreateToolDefinitionFromAcquisitionRequest body,
        HttpResponse response,
        IToolDefinitionAcquisitionFactory factory,
        CancellationToken cancellationToken) =>
        ToolsApiHttp.ExecuteAsync(async () =>
        {
            var stored = await factory.CreateAsync(ResourceNamespace.Parse(@namespace), id, body.Name,
                body.DisplayName, body.ArtifactId, cancellationToken);
            response.Headers.Location = $"/api/tooldefinitions/{Uri.EscapeDataString(stored.Value.Name)}?namespace={Uri.EscapeDataString(stored.Value.Namespace.Value)}&scopeRef={Uri.EscapeDataString(stored.Value.ScopeRef!.Value.Value)}";
            return ToolsApiHttp.ResourceResult(stored, response, StatusCodes.Status201Created);
        });

    private static Task<IResult> SetEnabledAsync(
        string name,
        string? @namespace,
        SetToolDefinitionEnabledRequest body,
        HttpRequest request,
        HttpResponse response,
        ToolDefinitionService service,
        CancellationToken cancellationToken) =>
        ToolsApiHttp.ExecuteAsync(async () => ToolsApiHttp.ResourceResult(
            await service.SetEnabledAsync(name, ResourceNamespace.Parse(@namespace), body.Enabled, ToolsApiHttp.IfMatch(request), cancellationToken), response, 200));

    private static Task<IResult> DeleteAsync(
        string name,
        string? @namespace,
        HttpRequest request,
        ToolDefinitionService service,
        CancellationToken cancellationToken) =>
        ToolsApiHttp.ExecuteAsync(async () =>
        {
            await service.DeleteAsync(name, ResourceNamespace.Parse(@namespace), ToolsApiHttp.IfMatch(request), cancellationToken);
            return Results.NoContent();
        });

    private static ToolDefinitionResource Resource(string name, ResourceNamespace @namespace, ToolDefinitionProperties properties, ResourceScopeRef scopeRef) => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = ToolResourceKinds.ToolDefinition,
        Metadata = new ResourceMetadata { Name = name, Namespace = @namespace },
        ScopeRef = scopeRef,
        Definition = properties
    };
}
