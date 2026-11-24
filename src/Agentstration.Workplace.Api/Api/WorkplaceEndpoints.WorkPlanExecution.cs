using static Agentstration.Work.Contracts.WorkPlanContractMapping;
using Agentstration.Application.Work;
using Agentstration.Work;
using Agentstration.Work.Contracts;

namespace Agentstration.Web;

public static partial class WorkplaceEndpoints
{
    private static async Task<IResult> StartWorkPlanExecutionAsync(string workspaceName, Guid planId, StartWorkPlanExecutionRequest request,
        WorkPlanExecutionService service, CancellationToken token)
    {
        var result = await service.StartAsync(WorkspaceId(workspaceName), new(planId), new(request.CommandId, request.ExpectedVersion,
            request.Revision, request.RevisionDigest, request.Targets?.Select(target => target is null ? null!
                : new WorkPlanTaskTarget(new(target.TaskId), target.Flow, target.Input)).ToArray()!)
            { ProjectId = request.ProjectId is { } id ? new WorkProjectId(id) : null }, token);
        return result.IsSuccess ? Results.Accepted(PlanUrl(workspaceName, planId), ToPlanResponse(result.Value!)) : WorkPlanProblem(result.ErrorCode!);
    }

    private static async Task<IResult> AdvanceWorkPlanExecutionAsync(string workspaceName, Guid planId,
        WorkPlanExecutionService service, CancellationToken token)
    {
        var result = await service.AdvanceAsync(WorkspaceId(workspaceName), new(planId), token);
        return result.IsSuccess ? Results.Accepted(PlanUrl(workspaceName, planId), ToPlanResponse(result.Value!)) : WorkPlanProblem(result.ErrorCode!);
    }

    private static Task<IResult> CancelWorkPlanExecutionAsync(string workspaceName, Guid planId, ControlWorkPlanExecutionRequest request,
        WorkPlanExecutionService service, CancellationToken token) => ControlWorkPlanExecutionAsync(workspaceName, planId, request, service, false, token);
    private static Task<IResult> RetryWorkPlanTaskAsync(string workspaceName, Guid planId, ControlWorkPlanExecutionRequest request,
        WorkPlanExecutionService service, CancellationToken token) => ControlWorkPlanExecutionAsync(workspaceName, planId, request, service, true, token);

    private static async Task<IResult> ControlWorkPlanExecutionAsync(string workspaceName, Guid planId, ControlWorkPlanExecutionRequest request,
        WorkPlanExecutionService service, bool retry, CancellationToken token)
    {
        var command = new ControlWorkPlanExecution(request.CommandId, request.ExpectedVersion, request.TaskId is { } taskId ? new(taskId) : null);
        var result = retry ? await service.RetryAsync(WorkspaceId(workspaceName), new(planId), command, token)
            : await service.CancelAsync(WorkspaceId(workspaceName), new(planId), command, token);
        return result.IsSuccess ? Results.Accepted(PlanUrl(workspaceName, planId), ToPlanResponse(result.Value!)) : WorkPlanProblem(result.ErrorCode!);
    }
}
