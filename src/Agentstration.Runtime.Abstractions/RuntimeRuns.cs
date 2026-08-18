using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.Resources;

namespace Agentstration.Runtime.Abstractions;

public static class RuntimeApiVersions
{
    public const string V20260801 = "2026-08-01";
}

public static class RuntimeResourceTypes
{
    public const string Runs = "Agentstration.Runtime/runs";
}

[JsonConverter(typeof(JsonStringEnumConverter<RuntimeRunState>))]
public enum RuntimeRunState { Pending, Running, Succeeded, Failed, Cancelled, TimedOut }
[JsonConverter(typeof(JsonStringEnumConverter<RuntimeExecutionMode>))]
public enum RuntimeExecutionMode { Interactive }
[JsonConverter(typeof(JsonStringEnumConverter<RuntimeRunOrigin>))]
public enum RuntimeRunOrigin { Console, Api, WorkItem, Flow }
[JsonConverter(typeof(JsonStringEnumConverter<RuntimeMessageRole>))]
public enum RuntimeMessageRole { System, Developer, User, Assistant, Tool }
[JsonConverter(typeof(JsonStringEnumConverter<RuntimeRunEventKind>))]
public enum RuntimeRunEventKind { RunCreated, StatusChanged, StepStarted, StepCompleted, ResponseDelta, ToolCallStarted, ToolCallCompleted, ToolCallFailed, Metrics, Error, RunCompleted }

public sealed record RuntimeAgentReference(string ResourceId, long Version)
{
    public ResourceNamespace Namespace { get; init; } = ResourceNamespace.Default;
}
public sealed record RuntimeRunMessage(RuntimeMessageRole Role, string Content);
public sealed record RuntimeRunScope(Guid TenantId, WorkspaceId WorkspaceId, Guid PrincipalId);
public readonly record struct RuntimeRunKey(WorkspaceId WorkspaceId, string RunId);
public sealed record RuntimeRunQueueItem(RuntimeRunScope Scope, string RunId);

public sealed record RuntimeRunInput
{
    public required IReadOnlyList<RuntimeRunMessage> Messages { get; init; }
    public string? Context { get; init; }
}

public sealed record RuntimeExecutionOptions
{
    public RuntimeExecutionMode Mode { get; init; } = RuntimeExecutionMode.Interactive;
    public int TimeoutSeconds { get; init; } = 120;
    public RuntimeStreamingMode Streaming { get; init; } = RuntimeStreamingMode.Automatic;
    public IReadOnlyDictionary<string, JsonElement> Parameters { get; init; } = new Dictionary<string, JsonElement>();
}

public sealed record RuntimeRunProperties
{
    public required RuntimeAgentReference Agent { get; init; }
    public required RuntimeRunInput Input { get; init; }
    public required RuntimeExecutionOptions Execution { get; init; }
    public RuntimeRunOrigin Origin { get; init; } = RuntimeRunOrigin.Api;
    public string Initiator { get; init; } = "local-user";
    public RuntimeRunScope? Scope { get; init; }
}

public sealed record RuntimeToolCall
{
    public required string Id { get; init; }
    public required string InvocationId { get; init; }
    public required string ToolId { get; init; }
    public required string Name { get; init; }
    public required RuntimeRunState State { get; init; }
    public int Attempt { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public double? DurationMilliseconds { get; init; }
    public string? ProviderId { get; init; }
    public string? ExternalToolId { get; init; }
    public string? Arguments { get; init; }
    public string? Result { get; init; }
    public string? Error { get; init; }
    public ToolExecutionFailureKind? FailureKind { get; init; }
    public string? ErrorCode { get; init; }
    public string? CorrelationId { get; init; }
}

public sealed record RuntimeRunStatus
{
    public RuntimeRunState State { get; init; } = RuntimeRunState.Pending;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? Response { get; init; }
    public string? Error { get; init; }
    public string Runtime { get; init; } = "Local";
    public string? ModelProfile { get; init; }
    public string? ResolvedModel { get; init; }
    public string? ModelProvider { get; init; }
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public float? EffectiveTemperature { get; init; }
    public int? EffectiveMaxOutputTokens { get; init; }
    public IReadOnlyList<RuntimeToolCall> ToolCalls { get; init; } = [];
}

public sealed record RuntimeRun
{
    public required WorkspaceId WorkspaceId { get; init; }
    public required RuntimeRunScope Scope { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Type { get; init; } = RuntimeResourceTypes.Runs;
    public string ApiVersion { get; init; } = RuntimeApiVersions.V20260801;
    public required RuntimeRunProperties Properties { get; init; }
    public required RuntimeRunStatus Status { get; init; }
    public string? ETag { get; init; }
}

public sealed record RuntimeRunEvent
{
    public required WorkspaceId WorkspaceId { get; init; }
    public long Sequence { get; init; }
    public required Guid EventId { get; init; }
    public required string RunId { get; init; }
    public required RuntimeRunEventKind Kind { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public string? Step { get; init; }
    public string? Message { get; init; }
    public string? Content { get; init; }
    public RuntimeRunState? State { get; init; }
    public RuntimeToolCall? ToolCall { get; init; }
}

public sealed record StoredRuntimeRun(RuntimeRun Value, string ETag, DateTimeOffset UpdatedAt);

public interface IRuntimeRunStore
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<StoredRuntimeRun> CreateAsync(RuntimeRun run, CancellationToken cancellationToken);
    Task<StoredRuntimeRun?> GetAsync(WorkspaceId workspaceId, string runId, CancellationToken cancellationToken);
    Task<IReadOnlyList<StoredRuntimeRun>> ListAsync(WorkspaceId workspaceId, string? agentResourceId, int skip, int take, CancellationToken cancellationToken);
    Task<IReadOnlyList<RuntimeRunKey>> ListRecoverableAsync(int skip, int take, CancellationToken cancellationToken);
    Task<StoredRuntimeRun> UpdateAsync(RuntimeRun run, string expectedETag, CancellationToken cancellationToken);
    Task<RuntimeRunEvent> AppendEventAsync(RuntimeRunEvent runEvent, CancellationToken cancellationToken);
    Task<IReadOnlyList<RuntimeRunEvent>> ListEventsAsync(WorkspaceId workspaceId, string runId, long afterSequence, CancellationToken cancellationToken);
}

public sealed record RuntimeExecutionState(
    WorkspaceId WorkspaceId,
    string RunId,
    string RuntimeType,
    string StateId,
    JsonElement Payload,
    DateTimeOffset CreatedAt,
    string? ParentStateId = null);

public interface IRuntimeExecutionStateStore
{
    Task StoreAsync(RuntimeExecutionState state, CancellationToken cancellationToken);
    Task<RuntimeExecutionState?> GetAsync(WorkspaceId workspaceId, string runId, string runtimeType, string stateId, CancellationToken cancellationToken);
    Task<IReadOnlyList<RuntimeExecutionState>> ListAsync(WorkspaceId workspaceId, string runId, string runtimeType, string? parentStateId, CancellationToken cancellationToken);
    Task DeleteAsync(WorkspaceId workspaceId, string runId, string? runtimeType, CancellationToken cancellationToken);
}

public interface IRuntimeRunQueue
{
    ValueTask EnqueueAsync(RuntimeRunQueueItem item, CancellationToken cancellationToken);
    IAsyncEnumerable<RuntimeRunQueueItem> ReadAllAsync(CancellationToken cancellationToken);
}

public interface IRuntimeRunExecutionScope
{
    ValueTask ValidateAsync(RuntimeRunScope scope, CancellationToken cancellationToken);
    IDisposable Enter(RuntimeRunScope scope);
}

public interface IRuntimeRunCancellationRegistry
{
    CancellationToken Register(RuntimeRunKey key, CancellationToken stoppingToken);
    bool Cancel(RuntimeRunKey key);
    void Complete(RuntimeRunKey key);
}

public sealed class RuntimeRunNotFoundException(string runId) : Exception($"Runtime run '{runId}' was not found.");
public sealed class RuntimeRunConcurrencyException(string message) : Exception(message);
public sealed class RuntimeRunValidationException(string code, string message) : Exception(message) { public string Code { get; } = code; }

public static class RuntimeRunStateExtensions
{
    public static bool IsTerminal(this RuntimeRunState state) => state is RuntimeRunState.Succeeded or RuntimeRunState.Failed or RuntimeRunState.Cancelled or RuntimeRunState.TimedOut;
}
