using System.Security.Cryptography;
using System.Text.Json;
using Agentstration.Resources;

namespace Agentstration.Work;

public enum WorkPlanStatus { Draft, Validated, Active, Inactive }
public enum WorkPlanAction { Create, Revise, Validate, Activate, Deactivate, StartExecution, AdvanceExecution, CancelExecution, RetryTask, RecordExecutionEvidence }
public enum WorkPlanTaskReadiness { Ready, Blocked, Accepted }
public sealed record WorkTaskDependency(WorkTaskId TaskId, WorkTaskId DependsOnTaskId);
public sealed record WorkPlanTaskReference(WorkTaskId TaskId, string CreationDigest);
public sealed record WorkPlanTaskObservation(WorkTaskId TaskId, long Version);
public sealed record WorkPlanTaskConnections(WorkTaskId TaskId, IReadOnlyList<string> Names);

public sealed record WorkPlanDefinition(string Intent, IReadOnlyList<WorkTaskId> TaskIds,
    IReadOnlyList<WorkTaskDependency> Dependencies)
{
    public IReadOnlyList<WorkPlanTaskConnections> TaskConnections { get; init; } = [];
    public const int MaximumTasks = 100;
    public const int MaximumDependencies = 1_000;
    public const int MaximumIntentLength = 8_000;
    public const int MaximumRevisions = 100;
    public const int MaximumCommands = 1_024;

    public Result<WorkPlanDefinition> Validate()
    {
        if (string.IsNullOrWhiteSpace(Intent) || Intent.Length > MaximumIntentLength
            || TaskIds is null || TaskIds.Count is < 1 or > MaximumTasks
            || Dependencies is null || Dependencies.Count > MaximumDependencies)
            return Result<WorkPlanDefinition>.Failure("work_plan_definition_invalid");
        var ids = TaskIds.ToHashSet();
        if (ids.Count != TaskIds.Count || ids.Any(id => id.Value == Guid.Empty))
            return Result<WorkPlanDefinition>.Failure("work_plan_task_duplicate_or_invalid");
        if (Dependencies.Any(edge => edge is null || !ids.Contains(edge.TaskId) || !ids.Contains(edge.DependsOnTaskId)))
            return Result<WorkPlanDefinition>.Failure("work_plan_dependency_not_member");
        if (Dependencies.Distinct().Count() != Dependencies.Count)
            return Result<WorkPlanDefinition>.Failure("work_plan_dependency_duplicate");
        if (TaskConnections is null || TaskConnections.Count > MaximumTasks
            || TaskConnections.Any(value => value is null || !ids.Contains(value.TaskId) || value.Names is not { Count: > 0 and <= 32 }
                || value.Names.Any(name => !WorkProjectDefinition.ValidName(name)) || value.Names.Distinct(StringComparer.Ordinal).Count() != value.Names.Count)
            || TaskConnections.Select(value => value.TaskId).Distinct().Count() != TaskConnections.Count)
            return Result<WorkPlanDefinition>.Failure("work_plan_connections_invalid");
        if (HasCycle(ids, Dependencies)) return Result<WorkPlanDefinition>.Failure("work_plan_dependency_cycle");
        return Result<WorkPlanDefinition>.Success(this with
        {
            TaskConnections = TaskConnections.OrderBy(value => value.TaskId.Value)
                .Select(value => value with { Names = value.Names.Order(StringComparer.Ordinal).ToArray() }).ToArray(),
            Intent = Intent.Trim(),
            TaskIds = ids.OrderBy(id => id.Value).ToArray(),
            Dependencies = Dependencies.OrderBy(edge => edge.TaskId.Value).ThenBy(edge => edge.DependsOnTaskId.Value).ToArray()
        });
    }

    public static bool HasCycle(IReadOnlyCollection<WorkTaskId> members, IReadOnlyList<WorkTaskDependency> edges)
    {
        var remaining = members.ToHashSet();
        while (remaining.Count > 0)
        {
            var ready = remaining.Where(id => !edges.Any(edge => edge.TaskId == id && remaining.Contains(edge.DependsOnTaskId))).ToArray();
            if (ready.Length == 0) return true;
            remaining.ExceptWith(ready);
        }
        return false;
    }

    public string ContentDigest(IReadOnlyList<WorkPlanTaskReference> tasks) => Digest(new { Intent, Tasks = tasks, Dependencies, TaskConnections });
    public static string Digest<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
}

public sealed record WorkPlanSnapshot(WorkPlanId Id, WorkspaceId WorkspaceId, Guid OwnerPrincipalId, WorkPlanStatus Status,
    long Version, long CurrentRevision, long? ValidatedRevision, string? ValidatedDigest,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public WorkPlanExecution? Execution { get; init; }
    public IReadOnlyList<WorkPlanAttempt> Attempts { get; init; } = [];
}
public sealed record WorkPlanRevision(WorkPlanId PlanId, long Number, WorkPlanDefinition Definition,
    IReadOnlyList<WorkPlanTaskReference> Tasks, string Digest, DateTimeOffset CreatedAt);
public sealed record WorkPlanCommandReceipt(Guid CommandId, long PlanVersion, WorkPlanAction Action, string IntentDigest, long Revision,
    string RevisionDigest, DateTimeOffset AppliedAt, IReadOnlyList<WorkPlanTaskObservation> ObservedTasks);
public sealed record WorkPlanTaskState(WorkTaskId TaskId, long Version, WorkTaskOutcome Outcome, WorkPlanTaskReadiness Readiness,
    IReadOnlyList<string> Reasons, IReadOnlyList<WorkTaskId> UnmetDependencies);
public sealed record WorkPlanView(WorkPlanSnapshot Plan, WorkPlanRevision Revision, IReadOnlyList<WorkPlanTaskState> Tasks);
