using Agentstration.Application.Work;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Agentstration.Web.Security;

namespace Agentstration.Web;

public static partial class WorkplaceEndpoints
{
    private static void MapTaskExecutionEvidence(RouteGroupBuilder group)
    {
        group.MapGet("/tasks/{taskId:guid}/execution-history", async (string workspaceName, Guid taskId, int? skip, int? take,
            WorkTaskExecutionEvidenceService service, CancellationToken token) =>
        {
            var result = await service.ListAsync(WorkspaceId(workspaceName), new(taskId), skip ?? 0, take ?? 25, token);
            return result.IsSuccess ? Results.Ok(result.Value) : EvidenceProblem(result.ErrorCode!);
        }).RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        group.MapGet("/tasks/{taskId:guid}/results/page", async (string workspaceName, Guid taskId, int? skip, int? take,
            WorkTaskExecutionEvidenceService service, CancellationToken token) =>
        {
            var result = await service.ResultsAsync(WorkspaceId(workspaceName), new(taskId), skip ?? 0, take ?? 25, token);
            return result.IsSuccess ? Results.Ok(result.Value) : EvidenceProblem(result.ErrorCode!);
        }).RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        group.MapGet("/tasks/{taskId:guid}/artifacts/page", async (string workspaceName, Guid taskId, int? skip, int? take,
            WorkTaskExecutionEvidenceService service, CancellationToken token) =>
        {
            var workspace = WorkspaceId(workspaceName);
            var result = await service.ArtifactsAsync(workspace, new(taskId), skip ?? 0, take ?? 25, token);
            if (!result.IsSuccess) return EvidenceProblem(result.ErrorCode!);
            var page = result.Value!;
            return Results.Ok(new WorkTaskHistoryPage<WorkTaskArtifactResponse>(page.Items.Select(value => new WorkTaskArtifactResponse(value.Id.Value,
                value.FlowRunId, value.Name, value.ContentType, value.Length, value.CreatedAt, value.Sequence,
                $"/api/workspaces/{Uri.EscapeDataString(workspaceName)}/tasks/{taskId}/artifacts/{value.Id}/content")).ToArray(), page.Skip, page.Take, page.TotalCount));
        }).RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        group.MapPost("/work-plans/{planId:guid}/tasks/{taskId:guid}/attempts/{attemptId:guid}/evidence", async (string workspaceName,
            Guid planId, Guid taskId, Guid attemptId, RecordWorkTaskExecutionEvidenceRequest request, WorkTaskExecutionEvidenceService service, CancellationToken token) =>
        {
            var result = await service.RecordAsync(WorkspaceId(workspaceName), new(planId), new(taskId), new(request.EventId, attemptId,
                request.ExpectedPlanVersion, request.ExpectedTaskVersion, request.AdmittedTaskVersion, request.TaskDigest, request.Summary,
                request.ResultIds?.Select(id => new WorkTaskResultId(id)).ToArray()!, request.ArtifactIds?.Select(id => new WorkTaskArtifactId(id)).ToArray()!), token);
            return result.IsSuccess ? Results.Ok(result.Value) : EvidenceProblem(result.ErrorCode!);
        }).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(32 * 1024)).RequireAuthorization(AgentstrationPolicies.CanExecuteRuns);
    }
    private static IResult EvidenceProblem(string code) => Results.Problem(title: code, statusCode: code.EndsWith("not_found", StringComparison.Ordinal)
        ? 404 : code.EndsWith("stale", StringComparison.Ordinal) || code.EndsWith("conflict", StringComparison.Ordinal) ? 409 : 400);
}
