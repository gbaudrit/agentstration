using static Agentstration.Work.Contracts.WorkPlanContractMapping;
using Agentstration.Application.Work;
using Agentstration.Work;
using Agentstration.Work.Contracts;

namespace Agentstration.Web;

public static partial class WorkplaceEndpoints
{
    private static async Task<IResult> CreateWorkPlanAsync(string workspaceName, CreateWorkPlanRequest request,
        WorkPlanService service, CancellationToken token)
    {
        var result = await service.CreateAsync(WorkspaceId(workspaceName), new(request.Id),
            new(request.CommandId, 0, ToPlanDefinition(request.Definition)), token);
        return result.IsSuccess ? Results.Created(PlanUrl(workspaceName, request.Id), ToPlanResponse(result.Value!)) : WorkPlanProblem(result.ErrorCode!);
    }

    private static async Task<IResult> ReviseWorkPlanAsync(string workspaceName, Guid planId, ReviseWorkPlanRequest request,
        WorkPlanService service, CancellationToken token)
    {
        var result = await service.ReviseAsync(WorkspaceId(workspaceName), new(planId),
            new(request.CommandId, request.ExpectedVersion, ToPlanDefinition(request.Definition)), token);
        return result.IsSuccess ? Results.Created($"{PlanUrl(workspaceName, planId)}/revisions/{result.Value!.Revision.Number}",
            ToPlanResponse(result.Value)) : WorkPlanProblem(result.ErrorCode!);
    }

    private static async Task<IResult> GetWorkPlanAsync(string workspaceName, Guid planId, WorkPlanService service, CancellationToken token)
    {
        var result = await service.GetAsync(WorkspaceId(workspaceName), new(planId), token);
        return result.IsSuccess ? Results.Ok(ToPlanResponse(result.Value!)) : WorkPlanProblem(result.ErrorCode!);
    }

    private static async Task<IResult> GetWorkPlanRevisionAsync(string workspaceName, Guid planId, long revision,
        WorkPlanService service, CancellationToken token)
    {
        var result = await service.GetRevisionAsync(WorkspaceId(workspaceName), new(planId), revision, token);
        return result.IsSuccess ? Results.Ok(ToPlanRevisionResponse(result.Value!)) : WorkPlanProblem(result.ErrorCode!);
    }

    private static async Task<IResult> GetWorkPlanCommandAsync(string workspaceName, Guid planId, Guid commandId,
        WorkPlanService service, CancellationToken token)
    {
        var result = await service.GetReceiptAsync(WorkspaceId(workspaceName), new(planId), commandId, token);
        if (!result.IsSuccess) return WorkPlanProblem(result.ErrorCode!);
        var receipt = result.Value!;
        return Results.Ok(new WorkPlanCommandResponse(planId, receipt.CommandId, receipt.PlanVersion, receipt.Action, receipt.IntentDigest, receipt.Revision,
            receipt.RevisionDigest, receipt.AppliedAt, receipt.ObservedTasks.Select(task => new WorkPlanTaskObservationResponse(task.TaskId.Value, task.Version)).ToArray()));
    }

    private static Task<IResult> ValidateWorkPlanAsync(string workspaceName, Guid planId, TransitionWorkPlanRequest request,
        WorkPlanService service, CancellationToken token) => TransitionWorkPlanAsync(workspaceName, planId, request, WorkPlanAction.Validate, service, token);
    private static Task<IResult> ActivateWorkPlanAsync(string workspaceName, Guid planId, TransitionWorkPlanRequest request,
        WorkPlanService service, CancellationToken token) => TransitionWorkPlanAsync(workspaceName, planId, request, WorkPlanAction.Activate, service, token);
    private static Task<IResult> DeactivateWorkPlanAsync(string workspaceName, Guid planId, TransitionWorkPlanRequest request,
        WorkPlanService service, CancellationToken token) => TransitionWorkPlanAsync(workspaceName, planId, request, WorkPlanAction.Deactivate, service, token);

    private static async Task<IResult> TransitionWorkPlanAsync(string workspaceName, Guid planId, TransitionWorkPlanRequest request,
        WorkPlanAction action, WorkPlanService service, CancellationToken token)
    {
        var result = await service.TransitionAsync(WorkspaceId(workspaceName), new(planId),
            new(request.CommandId, request.ExpectedVersion, request.Revision, request.RevisionDigest, action), token);
        return result.IsSuccess ? Results.Ok(ToPlanResponse(result.Value!)) : WorkPlanProblem(result.ErrorCode!);
    }

    private static string PlanUrl(string workspaceName, Guid id) => $"/api/workspaces/{Uri.EscapeDataString(workspaceName)}/work-plans/{id}";
    private static IResult WorkPlanProblem(string code) => code.StartsWith("work_project_", StringComparison.Ordinal) ? WorkProjectProblem(code)
        : Results.Problem(title: code, statusCode: code switch
    {
        "work_plan_not_found" or "work_plan_revision_not_found" or "work_plan_command_not_found" or "work_plan_task_not_found" => StatusCodes.Status404NotFound,
        "work_plan_conflict" or "work_plan_command_conflict" or "work_plan_active" or "work_plan_tasks_active"
            or "work_plan_history_limit" or "work_plan_task_changed" or "work_plan_revision_conflict"
            or "work_plan_execution_transition_invalid" or "work_plan_retry_not_allowed" or "work_plan_execution_target_changed" or "work_plan_execution_run_mismatch" or "work_plan_execution_run_unavailable"
            or "work_plan_transition_invalid" or "work_plan_not_validated" or "work_plan_task_intent_changed" => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status400BadRequest
    });

}
