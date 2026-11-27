using System.Text.Json;
using Agentstration.Flows;

namespace Agentstration.Work;

public enum WorkPlanAttemptStatus { Admitted, Submitted, Completed, Failed, Cancelled }

public sealed record WorkPlanTaskTarget(WorkTaskId TaskId, FlowReference Flow, JsonElement Input)
{
    public IReadOnlyList<string> Connections { get; init; } = [];
}

public sealed record WorkPlanExecution(FlowRunScope Scope, long Revision, string RevisionDigest,
    bool Enabled, IReadOnlyList<WorkPlanTaskTarget> Targets)
{
    public WorkProjectId? ProjectId { get; init; }
}

public sealed record WorkPlanAttempt(Guid Id, WorkTaskId TaskId, long PlanRevision, string PlanDigest,
    long TaskVersion, string TaskDigest, WorkItemId WorkItemId, string FlowRunId, FlowReference Flow,
    JsonElement Input, IReadOnlyList<WorkPlanTaskObservation> Dependencies, WorkPlanAttemptStatus Status,
    bool CancellationRequested, DateTimeOffset AdmittedAt, DateTimeOffset UpdatedAt,
    string? Diagnostic = null)
{
    public IReadOnlyList<WorkPlanAdmissionEvidence> AdmissionEvidence { get; init; } = [];
    public WorkProjectContextSnapshot? ProjectContext { get; init; }
    public WorkTaskExecutionEvidence? Evidence { get; init; }
    public WorkResult? Result { get; init; }
    public WorkError? Error { get; init; }
}

public sealed record WorkPlanAdmissionEvidence(string Kind, string Reference, string Revision, DateTimeOffset ObservedAt);
