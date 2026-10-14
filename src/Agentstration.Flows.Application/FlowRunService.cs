using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Resources;

namespace Agentstration.Flows.Application;

public sealed record FlowToolExecutionRequest(
    FlowRunScope Scope,
    string RunId,
    FlowId OwnerFlowId,
    string StepName,
    int Attempt,
    string CorrelationId,
    FlowToolReference Tool,
    JsonElement Arguments,
    ResolvedFlowToolRoute? Route = null);

public sealed record FlowToolExecutionResult(
    JsonElement? Output,
    string ToolName,
    ResourceNamespace ToolNamespace,
    Guid ToolUid,
    long ToolGeneration,
    string ProviderName,
    ResourceNamespace ProviderNamespace,
    string ProviderType,
    string ExternalToolId);

public interface IFlowToolExecutor
{
    Task<FlowToolExecutionResult> ExecuteAsync(FlowToolExecutionRequest request, CancellationToken cancellationToken);
}

public interface IFlowToolSetResolver
{
    Task<ResolvedFlowToolRoute> ResolveAsync(
        WorkspaceId workspaceId,
        ResourceNamespace ownerNamespace,
        ToolRouteFlowStepDefinition step,
        CancellationToken cancellationToken);
}

public sealed record FlowStepArtifactCaptureRequest(
    FlowRunScope Scope,
    string FlowRunId,
    string RootFlowRunId,
    string? ParentFlowRunId,
    string StepName,
    int Attempt,
    string? CorrelationId,
    FlowStepArtifactOutputDefinition Definition,
    JsonElement Content,
    IReadOnlyDictionary<string, string> Provenance);

public interface IFlowStepArtifactCapture
{
    Task<FlowStepArtifactReference> CaptureAsync(
        FlowStepArtifactCaptureRequest request,
        CancellationToken cancellationToken);

    Task CleanupAsync(
        FlowRunScope scope,
        string flowRunId,
        string stepName,
        FlowStepArtifactReference artifact,
        CancellationToken cancellationToken) =>
        Task.FromException(new FlowValidationException(
            "flow_step_artifact_cleanup_unavailable",
            "No governed Artifact cleanup service is configured for Flow Runs."));
}

public sealed class UnsupportedFlowStepArtifactCapture : IFlowStepArtifactCapture
{
    public static UnsupportedFlowStepArtifactCapture Instance { get; } = new();
    private UnsupportedFlowStepArtifactCapture() { }

    public Task<FlowStepArtifactReference> CaptureAsync(
        FlowStepArtifactCaptureRequest request,
        CancellationToken cancellationToken) =>
        Task.FromException<FlowStepArtifactReference>(new FlowValidationException(
            "flow_step_artifact_capture_unavailable",
            "No governed Artifact capture service is configured for Flow Runs."));
}

public sealed class UnsupportedFlowToolSetResolver : IFlowToolSetResolver
{
    public static UnsupportedFlowToolSetResolver Instance { get; } = new();
    private UnsupportedFlowToolSetResolver() { }
    public Task<ResolvedFlowToolRoute> ResolveAsync(WorkspaceId workspaceId, ResourceNamespace ownerNamespace,
        ToolRouteFlowStepDefinition step, CancellationToken cancellationToken) =>
        Task.FromException<ResolvedFlowToolRoute>(new FlowValidationException("flow_tool_set_resolver_unavailable", "No ToolSet resolver is configured for Flow Runs."));
}

public sealed class UnsupportedFlowToolExecutor : IFlowToolExecutor
{
    public static UnsupportedFlowToolExecutor Instance { get; } = new();
    private UnsupportedFlowToolExecutor() { }

    public Task<FlowToolExecutionResult> ExecuteAsync(FlowToolExecutionRequest request, CancellationToken cancellationToken) =>
        Task.FromException<FlowToolExecutionResult>(new FlowValidationException("flow_tool_executor_unavailable", "No governed Tool executor is configured for Flow Runs."));
}
public interface IFlowRunQueue
{
    ValueTask EnqueueAsync(FlowRunQueueItem item, CancellationToken cancellationToken);
    ValueTask RequestCancellationAsync(FlowRunQueueItem item, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

public sealed record FlowRunQueueItem(string RunId, FlowRunScope Scope);

public interface IFlowRunExecutionScope
{
    ValueTask ValidateAsync(FlowRunScope scope, CancellationToken cancellationToken);
    IDisposable Enter(FlowRunScope scope);
}

public interface IFlowRunCancellationRegistry
{
    CancellationToken Register(FlowRunKey key, CancellationToken stoppingToken);
    bool Cancel(FlowRunKey key);
    void Complete(FlowRunKey key);
}

public interface IFlowRunDeletionGuard
{
    Task ValidateDeleteAsync(WorkspaceId workspaceId, string runId, CancellationToken cancellationToken);
}

public interface IFlowRunEventSink
{
    Task PublishAsync(FlowRunEvent runEvent, CancellationToken cancellationToken);
}

public sealed class NullFlowRunEventSink : IFlowRunEventSink
{
    public Task PublishAsync(FlowRunEvent runEvent, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class CompositeFlowRunEventSink(IReadOnlyList<IFlowRunEventSink> sinks) : IFlowRunEventSink
{
    public async Task PublishAsync(FlowRunEvent runEvent, CancellationToken cancellationToken)
    {
        foreach (var sink in sinks)
            await sink.PublishAsync(runEvent, cancellationToken);
    }
}

public interface IFlowInputRequestSink
{
    Task PublishRequestedAsync(FlowRun run, InputRequest request, CancellationToken cancellationToken);
}

public sealed record FlowRunExecutionOptions
{
    public TimeSpan OrchestrationTimeout { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan InputRequestTimeout { get; init; } = TimeSpan.FromDays(7);
    public TimeSpan ExecutionLeaseDuration { get; init; } = TimeSpan.FromMinutes(15);
    public int MaximumNestingDepth { get; init; } = 16;
    public int MaximumDescendantRuns { get; init; } = 256;
}

public sealed record FlowRevisionUsage(
    string RevisionId,
    int ActiveRunCount,
    int WaitingForInputCount,
    int HistoricalRunCount,
    IReadOnlyList<string> ActiveRunIds,
    IReadOnlyList<FlowRevisionRunImpact> ActiveRuns);

public sealed record FlowRevisionRunImpact(
    WorkspaceId WorkspaceId,
    string RunId,
    FlowRunStatus Status,
    int PendingInputRequestCount);

public sealed class InputRequestAlreadyResolvedException(string requestId)
    : Exception($"Input Request '{requestId}' has already been resolved.");

public sealed partial class FlowRunService(
    IFlowRepository repository,
    IFlowRunQueue queue,
    IFlowRunCancellationRegistry cancellations,
    IFlowRunEventSink eventSink,
    TimeProvider timeProvider,
    FlowRunExecutionOptions? executionOptions = null,
    IFlowInputRequestSink? inputRequestSink = null,
    IEnumerable<IFlowRunDeletionGuard>? configuredRunDeletionGuards = null,
    IFlowStepArtifactCapture? configuredArtifactCapture = null,
    FlowService? flowDefinitions = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly FlowRunExecutionOptions executionOptions = executionOptions is null
        ? new()
        : executionOptions.OrchestrationTimeout > TimeSpan.Zero
          && executionOptions.InputRequestTimeout > TimeSpan.Zero
          && executionOptions.ExecutionLeaseDuration > executionOptions.OrchestrationTimeout
          && executionOptions.MaximumNestingDepth is >= 1 and <= 64
          && executionOptions.MaximumDescendantRuns is >= 1 and <= 4096
            ? executionOptions
            : throw new ArgumentOutOfRangeException(nameof(executionOptions), "Execution and input timeouts must be positive, and the execution lease must exceed the orchestration timeout.");
    private readonly IReadOnlyList<IFlowRunDeletionGuard> runDeletionGuards =
        configuredRunDeletionGuards?.ToArray() ?? [];
    private readonly IFlowStepArtifactCapture artifactCapture =
        configuredArtifactCapture ?? UnsupportedFlowStepArtifactCapture.Instance;
    private readonly FlowService? flowDefinitions = flowDefinitions;
    public static readonly ActivitySource ActivitySource = new("Agentstration.Flows");
    public static readonly Meter Meter = new("Agentstration.Flows");
    private static readonly Counter<long> RunsCreated = Meter.CreateCounter<long>("agentstration.flow.runs.created");

    private static string DefinitionSnapshotIdentity(FlowVersion version) =>
        $"{version.ScopeRef.Value}:{version.FlowId.Namespace.Value}:{version.FlowId.Value}:{version.Version}:{version.DefinitionHash ?? "legacy"}";






























































































}
