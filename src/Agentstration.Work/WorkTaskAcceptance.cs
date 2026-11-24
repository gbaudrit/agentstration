namespace Agentstration.Work;

public enum WorkTaskOutcome { Pending, Accepted, Failed, Cancelled }

public sealed record WorkTaskEvidence(Guid EventId, WorkTaskOutcome Outcome, string Evidence, DateTimeOffset RecordedAt);
public sealed record WorkTaskChildOutcome(WorkTaskId TaskId, WorkTaskOutcome Outcome, long Version);
public sealed record WorkTaskAcceptance(WorkTaskOutcome Outcome, WorkTaskEvidence? Evidence,
    IReadOnlyList<WorkTaskChildOutcome> RequiredChildren, DateTimeOffset UpdatedAt);

public static class WorkTaskAcceptanceRules
{
    public const int MaximumEvidenceLength = 8_000;

    public static WorkTaskAcceptance Consolidate(IReadOnlyList<WorkItem> children, DateTimeOffset now)
    {
        var required = children.Where(child => child.TaskHierarchy is { IsOptional: false })
            .OrderBy(child => child.Id.Value)
            .Select(child => new WorkTaskChildOutcome(WorkTaskId.FromWorkItem(child.Id),
                child.TaskAcceptance?.Outcome ?? WorkTaskOutcome.Pending, child.Version)).ToArray();
        var outcome = required.Any(child => child.Outcome == WorkTaskOutcome.Failed) ? WorkTaskOutcome.Failed
            : required.Any(child => child.Outcome == WorkTaskOutcome.Cancelled) ? WorkTaskOutcome.Cancelled
            : required.Length > 0 && required.All(child => child.Outcome == WorkTaskOutcome.Accepted) ? WorkTaskOutcome.Accepted
            : WorkTaskOutcome.Pending;
        return new(outcome, null, required, now);
    }
}
