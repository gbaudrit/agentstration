using Agentstration.Api.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Tools;
using Agentstration.Tools.Contracts;
using Agentstration.Web.Security;

namespace Agentstration.Web.Api.Models;

internal static class ToolSetEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/", ListAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources)
            .WithSummary("List ToolSets");
        group.MapGet("/{name}", GetAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources)
            .WithSummary("Get a ToolSet");
        group.MapGet("/{name}/versions", ListVersionsAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources)
            .WithSummary("List published ToolSet versions");
        group.MapGet("/{name}/versions/{version}", GetVersionAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources)
            .WithSummary("Get a published ToolSet version");
        group.MapPost("/", CreateAsync).RequireAuthorization(AgentstrationPolicies.CanWriteResources)
            .WithSummary("Create a ToolSet");
        group.MapPut("/{name}", PutAsync).RequireAuthorization(AgentstrationPolicies.CanWriteResources)
            .WithSummary("Update a ToolSet");
        group.MapPost("/{name}/versions", PublishAsync).RequireAuthorization(AgentstrationPolicies.CanWriteResources)
            .WithSummary("Publish an immutable ToolSet version");
        group.MapPost("/{name}/route", ResolveRouteAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources)
            .WithSummary("Resolve a ToolSet route");
        group.MapDelete("/{name}", DeleteAsync).RequireAuthorization(AgentstrationPolicies.CanDeleteResources)
            .WithSummary("Delete an unpublished ToolSet");
    }

    private static Task<IResult> ListAsync(string? resourceNamespace, ToolSetService service, CancellationToken token) =>
        ToolsApiHttp.ExecuteAsync(async () =>
        {
            var values = (await service.ListAsync(token)).Select(value => value.Value);
            if (!string.IsNullOrWhiteSpace(resourceNamespace))
            {
                var ns = ResourceNamespace.Parse(resourceNamespace);
                values = values.Where(value => value.Namespace == ns);
            }
            return Results.Ok(new ValueResponse<ToolSetResource>(values.ToArray()));
        });

    private static Task<IResult> GetAsync(string name, string? resourceNamespace, HttpResponse response,
        ToolSetService service, CancellationToken token) => ToolsApiHttp.ExecuteAsync(async () =>
    {
        var ns = ResourceNamespace.Parse(resourceNamespace);
        var stored = await service.GetAsync(ns, name, token)
            ?? throw new ResourceNotFoundException(new(ToolResourceKinds.ToolSet, name, ns));
        return ToolsApiHttp.ResourceResult(stored, response, StatusCodes.Status200OK);
    });

    private static Task<IResult> ListVersionsAsync(string name, string? resourceNamespace,
        ToolSetService service, CancellationToken token) => ToolsApiHttp.ExecuteAsync(async () =>
            Results.Ok(new ValueResponse<ToolSetVersionResource>(
                (await service.ListVersionsAsync(ResourceNamespace.Parse(resourceNamespace), name, token))
                .Select(value => value.Value)
                .ToArray())));

    private static Task<IResult> GetVersionAsync(string name, string version, string? resourceNamespace,
        ToolSetService service, CancellationToken token) => ToolsApiHttp.ExecuteAsync(async () =>
    {
        var ns = ResourceNamespace.Parse(resourceNamespace);
        var stored = await service.GetVersionAsync(ns, name, version, token)
            ?? throw new ToolSetValidationException("tool_set_version_not_found", $"Published ToolSet '{ns}/{name}:{version}' was not found.");
        return Results.Ok(stored.Value);
    });

    private static Task<IResult> CreateAsync(CreateToolSetRequest body, HttpResponse response,
        ToolSetService service, CancellationToken token) => ToolsApiHttp.ExecuteAsync(async () =>
    {
        var ns = ResourceNamespace.Parse(body.Namespace);
        var stored = await service.CreateAsync(new ToolSetResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ToolResourceKinds.ToolSet,
            Metadata = new ResourceMetadata { Name = body.Name, Namespace = ns },
            Definition = body.Properties
        }, token);
        response.Headers.Location = ns.IsDefault
            ? $"/api/toolsets/{Uri.EscapeDataString(body.Name)}"
            : $"/api/toolsets/{Uri.EscapeDataString(body.Name)}?resourceNamespace={Uri.EscapeDataString(ns.Value)}";
        return ToolsApiHttp.ResourceResult(stored, response, StatusCodes.Status201Created);
    });

    private static Task<IResult> PutAsync(string name, string? resourceNamespace, PutToolSetRequest body,
        HttpRequest request, HttpResponse response, ToolSetService service, CancellationToken token) =>
        ToolsApiHttp.ExecuteAsync(async () => ToolsApiHttp.ResourceResult(
            await service.PutAsync(ResourceNamespace.Parse(resourceNamespace), name, body.Properties,
                ToolsApiHttp.IfMatch(request), token), response, StatusCodes.Status200OK));

    private static Task<IResult> PublishAsync(string name, string? resourceNamespace, PublishToolSetVersionRequest body,
        ToolSetService service, CancellationToken token) => ToolsApiHttp.ExecuteAsync(async () =>
    {
        var stored = await service.PublishAsync(ResourceNamespace.Parse(resourceNamespace), name, body.Version, token);
        return Results.Created($"/api/toolsets/{Uri.EscapeDataString(name)}/versions/{Uri.EscapeDataString(body.Version)}", stored.Value);
    });

    private static Task<IResult> ResolveRouteAsync(string name, string? resourceNamespace, ResolveToolSetRouteRequest body,
        ToolSetService service, CancellationToken token) => ToolsApiHttp.ExecuteAsync(async () =>
            Results.Ok(await service.ResolveRouteAsync(ResourceNamespace.Parse(resourceNamespace), name,
                body.Version, body.Capability, body.Route, token)));

    private static Task<IResult> DeleteAsync(string name, string? resourceNamespace, HttpRequest request,
        ToolSetService service, CancellationToken token) => ToolsApiHttp.ExecuteAsync(async () =>
    {
        await service.DeleteAsync(ResourceNamespace.Parse(resourceNamespace), name, ToolsApiHttp.IfMatch(request), token);
        return Results.NoContent();
    });
}
