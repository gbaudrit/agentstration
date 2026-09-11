using Agentstration.Flow;
using Agentstration.Runtime.Abstractions;
using Agentstration.Web.Components.Models;

namespace Agentstration.Web.Console;

public sealed class HttpAgentstrationEventStream(
    IAgentRunnerRuntimeClient runtime,
    IFlowApiClient flows,
    ILogger<HttpAgentstrationEventStream> logger) : IAgentstrationEventStream
{
    private const int RecentRunLimitPerSource = 10;
    private const int EventLimit = 200;

    public async Task<IReadOnlyList<EventListItem>> GetRecentEventsAsync(CancellationToken cancellationToken)
    {
        var runtimeRunsTask = TryLoadAsync("Runtime Runs", token => runtime.GetRunsAsync(null, token), cancellationToken);
        var flowRunsTask = TryLoadAsync("Flow Runs", token => flows.GetFlowRunsAsync(null, token), cancellationToken);
        await Task.WhenAll(runtimeRunsTask, flowRunsTask);

        var runtimeRunsResult = await runtimeRunsTask;
        var flowRunsResult = await flowRunsTask;
        if (!runtimeRunsResult.Available && !flowRunsResult.Available)
            throw new AgentstrationApiException("Runtime and Flow run activity are unavailable.", Guid.NewGuid().ToString("N"));

        var runtimeRuns = runtimeRunsResult.Value.OrderByDescending(run => run.Status.CreatedAt).Take(RecentRunLimitPerSource).ToArray();
        var flowRuns = flowRunsResult.Value.OrderByDescending(run => run.CreatedAt).Take(RecentRunLimitPerSource).ToArray();
        var runtimeEventTasks = runtimeRuns.Select(run => LoadRuntimeEventsAsync(run, cancellationToken));
        var flowEventTasks = flowRuns.Select(run => LoadFlowEventsAsync(run, cancellationToken));

        var runtimeEvents = (await Task.WhenAll(runtimeEventTasks)).SelectMany(events => events);
        var flowEvents = (await Task.WhenAll(flowEventTasks)).SelectMany(events => events);
        return runtimeEvents.Concat(flowEvents).OrderByDescending(runEvent => runEvent.Timestamp).Take(EventLimit).ToArray();
    }

    private async Task<IReadOnlyList<EventListItem>> LoadRuntimeEventsAsync(RuntimeRun run, CancellationToken cancellationToken)
    {
        var result = await TryLoadAsync($"Runtime Run {run.Id}", token => runtime.GetRunEventsAsync(run.Id, 0, token), cancellationToken);
        return result.Value
            .Where(runEvent => runEvent.Kind != RuntimeRunEventKind.ResponseDelta)
            .Select(runEvent => FromRuntime(run, runEvent))
            .ToArray();
    }

    private async Task<IReadOnlyList<EventListItem>> LoadFlowEventsAsync(FlowRun run, CancellationToken cancellationToken)
    {
        var result = await TryLoadAsync($"Flow Run {run.Id}", token => flows.GetFlowRunEventsAsync(run.Id, 0, token), cancellationToken);
        return result.Value
            .Where(runEvent => runEvent.Type != FlowRunEventType.StepOutputDelta)
            .Select(runEvent => FromFlow(run, runEvent))
            .ToArray();
    }

    private async Task<LoadResult<T>> TryLoadAsync<T>(string source, Func<CancellationToken, Task<IReadOnlyList<T>>> load, CancellationToken cancellationToken)
    {
        try
        {
            return new(await load(cancellationToken), true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Run event source {RunEventSource} is unavailable", source);
            return new(Array.Empty<T>(), false);
        }
    }

    internal static EventListItem FromRuntime(RuntimeRun run, RuntimeRunEvent runEvent) => new(
        runEvent.Timestamp,
        RuntimeLevel(runEvent),
        "Runtime",
        runEvent.Kind.ToString(),
        RuntimeSummary(run, runEvent),
        run.Id,
        Url: $"/runs/{Uri.EscapeDataString(run.Id)}");

    internal static EventListItem FromFlow(FlowRun run, FlowRunEvent runEvent) => new(
        runEvent.Timestamp,
        FlowLevel(runEvent.Type),
        "Flow",
        runEvent.Type.ToString(),
        FlowSummary(run, runEvent),
        run.Id,
        Url: $"/flow-runs/{Uri.EscapeDataString(run.Id)}");

    private static string RuntimeLevel(RuntimeRunEvent runEvent) => runEvent.Kind switch
    {
        RuntimeRunEventKind.Error or RuntimeRunEventKind.ToolCallFailed => "Error",
        RuntimeRunEventKind.RunCompleted when runEvent.State == RuntimeRunState.Failed => "Error",
        RuntimeRunEventKind.StatusChanged when runEvent.State == RuntimeRunState.Failed => "Error",
        RuntimeRunEventKind.RunCompleted or RuntimeRunEventKind.StatusChanged
            when runEvent.State is RuntimeRunState.Cancelled or RuntimeRunState.TimedOut => "Warning",
        _ => "Information"
    };

    private static string FlowLevel(FlowRunEventType type) => type switch
    {
        FlowRunEventType.FlowRunFailed or FlowRunEventType.StepRunFailed or FlowRunEventType.ToolCallFailed => "Error",
        FlowRunEventType.FlowRunCancelled or FlowRunEventType.FlowRunTimedOut or FlowRunEventType.InputExpired => "Warning",
        _ => "Information"
    };

    private static string RuntimeSummary(RuntimeRun run, RuntimeRunEvent runEvent) => runEvent.Kind switch
    {
        RuntimeRunEventKind.RunCreated => $"Run created for {run.Properties.Agent.ResourceId}",
        RuntimeRunEventKind.StatusChanged => $"Run status changed to {runEvent.State}",
        RuntimeRunEventKind.StepStarted => $"{runEvent.Step ?? "Runtime step"} started",
        RuntimeRunEventKind.StepCompleted => $"{runEvent.Step ?? "Runtime step"} completed",
        RuntimeRunEventKind.ToolCallStarted => $"Tool call {runEvent.ToolCall?.Name ?? "started"}",
        RuntimeRunEventKind.ToolCallGovernanceEvaluated => "Tool call governance evaluated",
        RuntimeRunEventKind.ToolCallCompleted => $"Tool call {runEvent.ToolCall?.Name ?? "completed"}",
        RuntimeRunEventKind.ToolCallFailed => $"Tool call {runEvent.ToolCall?.Name ?? "failed"}",
        RuntimeRunEventKind.Metrics => "Runtime metrics recorded",
        RuntimeRunEventKind.Error => "Runtime run failed",
        RuntimeRunEventKind.RunCompleted => $"Run completed with status {runEvent.State ?? run.Status.State}",
        _ => runEvent.Kind.ToString()
    };

    private static string FlowSummary(FlowRun run, FlowRunEvent runEvent)
    {
        var subject = string.IsNullOrWhiteSpace(runEvent.StepId) ? run.FlowId.Value : runEvent.StepId;
        return runEvent.Type switch
        {
            FlowRunEventType.FlowRunCreated => $"Flow run created for {run.FlowId.Value}",
            FlowRunEventType.FlowRunStarted => $"Flow run started for {run.FlowId.Value}",
            FlowRunEventType.FlowRunResumed => $"Flow run resumed for {run.FlowId.Value}",
            FlowRunEventType.FlowRunCompleted => $"Flow run completed for {run.FlowId.Value}",
            FlowRunEventType.FlowRunFailed => $"Flow run failed for {run.FlowId.Value}",
            FlowRunEventType.FlowRunCancelled => $"Flow run cancelled for {run.FlowId.Value}",
            FlowRunEventType.FlowRunTimedOut => $"Flow run timed out for {run.FlowId.Value}",
            FlowRunEventType.StepRunStarted => $"{subject} started",
            FlowRunEventType.StepRunCompleted => $"{subject} completed",
            FlowRunEventType.StepRunFailed => $"{subject} failed",
            FlowRunEventType.ParticipantTurnStarted => $"{subject} started a turn",
            FlowRunEventType.ParticipantTurnCompleted => $"{subject} completed a turn",
            FlowRunEventType.ParticipantHandoff => $"{subject} handed off the conversation",
            FlowRunEventType.InputRequested => $"{subject} requested input",
            FlowRunEventType.InputReceived => $"{subject} received input",
            FlowRunEventType.InputExpired => $"{subject} input request expired",
            FlowRunEventType.ToolCallStarted => $"{subject} started a tool call",
            FlowRunEventType.ToolCallGovernanceEvaluated => $"{subject} tool governance evaluated",
            FlowRunEventType.ToolCallCompleted => $"{subject} completed a tool call",
            FlowRunEventType.ToolCallFailed => $"{subject} tool call failed",
            _ => runEvent.Type.ToString()
        };
    }

    private sealed record LoadResult<T>(IReadOnlyList<T> Value, bool Available);
}

