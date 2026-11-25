using Agentstration.Application.Work;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Agentstration.Web.Security;

namespace Agentstration.Web;

public static partial class WorkplaceEndpoints
{
    private static void MapWorkProjectSharing(RouteGroupBuilder workspaces)
    {
        var sharing = workspaces.MapGroup("/projects/{projectId:guid}/sharing").WithTags("Project sharing");
        sharing.MapPost("/pull", PullWorkSharingAsync).RequireAuthorization(AgentstrationPolicies.CanExecuteRuns);
        sharing.MapPost("/operations", PublishWorkSharingAsync).RequireAuthorization(AgentstrationPolicies.CanExecuteRuns)
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(32 * 1024));
        sharing.MapPost("/operations/{operationId:guid}/resume", ResumeWorkSharingAsync).RequireAuthorization(AgentstrationPolicies.CanExecuteRuns);
        sharing.MapGet("/operations/{operationId:guid}", GetWorkSharingOperationAsync).RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        sharing.MapGet("/operations", ListWorkSharingOperationsAsync).RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        sharing.MapPost("/plans/{planId:guid}/import", ImportWorkSharingPlanAsync).RequireAuthorization(AgentstrationPolicies.CanExecuteRuns);
    }
    private static async Task<IResult> PullWorkSharingAsync(string workspaceName, Guid projectId, PullWorkSharingRequest request,
        WorkProjectSharingService service, CancellationToken token)
    {
        var result = await service.PullAsync(WorkspaceId(workspaceName), new(projectId), request.Revision, token);
        if (!result.IsSuccess) return SharingProblem(result.ErrorCode!);
        var value = result.Value!;
        return Results.Ok(new WorkSharingStateResponse(projectId, value.ProjectVersion, value.Version, value.Revision, value.Space, value.Records, value.UpdatedAt));
    }
    private static async Task<IResult> PublishWorkSharingAsync(string workspaceName, Guid projectId, PublishWorkSharingRequest request,
        WorkProjectSharingService service, CancellationToken token) => SharingAccepted(workspaceName, projectId,
            await service.PublishAsync(WorkspaceId(workspaceName), new(projectId), new(new(request.Id), request.ExpectedProjectVersion, request.Changes), token));
    private static async Task<IResult> ResumeWorkSharingAsync(string workspaceName, Guid projectId, Guid operationId,
        WorkProjectSharingService service, CancellationToken token) => SharingAccepted(workspaceName, projectId,
            await service.ResumeAsync(WorkspaceId(workspaceName), new(projectId), new(operationId), token));
    private static async Task<IResult> GetWorkSharingOperationAsync(string workspaceName, Guid projectId, Guid operationId,
        WorkProjectSharingService service, CancellationToken token)
    {
        var result = await service.GetOperationAsync(WorkspaceId(workspaceName), new(projectId), new(operationId), token);
        return result.IsSuccess ? Results.Ok(SharingResponse(result.Value!)) : SharingProblem(result.ErrorCode!);
    }
    private static async Task<IResult> ListWorkSharingOperationsAsync(string workspaceName, Guid projectId, int? skip, int? take,
        WorkProjectSharingService service, CancellationToken token)
    {
        var result = await service.ListOperationsAsync(WorkspaceId(workspaceName), new(projectId), skip ?? 0, take ?? 25, token);
        return result.IsSuccess ? Results.Ok(new WorkSharingOperationPageResponse(result.Value!.Items.Select(SharingResponse).ToArray(), result.Value.HasMore, result.Value.Skip, result.Value.Take))
            : SharingProblem(result.ErrorCode!);
    }
    private static async Task<IResult> ImportWorkSharingPlanAsync(string workspaceName, Guid projectId, Guid planId, ImportWorkSharingPlanRequest request,
        WorkProjectSharingService service, CancellationToken token)
    {
        var result = await service.ImportPlanAsync(WorkspaceId(workspaceName), new(projectId), new(planId), request.ExpectedDigest, token);
        return result.IsSuccess ? Results.Ok(WorkPlanContractMapping.ToPlanResponse(result.Value!)) : SharingProblem(result.ErrorCode!);
    }
    private static WorkSharingOperationResponse SharingResponse(WorkSharingOperation value) => new(value.Id.Value, value.ProjectId.Value, value.ProjectVersion,
        value.Status, value.Version, value.IntentDigest, value.Attempts.Count, value.RemoteRevision, value.ErrorCode, value.CreatedAt, value.UpdatedAt);
    private static IResult SharingAccepted(string workspaceName, Guid projectId, Result<WorkSharingOperation> result)
    {
        if (!result.IsSuccess) return SharingProblem(result.ErrorCode!);
        var value = result.Value!;
        return value.Status is WorkSharingOperationStatus.Rejected or WorkSharingOperationStatus.Conflict
            ? Results.Problem(title: value.ErrorCode ?? "sharing_publication_rejected", statusCode: 409, extensions: new Dictionary<string, object?> { ["operationId"] = value.Id.Value })
            : Results.Accepted($"/api/workspaces/{Uri.EscapeDataString(workspaceName)}/projects/{projectId}/sharing/operations/{value.Id}", SharingResponse(value));
    }
    private static IResult SharingProblem(string code) => code.StartsWith("work_project_", StringComparison.Ordinal) ? WorkProjectProblem(code)
        : code.StartsWith("work_plan_", StringComparison.Ordinal) ? WorkPlanProblem(code)
        : code.StartsWith("work_organization_", StringComparison.Ordinal) ? OrganizationProblem(code)
        : Results.Problem(title: code, statusCode: code switch
        {
            "sharing_operation_not_found" => 404,
            "sharing_provider_denied" or "sharing_provider_governance_denied" => 403,
            "sharing_record_conflict" or "sharing_state_conflict" or "sharing_local_plan_conflict" or "sharing_operation_conflict"
                or "sharing_command_conflict" or "sharing_connection_changed" or "sharing_plan_project_conflict" or "sharing_local_work_changed" => 409,
            _ => 400
        });
}
