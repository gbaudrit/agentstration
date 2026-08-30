using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Agentstration.Flow.Storage.Abstractions;
using Agentstration.Resources;

namespace Agentstration.Flow.Application;

public sealed record FlowAgentExecutionResult(
    JsonElement Output,
    string AgentResourceId,
    long AgentVersion,
    string? ModelProfileResourceId,
    string? Provider,
    FlowStepRunUsage? Usage,
    IReadOnlyList<string> Tools,
    IReadOnlyList<string> Logs);

public interface IFlowAgentExecutor
{
    Task<FlowAgentExecutionResult> ExecuteAsync(FlowTargetReference target, JsonElement input, string correlationId, CancellationToken cancellationToken);
}
public interface IFlowRunQueue
{
    ValueTask EnqueueAsync(FlowRunQueueItem item, CancellationToken cancellationToken);
    IAsyncEnumerable<FlowRunQueueItem> ReadAllAsync(CancellationToken cancellationToken);
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
    IFlowAgentExecutor agents,
    IFlowOrchestrationEngine orchestrations,
    IExpressionParser expressionParser,
    IExpressionEvaluator expressions,
    IFlowRunEventSink eventSink,
    IFlowRunExecutionScope executionScope,
    TimeProvider timeProvider,
    FlowRunExecutionOptions? executionOptions = null,
    IFlowInputRequestSink? inputRequestSink = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly FlowRunExecutionOptions executionOptions = executionOptions is null
        ? new()
        : executionOptions.OrchestrationTimeout > TimeSpan.Zero
          && executionOptions.InputRequestTimeout > TimeSpan.Zero
          && executionOptions.ExecutionLeaseDuration > executionOptions.OrchestrationTimeout
            ? executionOptions
            : throw new ArgumentOutOfRangeException(nameof(executionOptions), "Execution and input timeouts must be positive, and the execution lease must exceed the orchestration timeout.");
    public static readonly ActivitySource ActivitySource = new("Agentstration.Flow");
    public static readonly Meter Meter = new("Agentstration.Flow");
    private static readonly Counter<long> RunsCreated = Meter.CreateCounter<long>("agentstration.flow.runs.created");
    private static readonly Counter<long> RunsCompleted = Meter.CreateCounter<long>("agentstration.flow.runs.completed");
    private static readonly Counter<long> RunsFailed = Meter.CreateCounter<long>("agentstration.flow.runs.failed");
    private static readonly Histogram<double> RunDuration = Meter.CreateHistogram<double>("agentstration.flow.run.duration", "s");



































































    private static (string Route, FlowResourceReference Agent, double Confidence, string Reason)? SelectRoute(RouterFlowStepDefinition router, JsonElement input)
    {
        var text = input.GetRawText();
        foreach (var candidate in router.Candidates)
            if (text.Contains(candidate.Route, StringComparison.OrdinalIgnoreCase) || (candidate.Examples?.Any(example => text.Contains(example, StringComparison.OrdinalIgnoreCase)) ?? false)) return (candidate.Route, candidate.Agent, 1, $"Input matched route '{candidate.Route}'.");
        return router.Fallback is null ? null : ("fallback", router.Fallback, .5, "No rule matched; explicit fallback selected.");
    }




























}
