using System.Text.Json.Serialization;
using Agentstration.Resources;

namespace Agentstration.Work;

public readonly record struct WorkTaskOperationId(Guid Value)
{
    public override string ToString() => Value.ToString("D");
}

[JsonConverter(typeof(JsonStringEnumConverter<ExternalTaskOperationKind>))]
public enum ExternalTaskOperationKind { Read, Update, Publish, Comment, Relate, Transition }
public enum WorkTaskOperationStatus { Pending, Uncertain, Succeeded, Rejected }
[JsonConverter(typeof(JsonStringEnumConverter<ExternalTaskProviderStatus>))]
public enum ExternalTaskProviderStatus { Missing, Succeeded, Rejected, Uncertain }
public sealed record ExternalTaskRelation(string Kind, string Repository, string TaskId);

public sealed record ExternalTaskOperationInput(ExternalTaskOperationKind Operation, string Repository = "", string? TaskId = null,
    string? Title = null, string? Body = null, string? State = null,
    IReadOnlyList<string>? Labels = null, IReadOnlyList<ExternalTaskRelation>? Relations = null, string? ExpectedRevision = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorkTrackingTarget? Tracking { get; init; }
    public string? Comment { get; init; }
    public WorkTrackingRelation? Relation { get; init; }
    public string? TransitionId { get; init; }

    public Result<ExternalTaskOperationInput> Validate()
    {
        if (Tracking is not null) return ValidateTracking();
        if (Comment is not null || Relation is not null || TransitionId is not null || Operation > ExternalTaskOperationKind.Publish
            || !Enum.IsDefined(Operation) || !Text(Repository, 512) || Repository.Contains('@', StringComparison.Ordinal)
            || ExpectedRevision?.Length is > 256 || Operation == ExternalTaskOperationKind.Update && !Text(ExpectedRevision, 256)
            || Operation != ExternalTaskOperationKind.Update && ExpectedRevision is not null
            || TaskId?.Length is > 256 || Title is not null && !Text(Title, 512) || Body?.Length is > 32_768 || State is not null && !Text(State, 64)
            || Labels?.Count > 32 || Labels?.Any(label => !Text(label, 128)) == true
            || Relations?.Count > 100 || Relations?.Any(relation => relation is null || !Text(relation.Kind, 64)
                || !Text(relation.Repository, 512) || !Text(relation.TaskId, 256)) == true
            || (Operation == ExternalTaskOperationKind.Publish ? !Text(Title, 512) || TaskId is not null : !Text(TaskId, 256))
            || Operation == ExternalTaskOperationKind.Read && (Title is not null || Body is not null || State is not null || Labels is not null || Relations is not null))
            return Result<ExternalTaskOperationInput>.Failure("task_provider_input_invalid");
        return Result<ExternalTaskOperationInput>.Success(this);
    }

    private Result<ExternalTaskOperationInput> ValidateTracking()
    {
        var mutation = Operation is not (ExternalTaskOperationKind.Read or ExternalTaskOperationKind.Publish);
        var valid = Enum.IsDefined(Operation) && Repository == "" && Text(Tracking!.ProviderId, 128) && Text(Tracking.TargetId, 128)
            && (Operation == ExternalTaskOperationKind.Publish ? TaskId is null && Text(Title, 512) : Text(TaskId, 128))
            && (mutation ? Text(ExpectedRevision, 128) : ExpectedRevision is null)
            && State is null && Relations is null && Body?.Length is not > 32_768
            && (Title is null || Text(Title, 512))
            && (Labels is null || Labels.Count <= 32 && Labels.All(value => Text(value, 128)) && Labels.Distinct().Count() == Labels.Count)
            && (Operation is ExternalTaskOperationKind.Publish or ExternalTaskOperationKind.Update || Title is null && Body is null)
            && (Operation == ExternalTaskOperationKind.Update || Labels is null)
            && (Operation == ExternalTaskOperationKind.Comment ? !string.IsNullOrWhiteSpace(Comment) && Comment.Length <= 32_768 : Comment is null)
            && (Operation == ExternalTaskOperationKind.Transition ? Text(TransitionId, 128) : TransitionId is null)
            && (Operation == ExternalTaskOperationKind.Relate ? Relation is { } relation && Enum.IsDefined(relation.Kind)
                && relation.Item is { } item && Text(item.ProviderId, 128) && Text(item.TargetId, 128) && Text(item.ItemId, 128) : Relation is null);
        return valid ? Result<ExternalTaskOperationInput>.Success(this) : Result<ExternalTaskOperationInput>.Failure("task_provider_input_invalid");
    }

    private static bool Text(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl);
}

public sealed record ExternalTaskSnapshot(string Repository, string TaskId, string Revision, string Title, string? Body, string State,
    Uri? Url, IReadOnlyList<string> Labels, IReadOnlyList<ExternalTaskRelation> Relations)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CompletionReason { get; init; }
    public WorkTrackingObservation? Tracking { get; init; }
}
public sealed record WorkTaskProviderCapabilities(string Version, IReadOnlyList<ExternalTaskOperationKind> Operations,
    bool IdempotentOperations, bool OperationRecovery);
public sealed record WorkTaskProviderRoute(WorkProjectContextBinding Binding, WorkProjectResourceReference Tool,
    WorkTaskProviderCapabilities Capabilities)
{
    public WorkProjectResourceReference? Provider { get; init; }
    public WorkTrackingRoute? Tracking { get; init; }
}
public sealed record ExternalTaskProviderRequest(string Mode, string OperationKey, string IntentDigest, ExternalTaskOperationInput Input);
public sealed record ExternalTaskProviderResponse(string OperationKey, string IntentDigest, ExternalTaskProviderStatus Status,
    ExternalTaskSnapshot? Task = null, string? ErrorCode = null);

public sealed record WorkTaskOperation(WorkTaskOperationId Id, WorkspaceId WorkspaceId, Guid OwnerPrincipalId, Guid TenantId,
    WorkProjectId ProjectId, long ProjectVersion, WorkPlanId? PlanId, long? PlanRevision, string? PlanDigest,
    ExternalTaskOperationInput Input, string IntentDigest, string OperationKey, WorkTaskProviderRoute Route,
    WorkTaskOperationStatus Status, long Version, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    ExternalTaskSnapshot? Task = null, string? ErrorCode = null);
public sealed record WorkTaskOperationPage(IReadOnlyList<WorkTaskOperation> Items, bool HasMore);

public static class WorkTaskProviderProtocol
{
    public const string MetadataKey = "agentstration.work.task-provider";
    public const string Version = "1.0";
}

// Native evidence and guarantees remain provider-neutral in Work persistence.
public sealed record WorkTrackingTarget(string ProviderId, string TargetId);
public sealed record WorkTrackingItem(string ProviderId, string TargetId, string ItemId);
[JsonConverter(typeof(JsonStringEnumConverter<WorkTrackingRelationKind>))]
public enum WorkTrackingRelationKind { Parent, Child, Related, Blocks, BlockedBy }
public sealed record WorkTrackingRelation(WorkTrackingRelationKind Kind, WorkTrackingItem Item);
public sealed record WorkTrackingComment(string Id, string Text);
public sealed record WorkTrackingTransition(string Id, string DestinationState);
public sealed record WorkTrackingObservation(WorkTrackingItem Identity, bool RelationsComplete,
    IReadOnlyList<WorkTrackingRelation> Relations, IReadOnlyList<WorkTrackingComment> Comments,
    IReadOnlyList<WorkTrackingTransition> Transitions);
[JsonConverter(typeof(JsonStringEnumConverter<WorkTrackingConcurrency>))]
public enum WorkTrackingConcurrency { None, Observation, AtomicRevision }
[JsonConverter(typeof(JsonStringEnumConverter<WorkTrackingRecovery>))]
public enum WorkTrackingRecovery { None, Process, Durable }
public sealed record WorkTrackingCapabilities(IReadOnlyList<string> Operations, IReadOnlyList<string> UpdateFields,
    IReadOnlyList<WorkTrackingRelationKind> RelationKinds, WorkTrackingConcurrency Concurrency,
    WorkTrackingRecovery Recovery, int ReceiptWindowSeconds, int MaximumPageSize, int MaximumResultBytes);
public sealed record WorkTrackingRoute(WorkProjectResolvedBinding Connection, WorkTrackingCapabilities Capabilities,
    string? WireIntentDigest = null);
