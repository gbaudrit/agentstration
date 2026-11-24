using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;
using Microsoft.Extensions.Logging;

namespace Agentstration.Application.Work;

public sealed record RecordWorkTaskOutcome(Guid EventId, long ExpectedVersion, WorkTaskOutcome Outcome, string Evidence);

public sealed partial class WorkTaskTreeService
{
    public async Task<Result<WorkTaskTree>> RecordOutcomeAsync(WorkspaceId workspaceId, WorkTaskId taskId,
        RecordWorkTaskOutcome command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (workspaceId.Value == Guid.Empty || workspaceId != context.WorkspaceId || context.PrincipalId == Guid.Empty)
            return Result<WorkTaskTree>.Failure("work_task_not_found");
        if (command.EventId == Guid.Empty || command.ExpectedVersion < 1 || !Enum.IsDefined(command.Outcome)
            || command.Outcome == WorkTaskOutcome.Pending || string.IsNullOrWhiteSpace(command.Evidence)
            || command.Evidence.Length > WorkTaskAcceptanceRules.MaximumEvidenceLength)
            return Result<WorkTaskTree>.Failure("task_outcome_invalid");
        var evidence = command.Evidence.Trim();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var anchor = await repository.GetAsync(workspaceId, context.PrincipalId, taskId.ToWorkItemId(), cancellationToken);
            if (anchor?.Value.TaskHierarchy is not { } hierarchy)
                return Result<WorkTaskTree>.Failure("work_task_not_found");
            if (hierarchy.Kind != WorkTaskKind.Atomic)
                return Result<WorkTaskTree>.Failure("task_outcome_derived");
            var stored = await repository.ListTaskTreeAsync(workspaceId, context.PrincipalId, hierarchy.TreeId, cancellationToken);
            if (stored.Count is < 1 or > WorkTaskTreeDefinition.MaximumTasks)
                return Result<WorkTaskTree>.Failure("task_tree_size_invalid");
            var items = stored.ToDictionary(item => WorkTaskId.FromWorkItem(item.Value.Id));
            if (!items.TryGetValue(taskId, out var target) || !items.ContainsKey(hierarchy.TreeId))
                return Result<WorkTaskTree>.Failure("work_task_not_found");
            if (target.Value.TaskAcceptance?.Evidence is { } prior)
                return prior.EventId == command.EventId && prior.Outcome == command.Outcome && prior.Evidence == evidence
                    ? await GetAsync(workspaceId, hierarchy.TreeId, cancellationToken)
                    : Result<WorkTaskTree>.Failure("task_outcome_conflict");
            if (items.Values.Any(item => item.Value.TaskAcceptance?.Evidence?.EventId == command.EventId))
                return Result<WorkTaskTree>.Failure("task_outcome_conflict");
            if (target.Value.Version != command.ExpectedVersion)
                return Result<WorkTaskTree>.Failure("task_tree_conflict");

            var now = timeProvider.GetUtcNow();
            var updates = new List<WorkTaskTreeUpdate> { new(target.Value, target.Value.Version) };
            target.Value.RecordTaskOutcome(new(command.EventId, command.Outcome, evidence, now));
            var current = target.Value;
            while (current.TaskHierarchy?.ParentTaskId is { } parentId)
            {
                if (updates.Count > WorkTaskTreeDefinition.MaximumDepth || !items.TryGetValue(parentId, out var parent))
                    return Result<WorkTaskTree>.Failure("task_tree_depth_exceeded");
                updates.Add(new(parent.Value, parent.Value.Version));
                parent.Value.ConsolidateTaskOutcome(items.Values.Select(item => item.Value)
                    .Where(item => item.TaskHierarchy?.ParentTaskId == parentId).ToArray(), now);
                current = parent.Value;
            }
            try
            {
                await repository.SaveTaskTreeOutcomeAsync(workspaceId, context.PrincipalId, hierarchy.TreeId, updates, cancellationToken);
                if (logger.IsEnabled(LogLevel.Information))
                    logger.LogInformation("Recorded functional outcome {Outcome} for task {TaskId} in Workspace {WorkspaceId}; event {EventId}",
                        command.Outcome, taskId.Value, workspaceId.Value, command.EventId);
                return await GetAsync(workspaceId, hierarchy.TreeId, cancellationToken);
            }
            catch (WorkItemConcurrencyException) when (attempt < 2) { }
            catch (WorkItemConcurrencyException) { return Result<WorkTaskTree>.Failure("task_tree_conflict"); }
        }
        return Result<WorkTaskTree>.Failure("task_tree_conflict");
    }
}
