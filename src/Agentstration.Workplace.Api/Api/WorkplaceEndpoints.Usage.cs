using Agentstration.Application.Work;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Agentstration.Web.Security;

namespace Agentstration.Web;

public static partial class WorkplaceEndpoints
{
    private static void MapWorkUsage(RouteGroupBuilder workspaces)
    {
        var usage = workspaces.MapGroup("/projects/{projectId:guid}/usage").WithTags("Project usage");
        usage.MapPost("/captures", async (string workspaceName, Guid projectId, CaptureWorkUsageRequest request, WorkUsageService service, CancellationToken token) =>
        {
            var result = await service.CaptureAsync(WorkspaceId(workspaceName), new(projectId), new(request.PlanId), request.AttemptId, token);
            return result.IsSuccess ? Results.Ok(result.Value) : UsageProblem(result.ErrorCode!);
        }).RequireAuthorization(AgentstrationPolicies.CanExecuteRuns);
        usage.MapGet("/", async (string workspaceName, Guid projectId, int? skip, int? take, WorkUsageService service, CancellationToken token) =>
        {
            var result = await service.ListAsync(WorkspaceId(workspaceName), new(projectId), skip ?? 0, take ?? 25, token);
            return result.IsSuccess ? Results.Ok(result.Value) : UsageProblem(result.ErrorCode!);
        }).RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        usage.MapGet("/{usageId:guid}", async (string workspaceName, Guid projectId, Guid usageId, WorkUsageService service, CancellationToken token) =>
        {
            var result = await service.GetAsync(WorkspaceId(workspaceName), new(projectId), new(usageId), token);
            return result.IsSuccess ? Results.Ok(result.Value) : UsageProblem(result.ErrorCode!);
        }).RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        usage.MapPost("/summary", async (string workspaceName, Guid projectId, WorkUsageSummaryQuery request, WorkUsageService service, CancellationToken token) =>
        {
            var result = await service.SummarizeAsync(WorkspaceId(workspaceName), new(projectId), request, token);
            return result.IsSuccess ? Results.Ok(result.Value) : UsageProblem(result.ErrorCode!);
        }).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(16 * 1024)).RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        usage.MapPost("/{rootId:guid}/corrections", async (string workspaceName, Guid projectId, Guid rootId, CorrectWorkUsageRequest request,
            WorkUsageService service, CancellationToken token) =>
        {
            var result = await service.CorrectAsync(WorkspaceId(workspaceName), new(projectId), new(rootId), new(request.CommandId,
                new(request.ExpectedPreviousId), request.ExpectedPreviousDigest, request.Measurement, request.Totals, request.Reason, request.EvidenceReference), token);
            return result.IsSuccess ? Results.Ok(result.Value) : UsageProblem(result.ErrorCode!);
        }).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(32 * 1024)).RequireAuthorization(AgentstrationPolicies.CanExecuteRuns);
        usage.MapPost("/{usageId:guid}/publication", async (string workspaceName, Guid projectId, Guid usageId, WorkUsageService service, CancellationToken token) =>
            SharingAccepted(workspaceName, projectId, await service.PublishAsync(WorkspaceId(workspaceName), new(projectId), new(usageId), token)))
            .RequireAuthorization(AgentstrationPolicies.CanExecuteRuns);
    }
    private static IResult UsageProblem(string code) => code.StartsWith("work_project_", StringComparison.Ordinal) ? WorkProjectProblem(code)
        : Results.Problem(title: code, statusCode: code.EndsWith("not_found", StringComparison.Ordinal) ? 404
            : code.EndsWith("conflict", StringComparison.Ordinal) || code is "usage_execution_not_final" or "usage_sharing_identity_changed" ? 409 : 400);
}
