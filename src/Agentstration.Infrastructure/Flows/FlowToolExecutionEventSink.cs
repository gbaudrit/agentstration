using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.Flow;
using Agentstration.Flow.Application;
using Agentstration.Flow.Storage.Abstractions;
using Agentstration.Runtime.Abstractions;

namespace Agentstration.Infrastructure.Flows;

public sealed class FlowToolExecutionEventSink : IToolExecutionEventSink
{
    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly IFlowRepository runs;
    private readonly IFlowRunEventSink eventSink;
    private readonly ToolExecutionCaptureOptions captureOptions;

    public FlowToolExecutionEventSink(IFlowRepository runs, IFlowRunEventSink eventSink)
        : this(runs, eventSink, new ToolExecutionCaptureOptions()) { }

    public FlowToolExecutionEventSink(
        IFlowRepository runs,
        IFlowRunEventSink eventSink,
        ToolExecutionCaptureOptions captureOptions)
    {
        this.runs = runs;
        this.eventSink = eventSink;
        this.captureOptions = captureOptions;
    }

    public async ValueTask PublishAsync(
        ToolExecutionLifecycleEvent executionEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executionEvent);
        var context = executionEvent.Context;
        if (context.OwnerKind != ToolExecutionOwnerKind.FlowRun)
            return;
        if (context.WorkspaceId is not { } workspaceId || string.IsNullOrWhiteSpace(context.RunId))
            throw new InvalidOperationException("A Flow Run tool execution requires its Workspace and Run identities.");

        var runEvent = await runs.AppendRunEventAsync(new FlowRunEvent(
            workspaceId,
            context.RunId,
            0,
            EventType(executionEvent),
            null,
            Payload(executionEvent),
            executionEvent.Timestamp), cancellationToken);
        await eventSink.PublishAsync(runEvent, cancellationToken);
    }

    private static FlowRunEventType EventType(ToolExecutionLifecycleEvent executionEvent) => executionEvent switch
    {
        ToolExecutionStarted => FlowRunEventType.ToolCallStarted,
        ToolExecutionGovernanceEvaluated => FlowRunEventType.ToolCallGovernanceEvaluated,
        ToolExecutionCompleted => FlowRunEventType.ToolCallCompleted,
        ToolExecutionFailed => FlowRunEventType.ToolCallFailed,
        _ => throw new ArgumentOutOfRangeException(nameof(executionEvent))
    };

    private JsonElement Payload(ToolExecutionLifecycleEvent executionEvent)
    {
        var context = executionEvent.Context;
        return JsonSerializer.SerializeToElement(new
        {
            context.ToolCallId,
            context.InvocationId,
            context.ToolId,
            context.ToolName,
            ProviderId = context.ToolProviderId,
            context.ExternalToolId,
            context.AgentId,
            context.AgentVersion,
            context.AgentGeneration,
            context.AgentRevisionId,
            context.CorrelationId,
            Arguments = captureOptions.CaptureArguments(context.Arguments, context.PersistArguments),
            Governance = executionEvent is ToolExecutionGovernanceEvaluated governance
                ? governance.Evaluations
                : null,
            Outcome = executionEvent switch
            {
                ToolExecutionStarted => "running",
                ToolExecutionGovernanceEvaluated => "governed",
                ToolExecutionCompleted => "succeeded",
                ToolExecutionFailed failed when failed.Cancelled => "cancelled",
                ToolExecutionFailed failed when failed.FailureKind == ToolExecutionFailureKind.Denied => "denied",
                ToolExecutionFailed => "failed",
                _ => "unknown"
            },
            DurationMilliseconds = executionEvent switch
            {
                ToolExecutionCompleted completed => completed.Duration.TotalMilliseconds,
                ToolExecutionFailed failed => failed.Duration.TotalMilliseconds,
                _ => (double?)null
            },
            ErrorType = (executionEvent as ToolExecutionFailed)?.ErrorType,
            ErrorCode = (executionEvent as ToolExecutionFailed)?.ErrorCode,
            FailureKind = (executionEvent as ToolExecutionFailed)?.FailureKind.ToString(),
            Error = (executionEvent as ToolExecutionFailed)?.ErrorMessage
        }, PayloadJsonOptions);
    }
}
