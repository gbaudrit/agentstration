using Agentstration.Api.Contracts;
using Agentstration.Functions.Abstractions;
using Agentstration.Functions.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Schemas;
using Agentstration.Web.Security;

namespace Agentstration.Functions.Api;

public static class FunctionEndpoints
{
    public static void MapFunctionsApi(this IEndpointRouteBuilder endpoints)
    {
        var functions = endpoints.MapGroup("/api/transformation-functions").WithTags("Transformation functions");
        functions.MapGet("/", async (string? scopeRef, int? skip, int? take, TransformationFunctionService service, IResourceScopeOperations scopes, CancellationToken ct) => await Execute(async () =>
        {
            Page(skip, take);
            var values = await service.ListAsync(Scope(scopeRef, scopes, FunctionResourceKinds.TransformationFunction), skip ?? 0, take ?? 100, ct);
            return Results.Ok(new ValueResponse<TransformationFunctionResource>(values.Select(v => v.Value).ToArray()));
        })).WithSummary("List visible transformation functions").Produces<ValueResponse<TransformationFunctionResource>>().RequireAuthorization(AgentstrationPolicies.CanReadResources);
        functions.MapGet("/implementations", (NativeFunctionRegistry registry) => Results.Ok(new ValueResponse<NativeFunctionDescriptor>(registry.List())))
            .WithSummary("List registered native implementations").Produces<ValueResponse<NativeFunctionDescriptor>>().RequireAuthorization(AgentstrationPolicies.CanReadResources);
        functions.MapGet("/{name}", async (string name, string scopeRef, string? @namespace, TransformationFunctionService service, HttpResponse response, CancellationToken ct) => await Execute(async () =>
        {
            var stored = await service.GetAsync(ResourceScopeRef.Parse(scopeRef), Ns(@namespace), name, ct);
            if (stored is null) return Results.NotFound();
            response.Headers.ETag = stored.ETag;
            return Results.Ok(stored.Value);
        })).WithSummary("Get a transformation function").Produces<TransformationFunctionResource>().Produces(StatusCodes.Status404NotFound).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        functions.MapPost("/resolve", async (ResolveFunctionRequest request, TransformationFunctionService service, IResourceScopeOperations scopes, CancellationToken ct) =>
            await Execute(async () => Results.Ok(await service.ResolveAsync(request.Function, Scope(request.ConsumerScopeRef, scopes, FunctionResourceKinds.TransformationFunction), ct))))
            .WithSummary("Resolve an exact authorized function contract").Produces<ResolvedTransformationFunction>().RequireAuthorization(AgentstrationPolicies.CanReadResources);
        functions.MapPost("/", async (TransformationFunctionResource body, TransformationFunctionService service, HttpResponse response, CancellationToken ct) =>
            await Execute(async () => Written(await service.PutAsync(body, null, true, ct), response, 201)))
            .WithSummary("Declare a transformation function").Produces<TransformationFunctionResource>(StatusCodes.Status201Created).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        functions.MapPut("/{name}", async (string name, string scopeRef, TransformationFunctionResource body, TransformationFunctionService service, HttpRequest request, HttpResponse response, CancellationToken ct) =>
            await Execute(async () => Written(await service.PutAsync(body with { ScopeRef = ResourceScopeRef.Parse(scopeRef), Metadata = body.Metadata with { Name = name } }, request.Headers.IfMatch.FirstOrDefault(), false, ct), response)))
            .WithSummary("Update a transformation function").Produces<TransformationFunctionResource>().RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        var kinds = endpoints.MapGroup("/api/function-kinds").WithTags("Function kinds");
        kinds.MapGet("/", async (string? scopeRef, int? skip, int? take, TransformationFunctionService service, IResourceScopeOperations scopes, CancellationToken ct) => await Execute(async () =>
        {
            Page(skip, take);
            var values = await service.ListKindsAsync(Scope(scopeRef, scopes, FunctionResourceKinds.FunctionKind), skip ?? 0, take ?? 100, ct);
            return Results.Ok(new ValueResponse<FunctionKindResource>(values.Select(v => v.Value).ToArray()));
        })).WithSummary("List visible function kinds").Produces<ValueResponse<FunctionKindResource>>().RequireAuthorization(AgentstrationPolicies.CanReadResources);
        kinds.MapGet("/{name}", async (string name, string scopeRef, string? @namespace, TransformationFunctionService service, HttpResponse response, CancellationToken ct) => await Execute(async () =>
        {
            var stored = await service.GetKindAsync(ResourceScopeRef.Parse(scopeRef), Ns(@namespace), name, ct);
            if (stored is null) return Results.NotFound();
            response.Headers.ETag = stored.ETag;
            return Results.Ok(stored.Value);
        })).WithSummary("Get a function kind").Produces<FunctionKindResource>().Produces(StatusCodes.Status404NotFound).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        kinds.MapPost("/", async (FunctionKindResource body, TransformationFunctionService service, HttpResponse response, CancellationToken ct) =>
            await Execute(async () => Written(await service.PutKindAsync(body, null, true, ct), response, 201)))
            .WithSummary("Declare a function kind").Produces<FunctionKindResource>(StatusCodes.Status201Created).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        kinds.MapPut("/{name}", async (string name, string scopeRef, FunctionKindResource body, TransformationFunctionService service, HttpRequest request, HttpResponse response, CancellationToken ct) =>
            await Execute(async () => Written(await service.PutKindAsync(body with { ScopeRef = ResourceScopeRef.Parse(scopeRef), Metadata = body.Metadata with { Name = name } }, request.Headers.IfMatch.FirstOrDefault(), false, ct), response)))
            .WithSummary("Update a function kind").Produces<FunctionKindResource>().RequireAuthorization(AgentstrationPolicies.CanWriteResources);
    }

    private static IResult Written<T>(StoredResource<T> value, HttpResponse response, int status = 200) where T : Resource
    { response.Headers.ETag = value.ETag; return Results.Json(value.Value, statusCode: status); }
    private static ResourceScopeRef Scope(string? value, IResourceScopeOperations scopes, string kind) => value is null ? scopes.DefaultScopeRef(kind) : ResourceScopeRef.Parse(value);
    private static ResourceNamespace Ns(string? value) => ResourceNamespace.Parse(value ?? ResourceNamespace.DefaultValue);
    private static void Page(int? skip, int? take)
    {
        if (skip is < 0 or > 1_000_000 || take is < 1 or > 100) throw new TransformationFunctionException("function_page_invalid", "Pagination exceeds the supported bounds.");
    }
    private static async Task<IResult> Execute(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (TransformationFunctionException error) { return Results.Problem(statusCode: error.Code == "function_use_denied" ? 403 : 422, title: error.Code, detail: error.Message); }
        catch (DataContractException error) { return Results.Problem(statusCode: 422, title: error.Code, detail: error.Message); }
        catch (ResourceConcurrencyException error) { return Results.Problem(statusCode: 412, title: "function_concurrency_conflict", detail: error.Message); }
        catch (ResourceScopeAccessDeniedException error) { return Results.Problem(statusCode: 403, title: "function_scope_denied", detail: error.Message); }
        catch (ArgumentException error) { return Results.Problem(statusCode: 422, title: "function_definition_invalid", detail: error.Message); }
    }
    public sealed record ResolveFunctionRequest(FunctionReference Function, string? ConsumerScopeRef = null);
}
