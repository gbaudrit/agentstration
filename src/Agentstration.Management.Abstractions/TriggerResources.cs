using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.Resources;

namespace Agentstration.Management.Abstractions;

[JsonConverter(typeof(JsonStringEnumConverter<TriggerScheduleType>))]
public enum TriggerScheduleType
{
    [JsonStringEnumMemberName("once")] Once,
    [JsonStringEnumMemberName("cron")] Cron,
    [JsonStringEnumMemberName("interval")] Interval
}

[JsonConverter(typeof(JsonStringEnumConverter<TriggerMisfirePolicy>))]
public enum TriggerMisfirePolicy
{
    [JsonStringEnumMemberName("skip")] Skip,
    [JsonStringEnumMemberName("fireOnce")] FireOnce
}

[JsonConverter(typeof(JsonStringEnumConverter<TriggerConcurrencyPolicy>))]
public enum TriggerConcurrencyPolicy
{
    [JsonStringEnumMemberName("skip")] Skip,
    [JsonStringEnumMemberName("allow")] Allow
}

public sealed record TriggerSchedule
{
    public TriggerScheduleType Type { get; init; }
    public string? Expression { get; init; }
    public string? TimeZone { get; init; }
    public DateTimeOffset? At { get; init; }
    public DateTimeOffset? StartAt { get; init; }
    public string? Every { get; init; }
}

public sealed record TriggerSource
{
    public string Kind { get; init; } = "schedule";
    public TriggerSchedule? Schedule { get; init; }
}

public sealed record TriggerFlowTarget
{
    public required string Name { get; init; }
    public ResourceNamespace? Namespace { get; init; }
    public string? Version { get; init; }
}

public sealed record TriggerTarget
{
    public string Kind { get; init; } = "flow";
    public TriggerFlowTarget? Flow { get; init; }
}

public sealed record TriggerExecutionScope(Guid TenantId, Guid WorkspaceId, Guid PrincipalId);

public sealed record TriggerProperties
{
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public bool Enabled { get; init; }
    public required TriggerSource Source { get; init; }
    public required TriggerTarget Target { get; init; }
    public JsonElement Input { get; init; } = JsonSerializer.SerializeToElement(new Dictionary<string, object?>());
    public TriggerMisfirePolicy MisfirePolicy { get; init; } = TriggerMisfirePolicy.FireOnce;
    public TriggerConcurrencyPolicy ConcurrencyPolicy { get; init; } = TriggerConcurrencyPolicy.Skip;
    public TriggerExecutionScope? ExecutionScope { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<TriggerLastOutcome>))]
public enum TriggerLastOutcome
{
    [JsonStringEnumMemberName("none")] None,
    [JsonStringEnumMemberName("submitted")] Submitted,
    [JsonStringEnumMemberName("skipped")] Skipped,
    [JsonStringEnumMemberName("failed")] Failed
}

public sealed record TriggerObservedStatus
{
    public DateTimeOffset? LastScheduledAt { get; init; }
    public DateTimeOffset? LastFiredAt { get; init; }
    public DateTimeOffset? NextOccurrenceAt { get; init; }
    public TriggerLastOutcome LastOutcome { get; init; }
    public string? LastErrorCode { get; init; }
}

public sealed record TriggerResource : Resource
{
    public TriggerProperties Definition { get; init; } = null!;
    public TriggerObservedStatus Observed { get; init; } = new();
}

[JsonConverter(typeof(JsonStringEnumConverter<TriggerOccurrenceKind>))]
public enum TriggerOccurrenceKind
{
    [JsonStringEnumMemberName("scheduled")] Scheduled,
    [JsonStringEnumMemberName("manual")] Manual
}

[JsonConverter(typeof(JsonStringEnumConverter<TriggerOccurrenceOutcome>))]
public enum TriggerOccurrenceOutcome
{
    [JsonStringEnumMemberName("pending")] Pending,
    [JsonStringEnumMemberName("submitted")] Submitted,
    [JsonStringEnumMemberName("skipped")] Skipped,
    [JsonStringEnumMemberName("failed")] Failed
}

public sealed record TriggerOccurrence
{
    public required Guid Id { get; init; }
    public required Guid TenantId { get; init; }
    public required Guid WorkspaceId { get; init; }
    public required Guid TriggerUid { get; init; }
    public required string TriggerName { get; init; }
    public required ResourceNamespace TriggerNamespace { get; init; }
    public required long TriggerGeneration { get; init; }
    public required TriggerOccurrenceKind Kind { get; init; }
    public required DateTimeOffset ScheduledAt { get; init; }
    public DateTimeOffset? FiredAt { get; init; }
    public TriggerOccurrenceOutcome Outcome { get; init; } = TriggerOccurrenceOutcome.Pending;
    public string? WorkItemId { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

public interface ITriggerScheduleCalculator
{
    void Validate(TriggerSchedule schedule);
    DateTimeOffset? GetNextOccurrence(TriggerSchedule schedule, DateTimeOffset after);
}

public interface ITriggerTargetValidator
{
    Task ValidateAsync(ResourceNamespace ownerNamespace, TriggerTarget target, CancellationToken cancellationToken);
}

public interface ITriggerSchedulerProjection
{
    Task ReconcileAsync(TriggerResource trigger, CancellationToken cancellationToken);
    Task RemoveAsync(Guid workspaceId, Guid triggerUid, CancellationToken cancellationToken);
}

public interface ITriggerOccurrenceStore
{
    Task<bool> TryCreateAsync(TriggerOccurrence occurrence, CancellationToken cancellationToken);
    Task CompleteAsync(Guid workspaceId, Guid occurrenceId, TriggerOccurrenceOutcome outcome, DateTimeOffset firedAt, string? workItemId, string? errorCode, string? errorMessage, CancellationToken cancellationToken);
    Task<IReadOnlyList<TriggerOccurrence>> ListAsync(Guid workspaceId, Guid triggerUid, int take, CancellationToken cancellationToken);
}

public sealed record TriggerSubmission(string WorkItemId);

public interface ITriggerWorkSubmitter
{
    Task<bool> HasActiveWorkAsync(Guid workspaceId, Guid triggerUid, CancellationToken cancellationToken);
    Task<TriggerSubmission?> GetExistingAsync(Guid workspaceId, Guid occurrenceId, CancellationToken cancellationToken);
    Task<TriggerSubmission> SubmitAsync(TriggerResource trigger, TriggerOccurrence occurrence, CancellationToken cancellationToken);
}

public interface ITriggerExecutionAuthorizer
{
    Task AuthorizeAsync(TriggerExecutionScope executionScope, CancellationToken cancellationToken);
    IDisposable Enter(TriggerExecutionScope executionScope);
}
