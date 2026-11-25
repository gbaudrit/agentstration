using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;
using Microsoft.Extensions.Logging;

namespace Agentstration.Application.Work;

public sealed record RecordWorkTaskExecutionEvidence(Guid EventId, Guid AttemptId, long ExpectedPlanVersion, long ExpectedTaskVersion,
    long AdmittedTaskVersion, string TaskDigest, string Summary, IReadOnlyList<WorkTaskResultId> ResultIds, IReadOnlyList<WorkTaskArtifactId> ArtifactIds);

public sealed class WorkTaskExecutionEvidenceService(IWorkPlanRepository plans, IWorkItemRepository tasks,
    IWorkplaceRepository workplace, IWorkPlanFlowObserver flows, IWorkplaceContext context, TimeProvider clock, ILogger<WorkTaskExecutionEvidenceService> logger)
{
    public async Task<Result<WorkTaskExecutionPage>> ListAsync(WorkspaceId workspace, WorkTaskId task, int skip, int take, CancellationToken token)
    {
        if (!await OwnedAsync(workspace, task, token)) return Result<WorkTaskExecutionPage>.Failure("work_task_not_found");
        if (!Pagination(skip, take)) return Result<WorkTaskExecutionPage>.Failure("task_history_pagination_invalid");
        return Result<WorkTaskExecutionPage>.Success(await plans.ListTaskAttemptsAsync(workspace, context.PrincipalId, task, skip, take, token));
    }

    public async Task<Result<WorkTaskHistoryPage<WorkTaskResult>>> ResultsAsync(WorkspaceId workspace, WorkTaskId task, int skip, int take, CancellationToken token)
    {
        if (!await OwnedAsync(workspace, task, token)) return Result<WorkTaskHistoryPage<WorkTaskResult>>.Failure("work_task_not_found");
        if (!Pagination(skip, take)) return Result<WorkTaskHistoryPage<WorkTaskResult>>.Failure("task_history_pagination_invalid");
        return Result<WorkTaskHistoryPage<WorkTaskResult>>.Success(await workplace.ListResultPageAsync(workspace, task, skip, take, token));
    }

    public async Task<Result<WorkTaskHistoryPage<WorkTaskArtifact>>> ArtifactsAsync(WorkspaceId workspace, WorkTaskId task, int skip, int take, CancellationToken token)
    {
        if (!await OwnedAsync(workspace, task, token)) return Result<WorkTaskHistoryPage<WorkTaskArtifact>>.Failure("work_task_not_found");
        if (!Pagination(skip, take)) return Result<WorkTaskHistoryPage<WorkTaskArtifact>>.Failure("task_history_pagination_invalid");
        return Result<WorkTaskHistoryPage<WorkTaskArtifact>>.Success(await workplace.ListArtifactPageAsync(workspace, task, skip, take, token));
    }

    public async Task<Result<WorkTaskExecutionReceipt>> RecordAsync(WorkspaceId workspace, WorkPlanId planId, WorkTaskId taskId,
        RecordWorkTaskExecutionEvidence command, CancellationToken token)
    {
        if (!await OwnedAsync(workspace, taskId, token)) return Failure("work_task_not_found");
        if (command.EventId == Guid.Empty || command.AttemptId == Guid.Empty || command.ExpectedPlanVersion < 1 || command.ExpectedTaskVersion < 1
            || command.AdmittedTaskVersion < 1 || !WorkSharingFormat.Digest(command.TaskDigest) || string.IsNullOrWhiteSpace(command.Summary) || command.Summary.Length > 8_000
            || command.ResultIds is null || command.ArtifactIds is null || command.ResultIds.Count > 64 || command.ArtifactIds.Count > 64
            || command.ResultIds.Any(id => id.Value == Guid.Empty) || command.ArtifactIds.Any(id => id.Value == Guid.Empty)
            || command.ResultIds.Distinct().Count() != command.ResultIds.Count || command.ArtifactIds.Distinct().Count() != command.ArtifactIds.Count)
            return Failure("task_execution_evidence_invalid");
        var plan = await plans.GetAsync(workspace, context.PrincipalId, planId, token);
        var attempt = plan?.Attempts.SingleOrDefault(value => value.Id == command.AttemptId && value.TaskId == taskId);
        if (plan is null || attempt is null) return Failure("task_execution_attempt_not_found");
        var intent = WorkPlanDefinition.Digest(command with { Summary = command.Summary.Trim() });
        if (attempt.Evidence is { } evidence)
            return evidence.EventId == command.EventId && evidence.IntentDigest == intent
                ? Result<WorkTaskExecutionReceipt>.Success(new(workspace, context.PrincipalId, planId, attempt)) : Failure("task_execution_evidence_conflict");
        var task = (await tasks.GetAsync(workspace, context.PrincipalId, taskId.ToWorkItemId(), token))?.Value;
        if (task is null) return Failure("work_task_not_found");
        if (plan.Version != command.ExpectedPlanVersion || task.Version != command.ExpectedTaskVersion || task.CurrentExecutionId != new WorkExecutionId(attempt.Id)
            || attempt.TaskVersion != command.AdmittedTaskVersion || attempt.TaskDigest != command.TaskDigest
            || attempt.Status is WorkPlanAttemptStatus.Admitted or WorkPlanAttemptStatus.Submitted)
            return Failure("task_execution_evidence_stale");
        foreach (var id in command.ResultIds)
        {
            var result = await workplace.GetResultAsync(workspace, taskId, id, token);
            if (result is null || !await MatchesRunAsync(plan, attempt, result.FlowRunId, token)) return Failure("task_execution_evidence_reference_invalid");
        }
        foreach (var id in command.ArtifactIds)
        {
            var artifact = await workplace.GetArtifactAsync(workspace, taskId, id, token);
            if (artifact is null || !await MatchesRunAsync(plan, attempt, artifact.FlowRunId, token)) return Failure("task_execution_evidence_reference_invalid");
        }
        var now = clock.GetUtcNow();
        attempt = attempt with { Evidence = new(command.EventId, task.Version, intent, command.Summary.Trim(), command.ResultIds.ToArray(), command.ArtifactIds.ToArray(), now) };
        var receipt = new WorkPlanCommandReceipt(command.EventId, plan.Version + 1, WorkPlanAction.RecordExecutionEvidence, intent,
            attempt.PlanRevision, attempt.PlanDigest, now, [new(taskId, task.Version)]);
        var next = plan with { Version = plan.Version + 1, UpdatedAt = now, Attempts = plan.Attempts.Select(value => value.Id == attempt.Id ? attempt : value).ToArray() };
        var saved = await plans.TrySaveAsync(new(next, plan.Version, null, receipt, false), token);
        if (saved is not (WorkPlanSaveStatus.Saved or WorkPlanSaveStatus.Replayed)) return Failure("task_execution_evidence_conflict");
        if (logger.IsEnabled(LogLevel.Information)) logger.LogInformation("Recorded execution evidence {EvidenceId} for Task {TaskId}, Plan {PlanId}, attempt {AttemptId}, Flow Run {FlowRunId} in Workspace {WorkspaceId}",
            command.EventId, taskId.Value, planId.Value, attempt.Id, attempt.FlowRunId, workspace.Value);
        return Result<WorkTaskExecutionReceipt>.Success(new(workspace, context.PrincipalId, planId, attempt));
    }

    private async Task<bool> OwnedAsync(WorkspaceId workspace, WorkTaskId task, CancellationToken token) => workspace == context.WorkspaceId
        && context.PrincipalId != Guid.Empty && (await tasks.GetAsync(workspace, context.PrincipalId, task.ToWorkItemId(), token))?.Value.TaskHierarchy?.Kind == WorkTaskKind.Atomic;
    private async Task<bool> MatchesRunAsync(WorkPlanSnapshot plan, WorkPlanAttempt attempt, string? runId, CancellationToken token)
    {
        if (runId == attempt.FlowRunId) return true;
        if (runId is not { Length: > 0 and <= 200 } || plan.Execution?.Scope is not { } scope) return false;
        var run = await flows.GetAsync(scope, runId, token);
        return run is not null && run.Scope == scope && run.RootFlowRunId == attempt.FlowRunId && run.WorkTaskId == attempt.TaskId.ToString();
    }
    private static bool Pagination(int skip, int take) => skip >= 0 && take is >= 1 and <= 100;
    private static Result<WorkTaskExecutionReceipt> Failure(string code) => Result<WorkTaskExecutionReceipt>.Failure(code);
}
