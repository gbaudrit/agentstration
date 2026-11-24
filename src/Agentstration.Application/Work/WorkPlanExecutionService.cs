using Agentstration.Flows;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;
using Microsoft.Extensions.Logging;

namespace Agentstration.Application.Work;

public sealed record StartWorkPlanExecution(Guid CommandId, long ExpectedVersion, long Revision, string RevisionDigest,
    IReadOnlyList<WorkPlanTaskTarget> Targets);
public sealed record ControlWorkPlanExecution(Guid CommandId, long ExpectedVersion, WorkTaskId? TaskId = null);

public interface IWorkPlanFlowObserver
{
    Task<FlowRun?> GetAsync(FlowRunScope scope, string runId, CancellationToken cancellationToken);
    Task CancelAsync(FlowRunScope scope, string runId, CancellationToken cancellationToken);
}

public interface IWorkPlanAdmissionGate
{
    Task<Result<IReadOnlyList<WorkPlanAdmissionEvidence>>> ObserveAsync(FlowRunScope scope, WorkPlanSnapshot plan,
        WorkPlanRevision revision, WorkTaskId taskId, CancellationToken cancellationToken);
}

public sealed partial class WorkPlanExecutionService(IWorkPlanRepository plans, IWorkItemRepository tasks,
    WorkPlanService planService, WorkItemService workItems, RootFlowSubmissionService submissions, IRootFlowTargetResolver targets,
    IRootFlowSubmissionAuthorizer authorizer, IWorkPlanFlowObserver flows, IWorkplaceContext context,
    IEnumerable<IWorkExecutionScopeAccessor> scopes, IEnumerable<IWorkPlanAdmissionGate> gates,
    TimeProvider clock, ILogger<WorkPlanExecutionService> logger)
{
    private const int MaximumAttempts = 200;

    public async Task<Result<WorkPlanView>> StartAsync(WorkspaceId workspaceId, WorkPlanId id,
        StartWorkPlanExecution command, CancellationToken token)
    {
        var loaded = await planService.GetAsync(workspaceId, id, token);
        if (!loaded.IsSuccess) return loaded;
        var view = loaded.Value!;
        if (command.CommandId == Guid.Empty || command.ExpectedVersion < 1 || command.Targets is null
            || command.Targets.Count is < 1 or > WorkPlanDefinition.MaximumTasks
            || command.Targets.Any(target => target is null || target.Flow is null || string.IsNullOrWhiteSpace(target.Flow.FlowId.Value)
                || target.Input.ValueKind != System.Text.Json.JsonValueKind.Object || target.Input.GetRawText().Length > 64 * 1024)
            || command.Targets.Select(target => target.TaskId).Distinct().Count() != command.Targets.Count
            || command.Targets.Sum(target => target.Input.GetRawText().Length) > 256 * 1024)
            return Result<WorkPlanView>.Failure("work_plan_execution_invalid");
        var intent = WorkPlanDefinition.Digest(new { command.Revision, command.RevisionDigest,
            Targets = command.Targets.OrderBy(target => target.TaskId.Value).ToArray() });
        var replay = await ReplayAsync(view.Plan, command.CommandId, intent, token);
        if (replay is not null) return replay;
        if (view.Plan.Version != command.ExpectedVersion) return Failure("work_plan_conflict");
        if (view.Plan.Status != WorkPlanStatus.Active || view.Plan.Execution is not null) return Failure("work_plan_execution_transition_invalid");
        if (view.Revision.Number != command.Revision || view.Revision.Digest != command.RevisionDigest)
            return Failure("work_plan_revision_conflict");
        var members = await MembersAsync(view, token);
        var atomic = members.Where(item => item.Value.TaskHierarchy?.Kind == WorkTaskKind.Atomic).Select(item => WorkTaskId.FromWorkItem(item.Value.Id));
        if (!atomic.OrderBy(task => task.Value).SequenceEqual(command.Targets.Select(target => target.TaskId).OrderBy(task => task.Value)))
            return Failure("work_plan_execution_targets_invalid");
        var scope = Scope(workspaceId);
        await authorizer.AuthorizeAsync(scope, token);
        var resolved = new List<WorkPlanTaskTarget>();
        foreach (var target in command.Targets.OrderBy(target => target.TaskId.Value))
        {
            var exact = await targets.ResolveAsync(scope, target.Flow, target.Input, token);
            resolved.Add(target with { Flow = exact.Reference, Input = target.Input.Clone() });
        }
        var plan = view.Plan with { Execution = new(scope, command.Revision, command.RevisionDigest, true, resolved) };
        return await SaveAsync(view, plan, command.CommandId, WorkPlanAction.StartExecution, intent, members, [], token);
    }

    public async Task<Result<WorkPlanView>> CancelAsync(WorkspaceId workspaceId, WorkPlanId id,
        ControlWorkPlanExecution command, CancellationToken token)
    {
        var loaded = await planService.GetAsync(workspaceId, id, token);
        if (!loaded.IsSuccess) return loaded;
        var view = loaded.Value!;
        var intent = WorkPlanDefinition.Digest(new { Action = WorkPlanAction.CancelExecution });
        var replay = await ReplayAsync(view.Plan, command.CommandId, intent, token);
        if (replay is not null) return replay;
        if (command.CommandId == Guid.Empty || command.TaskId is not null) return Failure("work_plan_execution_invalid");
        if (view.Plan.Version != command.ExpectedVersion) return Failure("work_plan_conflict");
        if (view.Plan.Execution is null) return Failure("work_plan_execution_transition_invalid");
        var plan = view.Plan with { Execution = view.Plan.Execution with { Enabled = false },
            Attempts = view.Plan.Attempts.Select(attempt => IsOpen(attempt) ? attempt with { CancellationRequested = true } : attempt).ToArray() };
        return await SaveAsync(view, plan, command.CommandId, WorkPlanAction.CancelExecution, intent, await MembersAsync(view, token), [], token);
    }

    public async Task<Result<WorkPlanView>> RetryAsync(WorkspaceId workspaceId, WorkPlanId id,
        ControlWorkPlanExecution command, CancellationToken token)
    {
        var loaded = await planService.GetAsync(workspaceId, id, token);
        if (!loaded.IsSuccess) return loaded;
        var view = loaded.Value!;
        var intent = WorkPlanDefinition.Digest(new { Action = WorkPlanAction.RetryTask, command.TaskId });
        var replay = await ReplayAsync(view.Plan, command.CommandId, intent, token);
        if (replay is not null) return replay;
        if (command.CommandId == Guid.Empty || command.TaskId is null) return Failure("work_plan_execution_invalid");
        if (view.Plan.Version != command.ExpectedVersion) return Failure("work_plan_conflict");
        if (view.Plan.Execution is not { } execution || view.Plan.Status != WorkPlanStatus.Active
            || view.Plan.Attempts.Any(attempt => attempt.TaskId == command.TaskId && IsOpen(attempt)))
            return Failure("work_plan_retry_not_allowed");
        var members = await MembersAsync(view, token);
        var task = members.SingleOrDefault(item => WorkTaskId.FromWorkItem(item.Value.Id) == command.TaskId)?.Value;
        if (task is null) return Failure("work_plan_task_not_found");
        var observations = Observe(members);
        var version = task.Version;
        try { task.RetryPlanTask(command.CommandId, clock.GetUtcNow()); }
        catch (WorkTransitionException exception) { return Failure(exception.Code); }
        return await SaveObservedAsync(view, view.Plan with { Execution = execution with { Enabled = true } }, command.CommandId,
            WorkPlanAction.RetryTask, intent, observations, [new(task, version)], token);
    }

    private FlowRunScope Scope(WorkspaceId workspaceId)
    {
        var scope = WorkExecutionScopeSelection.Select(scopes);
        if (scope is null || scope.WorkspaceId != workspaceId || scope.PrincipalId != context.PrincipalId)
            throw new WorkValidationException("work_execution_scope_mismatch", "WorkPlan execution requires the authenticated Workspace and Principal.");
        return scope;
    }

    private async Task<IReadOnlyList<StoredWorkItem>> MembersAsync(WorkPlanView view, CancellationToken token)
    {
        var result = new List<StoredWorkItem>();
        foreach (var id in view.Revision.Definition.TaskIds)
        {
            var item = await tasks.GetAsync(view.Plan.WorkspaceId, context.PrincipalId, id.ToWorkItemId(), token);
            if (item is null) throw new WorkValidationException("work_plan_task_not_found", "A plan task is unavailable.");
            result.Add(item);
        }
        return result;
    }

    private Task<Result<WorkPlanView>> SaveAsync(WorkPlanView before, WorkPlanSnapshot plan, Guid commandId, WorkPlanAction action,
        string intent, IReadOnlyList<StoredWorkItem> members, IReadOnlyList<WorkTaskTreeUpdate> updates, CancellationToken token) =>
        SaveObservedAsync(before, plan, commandId, action, intent, Observe(members), updates, token);

    private async Task<Result<WorkPlanView>> SaveObservedAsync(WorkPlanView before, WorkPlanSnapshot plan, Guid commandId, WorkPlanAction action,
        string intent, IReadOnlyList<WorkPlanTaskObservation> observations, IReadOnlyList<WorkTaskTreeUpdate> updates, CancellationToken token)
    {
        // Always reserve progress/cancellation for already admitted work, including at the authoring history limit.
        if (before.Plan.Version >= WorkPlanDefinition.MaximumCommands && action is not (WorkPlanAction.AdvanceExecution or WorkPlanAction.CancelExecution)
            || plan.Attempts.Count > MaximumAttempts)
            return Failure("work_plan_history_limit");
        var now = clock.GetUtcNow();
        plan = plan with { Version = before.Plan.Version + 1, UpdatedAt = now };
        var receipt = new WorkPlanCommandReceipt(commandId, plan.Version, action, intent, before.Revision.Number, before.Revision.Digest, now, observations);
        var saved = await plans.TrySaveAsync(new(plan, before.Plan.Version, null, receipt, false) { TaskUpdates = updates }, token);
        if (saved is not (WorkPlanSaveStatus.Saved or WorkPlanSaveStatus.Replayed))
            return await ReplayAsync(plan, commandId, intent, token) ?? Failure("work_plan_conflict");
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Applied WorkPlan execution action {Action} to {PlanId} in Workspace {WorkspaceId}; command {CommandId}",
                action, plan.Id.Value, plan.WorkspaceId.Value, commandId);
        return await planService.GetAsync(plan.WorkspaceId, plan.Id, token);
    }

    private async Task<Result<WorkPlanView>?> ReplayAsync(WorkPlanSnapshot plan, Guid id, string intent, CancellationToken token)
    {
        var receipt = await plans.GetReceiptAsync(plan.WorkspaceId, context.PrincipalId, plan.Id, id, token);
        return receipt is null ? null : receipt.IntentDigest != intent ? Failure("work_plan_command_conflict")
            : await planService.GetAsync(plan.WorkspaceId, plan.Id, token);
    }

    private static IReadOnlyList<WorkPlanTaskObservation> Observe(IEnumerable<StoredWorkItem> members) => members
        .Select(item => new WorkPlanTaskObservation(WorkTaskId.FromWorkItem(item.Value.Id), item.Value.Version)).OrderBy(item => item.TaskId.Value).ToArray();
    private static bool IsOpen(WorkPlanAttempt attempt) => attempt.Status is WorkPlanAttemptStatus.Admitted or WorkPlanAttemptStatus.Submitted;
    private static Result<WorkPlanView> Failure(string code) => Result<WorkPlanView>.Failure(code);
}
