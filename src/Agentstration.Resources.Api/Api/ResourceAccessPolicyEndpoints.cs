using Agentstration.Api.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Security;

namespace Agentstration.Web.Api.Models;

internal static class ResourceAccessPolicyEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var policies = endpoints.MapGroup("/api/resource-access-policies");
        policies.MapGet("/", ListAsync)
            .Produces<ValueResponse<ResourceAccessPolicyResponse>>()
            .WithSummary("List access policies owned by an exact resource scope")
            .RequireAuthorization(AgentstrationPolicies.CanReadResources);
        policies.MapGet("/{resourceUid:guid}", GetAsync)
            .Produces<ResourceAccessPolicy>()
            .WithSummary("Get the access policy for an exact resource")
            .RequireAuthorization(AgentstrationPolicies.CanReadResources);
        policies.MapPut("/{resourceUid:guid}", PutAsync)
            .Produces<ResourceAccessPolicy>()
            .WithSummary("Create or replace the access policy for an exact resource")
            .RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        policies.MapDelete("/{resourceUid:guid}", DeleteAsync)
            .Produces(StatusCodes.Status204NoContent)
            .WithSummary("Delete the access policy for an exact resource")
            .RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        policies.MapPost("/{resourceUid:guid}/evaluate", EvaluateAsync)
            .Produces<ResourceAccessDecision>()
            .WithSummary("Evaluate an exact resource access decision")
            .RequireAuthorization(AgentstrationPolicies.CanReadResources);
    }

    private static Task<IResult> ListAsync(string ownerScopeRef, IResourceAccessPolicyService service,
        CancellationToken token) => ExecuteAsync(async () => Results.Ok(
            new ValueResponse<ResourceAccessPolicyResponse>(await service.ListAsync(
                ResourceScopeRef.Parse(ownerScopeRef), token))));

    private static Task<IResult> GetAsync(Guid resourceUid, HttpResponse response,
        IResourceAccessPolicyService service, CancellationToken token) => ExecuteAsync(async () =>
    {
        var policy = await service.GetAsync(resourceUid, token)
            ?? throw new ResourceAccessPolicyException("policy_not_found",
                $"Access policy for resource '{resourceUid:D}' was not found.");
        response.Headers.ETag = policy.ETag;
        return Results.Ok(policy.Policy);
    });

    private static Task<IResult> PutAsync(Guid resourceUid, ResourceAccessPolicy body, HttpRequest request,
        HttpResponse response, IResourceAccessPolicyService service, CancellationToken token) => ExecuteAsync(async () =>
    {
        if (body.Resource.Uid != resourceUid)
            throw new ResourceAccessPolicyException("resource_uid_mismatch",
                "The route resource UID and policy resource UID must match.");
        var stored = await service.PutAsync(body, request.Headers.IfMatch.FirstOrDefault(), token);
        response.Headers.ETag = stored.ETag;
        return Results.Ok(stored.Policy);
    });

    private static Task<IResult> DeleteAsync(Guid resourceUid, HttpRequest request,
        IResourceAccessPolicyService service, CancellationToken token) => ExecuteAsync(async () =>
    {
        await service.DeleteAsync(resourceUid, request.Headers.IfMatch.FirstOrDefault(), token);
        return Results.NoContent();
    });

    private static Task<IResult> EvaluateAsync(Guid resourceUid, EvaluateResourceAccessRequest body,
        IResourceAccessPolicyService service, CancellationToken token) => ExecuteAsync(async () =>
    {
        if (body.Resource.Uid != resourceUid)
            throw new ResourceAccessPolicyException("resource_uid_mismatch",
                "The route resource UID and evaluation resource UID must match.");
        return Results.Ok(await service.EvaluateAsync(body.Resource, body.ConsumerScopeRef, body.Action, token));
    });

    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (ResourceAccessPolicyException exception)
        {
            var status = exception.Code is "resource_not_found" or "policy_not_found"
                ? StatusCodes.Status404NotFound : StatusCodes.Status422UnprocessableEntity;
            return Results.Problem(statusCode: status, title: "Resource access policy error",
                detail: exception.Message, extensions: new Dictionary<string, object?> { ["code"] = exception.Code });
        }
        catch (ResourceConcurrencyException exception)
        { return Results.Problem(statusCode: 409, title: "Resource access policy conflict", detail: exception.Message); }
        catch (ResourceScopeAccessDeniedException exception)
        { return Results.Problem(statusCode: 403, title: "Resource scope access denied", detail: exception.Message); }
        catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidOperationException)
        { return Results.Problem(statusCode: 422, title: "Invalid resource access policy operation", detail: exception.Message); }
    }
}
