using Agentstration.Resources;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentstration.Work;

public readonly record struct WorkSharingProjectId(Guid Value) { public override string ToString() => Value.ToString("D"); }
public readonly record struct WorkParticipantId(Guid Value) { public override string ToString() => Value.ToString("D"); }
public readonly record struct WorkSharingOperationId(Guid Value) { public override string ToString() => Value.ToString("D"); }

public sealed record WorkProjectSharingConnection(WorkSharingProjectId ProjectId, WorkParticipantId ParticipantId,
    string BindingName, string Location, string FormatVersion = WorkSharingFormat.Version);

[JsonConverter(typeof(JsonStringEnumConverter<WorkSharingDomain>))]
public enum WorkSharingDomain { BootstrapProfile, ProjectMetadata, Plan, Contribution, Usage }
public sealed record WorkSharingRecordKey(WorkSharingDomain Domain, string Name);
public sealed record WorkSharingRecord(WorkSharingRecordKey Key, long Version, JsonElement Content, string Digest,
    WorkParticipantId ParticipantId, DateTimeOffset CreatedAt);
public sealed record WorkSharingRecordEntry(WorkSharingRecordKey Key, long Version, string Digest, int SizeBytes);
public sealed record WorkSharingSpace(WorkSharingProjectId ProjectId, IReadOnlyList<WorkSharingRecordEntry> Records,
    string FormatVersion = WorkSharingFormat.Version);
public sealed record WorkSharingSnapshot(string? Revision, WorkSharingSpace Space, IReadOnlyList<WorkSharingRecord> Records);
public sealed record WorkTaskAllocation(WorkTaskId TaskId, WorkParticipantId ParticipantId);
public sealed record WorkSharingPlan(WorkPlanId PlanId, WorkPlanDefinition Definition,
    IReadOnlyList<WorkTaskTreeDefinition> Trees, IReadOnlyList<WorkTaskAllocation> Allocation)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorkSharingIssueReference? Issue { get; init; }
}
public sealed record WorkSharingIssueReference(string Provider, string Repository, string TaskId,
    string PlanLabelPrefix, string ParticipantLabelPrefix);
public sealed record WorkSharingProjectMetadata(string DisplayName, string? Description, IReadOnlyList<WorkParticipantId> Participants);
public sealed record WorkSharingContribution(Guid Id, WorkPlanId? PlanId, WorkTaskId? TaskId, string Kind, string Evidence,
    IReadOnlyList<string> ArtifactReferences);
public sealed record WorkSharingUsage(Guid Id, WorkPlanId? PlanId, WorkTaskId? TaskId, IReadOnlyDictionary<string, long> Counters)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorkUsageRecord? Accounting { get; init; }
}
public sealed record WorkSharingBootstrapResource(string ApiVersion, string Kind, string Name, string Namespace, JsonElement Definition);
public sealed record WorkSharingBootstrapProfile(string Name, string Locale, IReadOnlyList<WorkSharingBootstrapResource> Resources);

public static class WorkSharingFormat
{
    public const string Version = "1.0";
    public const int MaximumRecords = 64;
    public const int MaximumRecordBytes = 16 * 1024;
    public const int MaximumSnapshotBytes = 1024 * 1024;

    public static bool ValidConnection(WorkProjectSharingConnection? connection, IReadOnlyList<WorkProjectContextBinding> bindings) => connection is null
        || connection.ProjectId.Value != Guid.Empty && connection.ParticipantId.Value != Guid.Empty && connection.FormatVersion == Version
            && WorkProjectDefinition.ValidName(connection.BindingName) && connection.Location is { Length: > 0 and <= 512 }
            && !connection.Location.Any(char.IsControl)
            && bindings.Any(binding => binding.Name == connection.BindingName && binding.Purpose == WorkProjectBindingPurpose.Sharing);

    public static bool ValidKey(WorkSharingRecordKey? key) => key is not null && Enum.IsDefined(key.Domain) && WorkProjectDefinition.ValidName(key.Name);

    public static Result<WorkSharingSpace> Validate(WorkSharingSpace space)
    {
        if (space.ProjectId.Value == Guid.Empty || space.FormatVersion != Version || space.Records is null || space.Records.Count > MaximumRecords
            || space.Records.Any(record => record is null || !ValidKey(record.Key) || record.Version < 1 || !Digest(record.Digest) || record.SizeBytes is < 1 or > MaximumRecordBytes + 1024)
            || space.Records.Select(record => record.Key).Distinct().Count() != space.Records.Count)
            return Result<WorkSharingSpace>.Failure("sharing_space_invalid");
        return Result<WorkSharingSpace>.Success(space);
    }

    public static Result<WorkSharingRecord> Validate(WorkSharingRecord record)
    {
        if (!ValidKey(record.Key) || record.Version < 1 || record.ParticipantId.Value == Guid.Empty || !Digest(record.Digest)
            || record.Content.ValueKind != JsonValueKind.Object || JsonSerializer.SerializeToUtf8Bytes(record.Content).Length > MaximumRecordBytes
            || record.Digest != WorkPlanDefinition.Digest(record.Content)) return Result<WorkSharingRecord>.Failure("sharing_record_invalid");
        return Result<WorkSharingRecord>.Success(record);
    }

    public static bool Digest(string? value) => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public enum WorkSharingOperationStatus { Pending, Uncertain, Conflict, Succeeded, Rejected }
public sealed record WorkSharingChange(WorkSharingRecordKey Key, long ExpectedVersion, JsonElement Content);
public sealed record WorkSharingTransportAttempt(int Number, string OperationKey, string IntentDigest, string? ExpectedRevision,
    WorkSharingSpace Space, IReadOnlyList<WorkSharingRecord> Records);
public sealed record WorkSharingOperation(WorkSharingOperationId Id, WorkspaceId WorkspaceId, Guid PrincipalId, WorkProjectId ProjectId,
    long ProjectVersion, WorkProjectSharingConnection Connection, WorkProjectContextBinding Binding,
    IReadOnlyList<WorkSharingChange> Changes, string IntentDigest, WorkSharingOperationStatus Status, long Version,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, IReadOnlyList<WorkSharingTransportAttempt> Attempts,
    string? RemoteRevision = null, string? ErrorCode = null)
{
    public IReadOnlyList<WorkSharingPlanObservation> Plans { get; init; } = [];
}
public sealed record WorkSharingPlanObservation(WorkPlanId PlanId, long Version);
public sealed record WorkProjectSharingState(WorkspaceId WorkspaceId, Guid PrincipalId, WorkProjectId ProjectId, long ProjectVersion,
    WorkProjectSharingConnection Connection, string? Revision, WorkSharingSpace Space, IReadOnlyList<WorkSharingRecord> Records,
    long Version, DateTimeOffset UpdatedAt);

public sealed record WorkPlanExternalOperationReference(WorkPlanId PlanId, WorkTaskOperationId OperationId);
