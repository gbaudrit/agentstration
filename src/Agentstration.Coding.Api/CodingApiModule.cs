using Agentstration.Coding.Contracts;
using Agentstration.Web.Security;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Work;

namespace Agentstration.Coding.Api;

public static partial class CodingApiModule
{
    public static IEndpointRouteBuilder MapCodingApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/workspaces/{workspaceName}/projects/{projectId:guid}/coding").RequireAuthorization(AgentstrationPolicies.CanExecuteRuns).AddEndpointFilter(RequireWorkspaceAsync);
        group.MapPost("/repositories/resolve", async (Guid projectId, ResolveCodingRepositoriesRequest request,
            CodingRepositoryService service, ICurrentRequestContext current, CancellationToken token) =>
                Response(await service.ResolveAsync(Workspace(current), new(projectId), request, token)))
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(64 * 1024));
        group.MapPost("/pull-requests", async (Guid projectId, InvokeCodingPullRequestRequest request,
            CodingRepositoryService service, ICurrentRequestContext current, CancellationToken token) =>
                Response(await service.InvokePullRequestAsync(Workspace(current), new(projectId), request, token), true))
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(64 * 1024));
        group.MapPost("/issues/acknowledge", async (Guid projectId, AcknowledgeCodingIssueRequest request,
            CodingIssueCoordinationService service, ICurrentRequestContext current, CancellationToken token) =>
                Response(await service.AcknowledgeAsync(Workspace(current), new(projectId), request, token), true))
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(32 * 1024));
        group.MapGet("/plans/{planId:guid}/issue-readiness", async (Guid projectId, Guid planId,
            CodingIssueCoordinationService service, ICurrentRequestContext current, CancellationToken token) =>
                Response(await service.ReadinessAsync(Workspace(current), new(projectId), new(planId), token)));
        MapHarnessProfiles(endpoints);
        MapCodingDeliveries(endpoints);
        return endpoints;
    }

    private static async ValueTask<object?> RequireWorkspaceAsync(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var current = invocation.HttpContext.RequestServices.GetRequiredService<ICurrentRequestContext>().Current;
        var identities = invocation.HttpContext.RequestServices.GetRequiredService<IIdentityStore>();
        var workspace = await identities.GetWorkspaceAsync(current.TenantId, current.WorkspaceId, invocation.HttpContext.RequestAborted);
        var route = invocation.HttpContext.Request.RouteValues["workspaceName"]?.ToString();
        if (workspace is null || !string.Equals(route, workspace.Name, StringComparison.OrdinalIgnoreCase)
            && (!Guid.TryParse(route, out var id) || id != workspace.Id)) return Results.NotFound();
        return await next(invocation);
    }

    private static WorkspaceId Workspace(ICurrentRequestContext current) => new(current.Current.WorkspaceId);
    private static IResult Response<T>(Result<T> result, bool accepted = false) where T : class => result.IsSuccess
        ? result.Value is CodingIssueIntakeResponse { Status: CodingIssueIntakeStatus.Conflict } conflict
            ? Results.Problem(statusCode: 409, title: conflict.ErrorCode ?? "coding_issue_intake_conflict",
                extensions: new Dictionary<string, object?> { ["intake"] = conflict })
            : result.Value is CodingPullRequestResponse { Result.Status: CodingProviderStatus.Rejected } rejected
            ? Results.Problem(statusCode: 409, title: rejected.Result.ErrorCode ?? "coding_pull_request_rejected",
                extensions: new Dictionary<string, object?> { ["operationId"] = rejected.Id })
            : Results.Json(result.Value, statusCode: accepted ? StatusCodes.Status202Accepted : StatusCodes.Status200OK)
        : Results.Problem(statusCode: result.ErrorCode?.EndsWith("not_found", StringComparison.Ordinal) == true ? 404
            : result.ErrorCode?.Contains("denied", StringComparison.Ordinal) == true ? 403
            : result.ErrorCode?.Contains("changed", StringComparison.Ordinal) == true ? 409 : 400, title: result.ErrorCode);
}
