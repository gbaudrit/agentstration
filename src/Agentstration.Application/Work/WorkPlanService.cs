using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;
using Microsoft.Extensions.Logging;

namespace Agentstration.Application.Work;

public sealed record ReviseWorkPlanCommand(Guid CommandId, long ExpectedVersion, WorkPlanDefinition Definition);
public sealed record TransitionWorkPlanCommand(Guid CommandId, long ExpectedVersion, long Revision, string RevisionDigest,
    WorkPlanAction Action);

public sealed partial class WorkPlanService(IWorkPlanRepository plans, IWorkItemRepository tasks, IWorkplaceContext context,
    TimeProvider timeProvider, ILogger<WorkPlanService> logger)
{
    public Task InitializeAsync(CancellationToken cancellationToken) => plans.InitializeAsync(cancellationToken);

    public Task<Result<WorkPlanView>> CreateAsync(WorkspaceId workspaceId, WorkPlanId id, ReviseWorkPlanCommand command,
        CancellationToken cancellationToken) => SaveRevisionAsync(workspaceId, id, command, true, cancellationToken);

    public Task<Result<WorkPlanView>> ReviseAsync(WorkspaceId workspaceId, WorkPlanId id, ReviseWorkPlanCommand command,
        CancellationToken cancellationToken) => SaveRevisionAsync(workspaceId, id, command, false, cancellationToken);

    private async Task<Result<WorkPlanView>> SaveRevisionAsync(WorkspaceId workspaceId, WorkPlanId id, ReviseWorkPlanCommand command,
        bool create, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!OwnsScope(workspaceId, id)) return Result<WorkPlanView>.Failure("work_plan_not_found");
        if (command.CommandId == Guid.Empty || (create ? command.ExpectedVersion != 0 : command.ExpectedVersion < 1)
            || command.Definition is null) return Result<WorkPlanView>.Failure("work_plan_command_invalid");
        var validated = command.Definition.Validate();
        if (!validated.IsSuccess) return Result<WorkPlanView>.Failure(validated.ErrorCode!);
        var definition = validated.Value!;
        var action = create ? WorkPlanAction.Create : WorkPlanAction.Revise;
        var intent = WorkPlanDefinition.Digest(new { Action = action, Definition = definition });
        var replay = await ReplayAsync(workspaceId, id, command.CommandId, intent, cancellationToken);
        if (replay is not null) return replay;
        var current = await plans.GetAsync(workspaceId, context.PrincipalId, id, cancellationToken);
        if (!create && current is null) return Result<WorkPlanView>.Failure("work_plan_not_found");
        if (create ? current is not null : current!.Version != command.ExpectedVersion)
            return Result<WorkPlanView>.Failure("work_plan_conflict");
        if (current?.Status == WorkPlanStatus.Active) return Result<WorkPlanView>.Failure("work_plan_active");
        if (current?.Version >= WorkPlanDefinition.MaximumCommands || current?.CurrentRevision >= WorkPlanDefinition.MaximumRevisions)
            return Result<WorkPlanView>.Failure("work_plan_history_limit");
        var loaded = await LoadTasksAsync(workspaceId, definition, cancellationToken);
        if (!loaded.IsSuccess) return Result<WorkPlanView>.Failure(loaded.ErrorCode!);
        var members = loaded.Value!;
        var graphError = await ValidateHierarchyAsync(workspaceId, definition, members, cancellationToken);
        if (graphError is not null) return Result<WorkPlanView>.Failure(graphError);
        var observed = members.Values.ToDictionary(item => WorkTaskId.FromWorkItem(item.Value.Id));
        if (current is not null)
        {
            var previous = await RequiredRevisionAsync(workspaceId, id, current.CurrentRevision, cancellationToken);
            foreach (var removed in previous.Definition.TaskIds.Except(definition.TaskIds))
            {
                var item = await tasks.GetAsync(workspaceId, context.PrincipalId, removed.ToWorkItemId(), cancellationToken);
                if (item is not null) observed.Add(removed, item);
            }
            if (observed.Values.Any(item => IsActive(item.Value.Status))) return Result<WorkPlanView>.Failure("work_plan_tasks_active");
        }
        var now = timeProvider.GetUtcNow();
        var references = definition.TaskIds.Select(taskId => new WorkPlanTaskReference(taskId, members[taskId].Value.TaskHierarchy!.CreationDigest)).ToArray();
        var revision = new WorkPlanRevision(id, (current?.CurrentRevision ?? 0) + 1, definition, references,
            definition.ContentDigest(references), now);
        var plan = new WorkPlanSnapshot(id, workspaceId, context.PrincipalId, WorkPlanStatus.Draft,
            command.ExpectedVersion + 1, revision.Number, null, null, current?.CreatedAt ?? now, now);
        var receipt = new WorkPlanCommandReceipt(command.CommandId, plan.Version, action, intent, revision.Number, revision.Digest, now,
            Observe(observed.Values));
        return await SaveAsync(new(plan, command.ExpectedVersion, revision, receipt, !create), cancellationToken);
    }

    public async Task<Result<WorkPlanView>> TransitionAsync(WorkspaceId workspaceId, WorkPlanId id,
        TransitionWorkPlanCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!OwnsScope(workspaceId, id)) return Result<WorkPlanView>.Failure("work_plan_not_found");
        if (command.CommandId == Guid.Empty || command.ExpectedVersion < 1 || command.Revision < 1
            || command.RevisionDigest is null || command.RevisionDigest.Length != 64
            || command.Action is not (WorkPlanAction.Validate or WorkPlanAction.Activate or WorkPlanAction.Deactivate))
            return Result<WorkPlanView>.Failure("work_plan_command_invalid");
        var intent = WorkPlanDefinition.Digest(new { command.Action, command.Revision, command.RevisionDigest });
        var replay = await ReplayAsync(workspaceId, id, command.CommandId, intent, cancellationToken);
        if (replay is not null) return replay;
        var plan = await plans.GetAsync(workspaceId, context.PrincipalId, id, cancellationToken);
        if (plan is null) return Result<WorkPlanView>.Failure("work_plan_not_found");
        if (plan.Version != command.ExpectedVersion) return Result<WorkPlanView>.Failure("work_plan_conflict");
        if (plan.Version >= WorkPlanDefinition.MaximumCommands) return Result<WorkPlanView>.Failure("work_plan_history_limit");
        var revision = await RequiredRevisionAsync(workspaceId, id, plan.CurrentRevision, cancellationToken);
        if (command.Revision != revision.Number || command.RevisionDigest != revision.Digest)
            return Result<WorkPlanView>.Failure("work_plan_revision_conflict");
        if (command.Action == WorkPlanAction.Validate && plan.Status == WorkPlanStatus.Active
            || command.Action == WorkPlanAction.Deactivate && plan.Status != WorkPlanStatus.Active)
            return Result<WorkPlanView>.Failure("work_plan_transition_invalid");
        if (command.Action == WorkPlanAction.Activate && (plan.Status is not (WorkPlanStatus.Validated or WorkPlanStatus.Inactive)
            || plan.ValidatedRevision != revision.Number || plan.ValidatedDigest != revision.Digest))
            return Result<WorkPlanView>.Failure("work_plan_not_validated");
        var loaded = await LoadTasksAsync(workspaceId, revision.Definition, cancellationToken);
        if (!loaded.IsSuccess) return Result<WorkPlanView>.Failure(loaded.ErrorCode!);
        var members = loaded.Value!;
        if (revision.Tasks.Any(reference => members[reference.TaskId].Value.TaskHierarchy!.CreationDigest != reference.CreationDigest))
            return Result<WorkPlanView>.Failure("work_plan_task_intent_changed");
        var graphError = await ValidateHierarchyAsync(workspaceId, revision.Definition, members, cancellationToken);
        if (graphError is not null) return Result<WorkPlanView>.Failure(graphError);
        var requireInactive = command.Action != WorkPlanAction.Validate;
        if (requireInactive && members.Values.Any(item => IsActive(item.Value.Status)))
            return Result<WorkPlanView>.Failure("work_plan_tasks_active");
        var now = timeProvider.GetUtcNow();
        var changed = plan with
        {
            Version = plan.Version + 1,
            UpdatedAt = now,
            Status = command.Action switch
            {
                WorkPlanAction.Validate => WorkPlanStatus.Validated,
                WorkPlanAction.Activate => WorkPlanStatus.Active,
                _ => WorkPlanStatus.Inactive
            },
            ValidatedRevision = command.Action == WorkPlanAction.Validate ? revision.Number : plan.ValidatedRevision,
            ValidatedDigest = command.Action == WorkPlanAction.Validate ? revision.Digest : plan.ValidatedDigest
        };
        var receipt = new WorkPlanCommandReceipt(command.CommandId, changed.Version, command.Action, intent, revision.Number, revision.Digest, now, Observe(members.Values));
        return await SaveAsync(new(changed, plan.Version, null, receipt, requireInactive), cancellationToken);
    }

    public async Task<Result<WorkPlanView>> GetAsync(WorkspaceId workspaceId, WorkPlanId id, CancellationToken cancellationToken)
    {
        if (!OwnsScope(workspaceId, id)) return Result<WorkPlanView>.Failure("work_plan_not_found");
        var plan = await plans.GetAsync(workspaceId, context.PrincipalId, id, cancellationToken);
        if (plan is null) return Result<WorkPlanView>.Failure("work_plan_not_found");
        var revision = await RequiredRevisionAsync(workspaceId, id, plan.CurrentRevision, cancellationToken);
        var state = new List<WorkPlanTaskState>();
        var members = new Dictionary<WorkTaskId, StoredWorkItem>();
        foreach (var taskId in revision.Definition.TaskIds)
        {
            var task = await tasks.GetAsync(workspaceId, context.PrincipalId, taskId.ToWorkItemId(), cancellationToken);
            if (task is not null && task.Value.TaskHierarchy is not null) members.Add(taskId, task);
        }
        foreach (var taskId in revision.Definition.TaskIds)
            state.Add(Readiness(plan, revision, taskId, members));
        return Result<WorkPlanView>.Success(new(plan, revision, state));
    }

    public async Task<Result<WorkPlanRevision>> GetRevisionAsync(WorkspaceId workspaceId, WorkPlanId id, long revision,
        CancellationToken cancellationToken)
    {
        if (!OwnsScope(workspaceId, id)) return Result<WorkPlanRevision>.Failure("work_plan_not_found");
        var stored = await plans.GetRevisionAsync(workspaceId, context.PrincipalId, id, revision, cancellationToken);
        return stored is null ? Result<WorkPlanRevision>.Failure("work_plan_revision_not_found") : Result<WorkPlanRevision>.Success(stored);
    }

    private bool OwnsScope(WorkspaceId workspaceId, WorkPlanId id) => workspaceId.Value != Guid.Empty && workspaceId == context.WorkspaceId
        && context.PrincipalId != Guid.Empty && id.Value != Guid.Empty;

    public async Task<Result<WorkPlanCommandReceipt>> GetReceiptAsync(WorkspaceId workspaceId, WorkPlanId id, Guid commandId,
        CancellationToken cancellationToken)
    {
        if (!OwnsScope(workspaceId, id)) return Result<WorkPlanCommandReceipt>.Failure("work_plan_not_found");
        var receipt = await plans.GetReceiptAsync(workspaceId, context.PrincipalId, id, commandId, cancellationToken);
        return receipt is null ? Result<WorkPlanCommandReceipt>.Failure("work_plan_command_not_found") : Result<WorkPlanCommandReceipt>.Success(receipt);
    }

    private async Task<WorkPlanRevision> RequiredRevisionAsync(WorkspaceId workspaceId, WorkPlanId id, long revision, CancellationToken token) =>
        await plans.GetRevisionAsync(workspaceId, context.PrincipalId, id, revision, token)
        ?? throw new InvalidOperationException("The persisted WorkPlan revision is missing.");

    private async Task<Result<WorkPlanView>?> ReplayAsync(WorkspaceId workspaceId, WorkPlanId id, Guid commandId, string intent, CancellationToken token)
    {
        var receipt = await plans.GetReceiptAsync(workspaceId, context.PrincipalId, id, commandId, token);
        if (receipt is null) return null;
        return receipt.IntentDigest == intent ? await GetAsync(workspaceId, id, token)
            : Result<WorkPlanView>.Failure("work_plan_command_conflict");
    }

    private async Task<Result<WorkPlanView>> SaveAsync(WorkPlanChange change, CancellationToken token)
    {
        var result = await plans.TrySaveAsync(change, token);
        if (result == WorkPlanSaveStatus.Conflict)
            return await ReplayAsync(change.Plan.WorkspaceId, change.Plan.Id, change.Receipt.CommandId, change.Receipt.IntentDigest, token)
                ?? Result<WorkPlanView>.Failure("work_plan_conflict");
        if (result is WorkPlanSaveStatus.TasksActive or WorkPlanSaveStatus.TaskChanged)
            return Result<WorkPlanView>.Failure(result == WorkPlanSaveStatus.TasksActive ? "work_plan_tasks_active" : "work_plan_task_changed");
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Applied WorkPlan action {Action} to plan {PlanId}, revision {Revision}, in Workspace {WorkspaceId}; command {CommandId}",
                change.Receipt.Action, change.Plan.Id.Value, change.Plan.CurrentRevision, change.Plan.WorkspaceId.Value, change.Receipt.CommandId);
        return await GetAsync(change.Plan.WorkspaceId, change.Plan.Id, token);
    }

    private static IReadOnlyList<WorkPlanTaskObservation> Observe(IEnumerable<StoredWorkItem> items) => items
        .OrderBy(item => item.Value.Id.Value).Select(item => new WorkPlanTaskObservation(WorkTaskId.FromWorkItem(item.Value.Id), item.Value.Version)).ToArray();
    private static bool IsActive(WorkItemStatus status) => status is WorkItemStatus.Queued or WorkItemStatus.Running
        or WorkItemStatus.WaitingForInput or WorkItemStatus.WaitingForApproval or WorkItemStatus.Paused;
}
