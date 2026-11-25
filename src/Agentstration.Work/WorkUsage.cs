using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.Flows;
using Agentstration.Resources;

namespace Agentstration.Work;

public readonly record struct WorkUsageId(Guid Value)
{
    public override string ToString() => Value.ToString("D");
}

[JsonConverter(typeof(JsonStringEnumConverter<WorkUsageMeasurement>))]
public enum WorkUsageMeasurement { Measured, Estimated, Unknown }
[JsonConverter(typeof(JsonStringEnumConverter<WorkUsageSourceKind>))]
public enum WorkUsageSourceKind { FlowRun, CodingCli, Unavailable, NotStarted }
[JsonConverter(typeof(JsonStringEnumConverter<WorkUsageFlowBoundary>))]
public enum WorkUsageFlowBoundary { Direct, Inclusive }
[JsonConverter(typeof(JsonStringEnumConverter<WorkUsageReadSource>))]
public enum WorkUsageReadSource { Local, Shared }

public sealed record WorkUsageObservation(string Id, long? InputTokens, long? OutputTokens, long? ProviderTotalTokens,
    long? CachedInputTokens, long? ReasoningOutputTokens, DateTimeOffset ObservedAt, string? Reason = null);

public sealed record WorkUsageSource(WorkUsageSourceKind Kind, string AdapterVersion, Guid AttemptId,
    Guid WorkItemId, string FlowRunId, WorkPlanAttemptStatus Outcome)
{
    public Guid? AssignmentId { get; init; }
    public Guid? AssignmentAttemptId { get; init; }
    public Guid? InvocationId { get; init; }
    public IReadOnlyList<Guid> EventIds { get; init; } = [];
    public bool InvocationCountKnown { get; init; } = true;
}

public sealed record WorkUsageTotals(FlowRunTokenUsageTotal Direct, FlowRunTokenUsageTotal Inclusive)
{
    // These are provider-reported subsets, not additional input or output tokens.
    public long? CachedInputTokens { get; init; }
    public long? ReasoningOutputTokens { get; init; }
}

public sealed record WorkUsageCorrection(Guid CommandId, string IntentDigest, string Reason, string EvidenceReference);

/// <summary>Portable accounting data. Physical Workspace, Principal and local Project ownership stay in the local envelope.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkUsageRecord(WorkUsageId Id, WorkUsageId RootId, long Revision,
    WorkPlanId PlanId, long PlanRevision, string PlanDigest, WorkTaskId TaskId, long TaskVersion, string TaskDigest,
    WorkUsageSource Source, WorkUsageMeasurement Measurement, WorkUsageTotals Totals, DateTimeOffset RecordedAt)
{
    public string FormatVersion { get; init; } = WorkUsageFormat.Version;
    public WorkSharingProjectId? ProjectId { get; init; }
    public WorkParticipantId? ParticipantId { get; init; }
    public IReadOnlyList<WorkTaskId> AncestorTaskIds { get; init; } = [];
    public IReadOnlyList<WorkUsageObservation> Observations { get; init; } = [];
    public WorkUsageId? PreviousId { get; init; }
    public string? PreviousDigest { get; init; }
    public WorkUsageCorrection? Correction { get; init; }
}

public sealed record WorkUsageEntry(WorkspaceId WorkspaceId, Guid PrincipalId, WorkProjectId ProjectId,
    WorkUsageRecord Record, string Digest, DateTimeOffset CreatedAt);
public sealed record WorkUsagePage(IReadOnlyList<WorkUsageEntry> Items, bool HasMore);
public sealed record WorkUsageCapture(IReadOnlyList<WorkUsageEntry> Items, bool Replayed);

public static class WorkUsageFormat
{
    public const string Version = "1.0";
    public const int MaximumRecordBytes = 16 * 1024;
    public const int MaximumSummaryRecords = 5000;
    public const long MaximumAggregateTokens = 1_000_000_000_000_000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string Digest(WorkUsageRecord record) => WorkPlanDefinition.Digest(JsonSerializer.SerializeToElement(record, Json));

    public static bool Valid(WorkUsageRecord? value) => value is not null && value.FormatVersion == Version
        && value.Id.Value != Guid.Empty && value.RootId.Value != Guid.Empty && value.Revision >= 1
        && value.PlanId.Value != Guid.Empty && value.PlanRevision >= 1 && WorkSharingFormat.Digest(value.PlanDigest)
        && value.TaskId.Value != Guid.Empty && value.TaskVersion >= 1 && WorkSharingFormat.Digest(value.TaskDigest)
        && value.RecordedAt != default && Enum.IsDefined(value.Measurement)
        && (value.ProjectId is null && value.ParticipantId is null || value.ProjectId?.Value != Guid.Empty && value.ProjectId is not null
            && value.ParticipantId?.Value != Guid.Empty && value.ParticipantId is not null)
        && value.AncestorTaskIds is { Count: <= WorkTaskTreeDefinition.MaximumDepth }
        && value.AncestorTaskIds.All(id => id.Value != Guid.Empty && id != value.TaskId)
        && value.AncestorTaskIds.Distinct().Count() == value.AncestorTaskIds.Count
        && ValidSource(value.Source) && ValidTotals(value.Totals)
        && (value.Measurement != WorkUsageMeasurement.Unknown || Unknown(value.Totals))
        && value.Observations is { Count: <= 32 } && value.Observations.All(ValidObservation)
        && value.Observations.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() == value.Observations.Count
        && (value.Revision == 1 ? value.Id == value.RootId && value.PreviousId is null && value.PreviousDigest is null && value.Correction is null
            : value.Id != value.RootId && value.PreviousId is { Value: var previous } && previous != Guid.Empty && value.PreviousId != value.Id
                && WorkSharingFormat.Digest(value.PreviousDigest) && value.Correction is { CommandId: var command } correction
                && command != Guid.Empty && WorkSharingFormat.Digest(correction.IntentDigest) && Code(correction.Reason)
                && Reference(correction.EvidenceReference))
        && JsonSerializer.SerializeToUtf8Bytes(value, Json).Length <= MaximumRecordBytes;

    public static bool ValidTotals(WorkUsageTotals? totals) => totals is not null && ValidTotal(totals.Direct) && ValidTotal(totals.Inclusive)
        && Count(totals.CachedInputTokens) && Count(totals.ReasoningOutputTokens)
        && totals.Inclusive.InvocationCount >= totals.Direct.InvocationCount && totals.Inclusive.ReportedInvocationCount >= totals.Direct.ReportedInvocationCount
        && Includes(totals.Inclusive.InputTokens, totals.Direct.InputTokens) && Includes(totals.Inclusive.OutputTokens, totals.Direct.OutputTokens)
        && Includes(totals.Inclusive.TotalTokens, totals.Direct.TotalTokens)
        && (totals.Inclusive.Coverage != FlowRunTokenUsageCoverage.Complete
            || !(totals.CachedInputTokens > totals.Inclusive.InputTokens) && !(totals.ReasoningOutputTokens > totals.Inclusive.OutputTokens));

    private static bool ValidTotal(FlowRunTokenUsageTotal? total) => total is not null && Count(total.InputTokens) && Count(total.OutputTokens) && Count(total.TotalTokens)
        && total.InvocationCount >= 0 && total.ReportedInvocationCount >= 0 && total.ReportedInvocationCount <= total.InvocationCount
        && (total.Coverage switch
        {
            FlowRunTokenUsageCoverage.Complete => total.InvocationCount > 0 && total.ReportedInvocationCount == total.InvocationCount
                && total.InputTokens is not null && total.OutputTokens is not null && total.TotalTokens is not null,
            FlowRunTokenUsageCoverage.Partial => total.ReportedInvocationCount > 0
                && (total.InputTokens is not null || total.OutputTokens is not null || total.TotalTokens is not null),
            FlowRunTokenUsageCoverage.Unavailable => total.ReportedInvocationCount == 0 && total.InputTokens is null && total.OutputTokens is null && total.TotalTokens is null,
            _ => false
        });
    private static bool ValidSource(WorkUsageSource? source) => source is not null && Enum.IsDefined(source.Kind)
        && Reference(source.AdapterVersion) && source.AdapterVersion.Length <= 64 && source.AttemptId != Guid.Empty && source.WorkItemId != Guid.Empty
        && source.FlowRunId is { Length: > 0 and <= 200 } && !source.FlowRunId.Any(char.IsControl)
        && source.Outcome is WorkPlanAttemptStatus.Completed or WorkPlanAttemptStatus.Failed or WorkPlanAttemptStatus.Cancelled
        && source.AssignmentId != Guid.Empty && source.AssignmentAttemptId != Guid.Empty && source.InvocationId != Guid.Empty
        && (source.Kind != WorkUsageSourceKind.CodingCli || source.AssignmentId is not null && source.AssignmentAttemptId is not null && source.InvocationId is not null)
        && source.EventIds is { Count: <= 64 } && source.EventIds.All(id => id != Guid.Empty) && source.EventIds.Distinct().Count() == source.EventIds.Count;
    public static bool ValidObservation(WorkUsageObservation? value) => value is not null && Reference(value.Id) && value.Id.Length <= 128
        && ObservationCount(value.InputTokens) && ObservationCount(value.OutputTokens) && ObservationCount(value.ProviderTotalTokens)
        && ObservationCount(value.CachedInputTokens) && ObservationCount(value.ReasoningOutputTokens) && value.ObservedAt != default
        && !(value.InputTokens is { } input && value.CachedInputTokens > input) && !(value.OutputTokens is { } output && value.ReasoningOutputTokens > output)
        && (value.Reason is null || Code(value.Reason));
    private static bool Unknown(WorkUsageTotals value) => value.Direct.Coverage == FlowRunTokenUsageCoverage.Unavailable
        && value.Inclusive.Coverage == FlowRunTokenUsageCoverage.Unavailable && value.CachedInputTokens is null && value.ReasoningOutputTokens is null;
    private static bool Includes(long? inclusive, long? direct) => direct is null || inclusive is { } total && total >= direct.Value;
    private static bool Count(long? value) => value is null or >= 0 and <= MaximumAggregateTokens;
    private static bool ObservationCount(long? value) => value is null or >= 0 and <= 1_000_000_000_000;
    private static bool Code(string? value) => value is { Length: > 0 and <= 128 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.');
    private static bool Reference(string? value) => value is { Length: > 0 and <= 256 }
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or ':' or '/');
}
