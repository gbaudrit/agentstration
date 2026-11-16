using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.SystemGraph;
using Agentstration.Systems.Contracts;
using Agentstration.Web.Security;

namespace Agentstration.Systems.Api;

public static class SystemsApiModule
{
    public static IServiceCollection AddSystemsApi(this IServiceCollection services) => services;
    public static IEndpointRouteBuilder MapSystemsApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/systems");
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
        group.MapGet("/", async (string scopeRef, string? @namespace, HttpResponse response, SystemModelService service, CancellationToken ct) =>
            await ExecuteAsync(async () =>
            {
                response.Headers.CacheControl = "no-store";
                return Results.Ok(await service.ListAsync(ResourceScopeRef.Parse(scopeRef),
                    @namespace is null ? null : ResourceNamespace.Parse(@namespace), ct));
            }))
            .Produces<IReadOnlyList<SystemView>>().WithSummary("List authorized logical systems").RequireAuthorization(AgentstrationPolicies.CanReadResources);
        group.MapGet("/{name}", async (string name, string scopeRef, string? @namespace, HttpResponse response, SystemModelService service, CancellationToken ct) =>
            await ExecuteAsync(async () => Result(await service.GetAsync(ResourceScopeRef.Parse(scopeRef), Ns(@namespace), name, ct), response)))
            .Produces<SystemView>().WithSummary("Read or update an authorized logical system").RequireAuthorization(AgentstrationPolicies.CanReadResources);
        group.MapPost("/", async (CreateSystemRequest body, HttpResponse response, SystemModelService service, CancellationToken ct) =>
            await ExecuteAsync(async () => Result(await service.CreateAsync(body, ct), response, 201)))
            .Produces<SystemView>(201).WithSummary("Create a logical system").RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        group.MapPut("/{name}", async (string name, string scopeRef, string? @namespace, RenameSystemRequest body,
            HttpRequest request, HttpResponse response, SystemModelService service, CancellationToken ct) =>
            await ExecuteAsync(async () => Result(await service.RenameAsync(ResourceScopeRef.Parse(scopeRef), Ns(@namespace), name, body, ETag(request), ct), response)))
            .Produces<SystemView>().WithSummary("Read or update an authorized logical system").RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        group.MapPost("/{name}/members", async (string name, string scopeRef, string? @namespace, AddSystemMemberRequest body,
            HttpRequest request, HttpResponse response, SystemModelService service, CancellationToken ct) =>
            await ExecuteAsync(async () => Result(await service.AddMemberAsync(ResourceScopeRef.Parse(scopeRef), Ns(@namespace), name, body.Member, ETag(request), ct), response)))
            .Produces<SystemView>().WithSummary("Read or update an authorized logical system").RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        group.MapDelete("/{name}/members/{uid:guid}", async (string name, Guid uid, string scopeRef, string? @namespace,
            HttpRequest request, HttpResponse response, SystemModelService service, CancellationToken ct) =>
            await ExecuteAsync(async () => Result(await service.RemoveMemberAsync(ResourceScopeRef.Parse(scopeRef), Ns(@namespace), name, uid, ETag(request), ct), response)))
            .Produces<SystemView>().WithSummary("Read or update an authorized logical system").RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        group.MapDelete("/{name}", async (string name, string scopeRef, string? @namespace, HttpRequest request, SystemModelService service, CancellationToken ct) =>
            await ExecuteAsync(async () =>
            {
                await service.DeleteAsync(ResourceScopeRef.Parse(scopeRef), Ns(@namespace), name, ETag(request), ct);
                return Results.NoContent();
            })).Produces(204).WithSummary("Delete a logical system without deleting its members").RequireAuthorization(AgentstrationPolicies.CanDeleteResources);
        return endpoints;
    }

    private static ResourceNamespace Ns(string? value) => ResourceNamespace.Parse(value ?? ResourceNamespace.DefaultValue);
    private static string? ETag(HttpRequest request) => request.Headers.IfMatch.FirstOrDefault();
    private static IResult Result(SystemView value, HttpResponse response, int status = 200)
    {
        response.Headers.ETag = value.ETag;
        response.Headers.CacheControl = "no-store";
        return Results.Json(value, statusCode: status);
    }
    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (SystemModelException error) { return Results.Problem(statusCode: error.StatusCode, title: error.Code); }
        catch (SystemGraphException error) { return Results.Problem(statusCode: error.Code == "system_graph_forbidden" ? 403 : 503, title: "system_context_unavailable"); }
        catch (ResourceConcurrencyException) { return Results.Problem(statusCode: 412, title: "system_changed"); }
        catch (ResourceScopeAccessDeniedException) { return Results.Problem(statusCode: 403, title: "system_scope_unavailable"); }
        catch (ResourceScopePolicyException) { return Results.Problem(statusCode: 400, title: "system_scope_invalid"); }
        catch (ArgumentException) { return Results.Problem(statusCode: 400, title: "system_request_invalid"); }
    }
}
