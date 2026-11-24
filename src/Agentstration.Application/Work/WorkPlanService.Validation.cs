using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;

namespace Agentstration.Application.Work;

public sealed partial class WorkPlanService
{
    private async Task<Result<Dictionary<WorkTaskId, StoredWorkItem>>> LoadTasksAsync(WorkspaceId workspaceId, WorkPlanDefinition definition,
        CancellationToken token)
    {
        var members = new Dictionary<WorkTaskId, StoredWorkItem>();
        foreach (var taskId in definition.TaskIds)
        {
            var task = await tasks.GetAsync(workspaceId, context.PrincipalId, taskId.ToWorkItemId(), token);
            if (task?.Value.TaskHierarchy is null) return Result<Dictionary<WorkTaskId, StoredWorkItem>>.Failure("work_plan_task_not_found");
            members.Add(taskId, task);
        }
        return Result<Dictionary<WorkTaskId, StoredWorkItem>>.Success(members);
    }

    private async Task<string?> ValidateHierarchyAsync(WorkspaceId workspaceId, WorkPlanDefinition definition,
        IReadOnlyDictionary<WorkTaskId, StoredWorkItem> members, CancellationToken token)
    {
        var edges = definition.Dependencies.ToList();
        foreach (var treeId in members.Values.Where(item => item.Value.TaskHierarchy!.Kind == WorkTaskKind.Composite)
            .Select(item => item.Value.TaskHierarchy!.TreeId).Distinct())
        {
            var family = await tasks.ListTaskTreeAsync(workspaceId, context.PrincipalId, treeId, token);
            if (family.Count > WorkTaskTreeDefinition.MaximumTasks) return "work_plan_definition_invalid";
            foreach (var child in family.Where(item => item.Value.TaskHierarchy is { IsOptional: false, ParentTaskId: not null }))
            {
                var parent = child.Value.TaskHierarchy!.ParentTaskId!.Value;
                if (!members.ContainsKey(parent)) continue;
                var childId = WorkTaskId.FromWorkItem(child.Value.Id);
                if (!members.ContainsKey(childId)) return "work_plan_required_child_missing";
                edges.Add(new(parent, childId));
            }
        }
        return WorkPlanDefinition.HasCycle(definition.TaskIds, edges) ? "work_plan_hierarchy_dependency_cycle" : null;
    }

    private static WorkPlanTaskState Readiness(WorkPlanSnapshot plan, WorkPlanRevision revision, WorkTaskId taskId,
        IReadOnlyDictionary<WorkTaskId, StoredWorkItem> members)
    {
        var unmet = revision.Definition.Dependencies.Where(edge => edge.TaskId == taskId)
            .Select(edge => edge.DependsOnTaskId).Where(dependency => !members.TryGetValue(dependency, out var item)
                || item.Value.TaskAcceptance?.Outcome != WorkTaskOutcome.Accepted).ToArray();
        if (!members.TryGetValue(taskId, out var stored))
            return new(taskId, 0, WorkTaskOutcome.Pending, WorkPlanTaskReadiness.Blocked, ["task_unavailable"], unmet);
        var task = stored.Value;
        var outcome = task.TaskAcceptance?.Outcome ?? WorkTaskOutcome.Pending;
        if (outcome == WorkTaskOutcome.Accepted)
            return new(taskId, task.Version, outcome, WorkPlanTaskReadiness.Accepted, [], unmet);
        var reasons = new List<string>();
        if (plan.Status != WorkPlanStatus.Active) reasons.Add("plan_not_active");
        if (task.TaskHierarchy!.CreationDigest != revision.Tasks.Single(reference => reference.TaskId == taskId).CreationDigest)
            reasons.Add("task_intent_changed");
        if (outcome is WorkTaskOutcome.Failed or WorkTaskOutcome.Cancelled) reasons.Add("task_outcome_terminal");
        if (task.TaskHierarchy.Kind == WorkTaskKind.Composite) reasons.Add("composite_outcome_derived");
        if (IsActive(task.Status)) reasons.Add("task_active");
        else if (task.Status != WorkItemStatus.Pending) reasons.Add("task_review_required");
        if (unmet.Length > 0) reasons.Add("dependencies_not_accepted");
        return new(taskId, task.Version, outcome, reasons.Count == 0 ? WorkPlanTaskReadiness.Ready : WorkPlanTaskReadiness.Blocked, reasons, unmet);
    }
}
