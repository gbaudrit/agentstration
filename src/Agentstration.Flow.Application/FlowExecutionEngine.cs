using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Agentstration.Flow.Application;

public sealed record FlowOrchestrationExecutionRequest(
    string RunId,
    OrchestrationFlowDefinition Definition,
    JsonElement Input,
    string CorrelationId);

public abstract record FlowExecutionEvent;

public sealed record FlowParticipantTurnStarted(string ParticipantId, int Turn) : FlowExecutionEvent;

public sealed record FlowParticipantDelta(string ParticipantId, string Content) : FlowExecutionEvent;

public sealed record FlowParticipantTurnCompleted(string ParticipantId, int Turn) : FlowExecutionEvent;

public sealed record FlowParticipantCompleted(string ParticipantId, JsonElement Output) : FlowExecutionEvent;

public sealed record FlowExecutionCompleted(JsonElement Output) : FlowExecutionEvent;

public interface IFlowOrchestrationEngine
{
    IAsyncEnumerable<FlowExecutionEvent> ExecuteAsync(
        FlowOrchestrationExecutionRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class UnsupportedFlowOrchestrationEngine : IFlowOrchestrationEngine
{
    public async IAsyncEnumerable<FlowExecutionEvent> ExecuteAsync(
        FlowOrchestrationExecutionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        throw new FlowValidationException(
            "flow_orchestration_engine_unavailable",
            $"No execution engine is configured for '{request.Definition.Strategy}' orchestration.");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }
}
