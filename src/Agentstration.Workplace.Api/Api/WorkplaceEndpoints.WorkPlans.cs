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
    private static WorkPlanDefinition ToPlanDefinition(WorkPlanDefinitionRequest? request) => request is null ? null!
        : new(request.Intent, request.TaskIds?.Select(id => new WorkTaskId(id)).ToArray() ?? [],
            request.Dependencies?.Select(edge => edge is null ? null! : new WorkTaskDependency(new(edge.TaskId), new(edge.DependsOnTaskId))).ToArray() ?? []);

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

    private static WorkPlanRevisionResponse ToPlanRevisionResponse(WorkPlanRevision revision) => new(revision.PlanId.Value, revision.Number,
        revision.Definition.Intent, revision.Definition.TaskIds.Select(id => id.Value).ToArray(),
        revision.Definition.Dependencies.Select(edge => new WorkTaskDependencyRequest(edge.TaskId.Value, edge.DependsOnTaskId.Value)).ToArray(),
        revision.Tasks.Select(task => new WorkPlanTaskReferenceResponse(task.TaskId.Value, task.CreationDigest)).ToArray(), revision.Digest, revision.CreatedAt);

    private static WorkPlanResponse ToPlanResponse(WorkPlanView view) => new(view.Plan.Id.Value, view.Plan.WorkspaceId.Value, view.Plan.Status,
        view.Plan.Version, view.Plan.CurrentRevision, view.Plan.ValidatedRevision, view.Plan.ValidatedDigest, view.Plan.CreatedAt, view.Plan.UpdatedAt,
        ToPlanRevisionResponse(view.Revision), view.Tasks.Select(task => new WorkPlanTaskStateResponse(task.TaskId.Value, task.Version, task.Outcome,
            task.Readiness, task.Reasons, task.UnmetDependencies.Select(id => id.Value).ToArray())).ToArray())
        {
            Execution = view.Plan.Execution is { } execution ? new(execution.Revision, execution.RevisionDigest, execution.Enabled) { ProjectId = execution.ProjectId?.Value } : null,
            Attempts = view.Plan.Attempts.Select(attempt => new WorkPlanAttemptResponse(attempt.Id, attempt.TaskId.Value, attempt.PlanRevision,
                attempt.PlanDigest, attempt.TaskVersion, attempt.TaskDigest, attempt.WorkItemId.Value, attempt.FlowRunId, attempt.Flow,
                attempt.Dependencies.Select(item => new WorkPlanTaskObservationResponse(item.TaskId.Value, item.Version)).ToArray(),
                attempt.Status, attempt.CancellationRequested, attempt.AdmittedAt, attempt.UpdatedAt, attempt.Diagnostic, attempt.AdmissionEvidence)
            {
                ProjectContext = attempt.ProjectContext is { } context ? new(context.ProjectId.Value, context.ProjectVersion, context.Digest, context.Bindings, context.CapturedAt) : null
            }).ToArray()
        };
}
