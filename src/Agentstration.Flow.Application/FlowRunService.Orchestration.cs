using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Agentstration.Flow.Storage.Abstractions;
using Agentstration.Resources;

namespace Agentstration.Flow.Application;

public sealed partial class FlowRunService
{
    private async Task ExecuteOrchestrationAsync(
        StoredFlowRun initial,
        OrchestrationFlowDefinition definition,
        InputRequest? answeredInput,
        CancellationToken stoppingToken,
        CancellationToken runToken)
    {
        var stored = initial.Value.RuntimeState is null
            ? await CompleteSimpleStepAsync(initial, "Input", initial.Value.Input, null, runToken)
            : initial;
        var started = new HashSet<string>(StringComparer.Ordinal);
        FlowOrchestrationResult? result = null;
        var suspended = false;
        var request = new FlowOrchestrationExecutionRequest(
            stored.Value.WorkspaceId,
            stored.Value.Id,
            QualifyOrchestrationTargets(definition, stored.Value.FlowId.Namespace),
            stored.Value.Input,
            stored.Value.CorrelationId!,
            stored.Value.RuntimeBindings,
            stored.Value.RuntimeState,
            answeredInput,
            stored.Value.Scope);

        await foreach (var executionEvent in orchestrations.ExecuteAsync(request, runToken))
        {
            switch (executionEvent)
            {
                case FlowRuntimeBindingsResolved resolved:
                    if (stored.Value.RuntimeBindings.Count == 0)
                        stored = await SaveAsync(stored, stored.Value with { RuntimeBindings = resolved.Bindings }, runToken);
                    else if (!stored.Value.RuntimeBindings.SequenceEqual(resolved.Bindings))
                        throw new FlowValidationException("flow_runtime_binding_changed", "The runtime attempted to change immutable bindings for an existing Flow Run.");
                    break;
                case FlowExternalInputRequested input:
                    var existing = (await repository.ListInputRequestsAsync(stored.Value.WorkspaceId, stored.Value.Id, null, runToken))
                        .FirstOrDefault(value => string.Equals(value.Value.RuntimeRequestId, input.RuntimeRequestId, StringComparison.Ordinal));
                    var inputRequest = existing?.Value ?? new InputRequest
                    {
                        WorkspaceId = stored.Value.WorkspaceId,
                        Id = $"input-{Guid.NewGuid():N}",
                        RunId = stored.Value.Id,
                        Source = input.Source,
                        RuntimeRequestId = input.RuntimeRequestId,
                        Prompt = input.Prompt,
                        Type = input.Type,
                        Options = input.Options,
                        CreatedAt = timeProvider.GetUtcNow(),
                        ExpiresAt = timeProvider.GetUtcNow() + executionOptions.InputRequestTimeout
                    };
                    if (existing is null) await repository.CreateInputRequestAsync(inputRequest, runToken);
                    stored = await SaveAsync(stored, stored.Value with
                    {
                        Status = FlowRunStatus.WaitingForInput,
                        RuntimeState = input.RuntimeState,
                        ExecutionLeaseId = null,
                        ExecutionLeaseExpiresAt = null
                    }, runToken);
                    await EmitAsync(stored.Value.WorkspaceId, stored.Value.Id, FlowRunEventType.InputRequested, input.Source,
                        JsonSerializer.SerializeToElement(new { inputRequest.Id, inputRequest.Prompt, inputRequest.Type, inputRequest.ExpiresAt }), runToken);
                    if (inputRequestSink is not null)
                        await inputRequestSink.PublishRequestedAsync(stored.Value, inputRequest, runToken);
                    suspended = true;
                    break;
                case FlowParticipantTurnStarted turn:
                    if (started.Add(turn.ParticipantId))
                        stored = await StartStepAsync(stored, turn.ParticipantId, runToken);
                    await EmitAsync(stored.Value.WorkspaceId, stored.Value.Id, FlowRunEventType.ParticipantTurnStarted, turn.ParticipantId,
                        JsonSerializer.SerializeToElement(new { turn = turn.Turn }), runToken);
                    break;
                case FlowParticipantDelta delta:
                    if (started.Add(delta.ParticipantId))
                        stored = await StartStepAsync(stored, delta.ParticipantId, runToken);
                    await EmitAsync(stored.Value.WorkspaceId, stored.Value.Id, FlowRunEventType.StepOutputDelta, delta.ParticipantId,
                        JsonSerializer.SerializeToElement(new { content = delta.Content }), runToken);
                    break;
                case FlowParticipantTurnCompleted turn:
                    await EmitAsync(stored.Value.WorkspaceId, stored.Value.Id, FlowRunEventType.ParticipantTurnCompleted, turn.ParticipantId,
                        JsonSerializer.SerializeToElement(new { turn = turn.Turn }), runToken);
                    break;
                case FlowParticipantHandoff handoff:
                    await EmitAsync(stored.Value.WorkspaceId, stored.Value.Id, FlowRunEventType.ParticipantHandoff, handoff.FromParticipantId,
                        JsonSerializer.SerializeToElement(new { from = handoff.FromParticipantId, to = handoff.ToParticipantId }), runToken);
                    break;
                case FlowParticipantCompleted completed:
                    if (started.Add(completed.Result.ParticipantId))
                        stored = await StartStepAsync(stored, completed.Result.ParticipantId, runToken);
                    stored = await FinishParticipantStepAsync(stored, completed.Result, runToken);
                    break;
                case FlowExecutionCompleted completed:
                    result = completed.Result;
                    break;
            }
        }

        if (suspended) return;
        if (result is null)
            throw new FlowValidationException("flow_orchestration_output_missing", "The orchestration completed without a final output.");
        var output = JsonSerializer.SerializeToElement(result, JsonOptions);

        var now = timeProvider.GetUtcNow();
        var skipped = stored.Value.Steps.Select(step => step.Status == FlowStepRunStatus.NotStarted && step.StepType == "agent"
            ? step with { Status = FlowStepRunStatus.Skipped, CompletedAt = now }
            : step).ToArray();
        stored = await SaveAsync(stored, stored.Value with { Steps = skipped }, runToken);
        stored = await CompleteSimpleStepAsync(stored, "Output", output, null, runToken);
        await SaveAsync(stored, stored.Value with
        {
            Status = FlowRunStatus.Succeeded,
            Output = output.Clone(),
            CompletedAt = now,
            ExecutionLeaseId = null,
            ExecutionLeaseExpiresAt = null
        }, stoppingToken);
        RecordCompletion(stored.Value.CreatedAt, now, stored.Value.DefinitionState);
        await EmitAsync(stored.Value.WorkspaceId, stored.Value.Id, FlowRunEventType.FlowRunCompleted, null, null, stoppingToken);
    }

    private static OrchestrationFlowDefinition QualifyOrchestrationTargets(
        OrchestrationFlowDefinition definition,
        ResourceNamespace ownerNamespace)
    {
        var participants = definition.Participants
            .Select(participant => participant.Namespace is null
                ? participant with { Namespace = ownerNamespace }
                : participant)
            .ToArray();
        var pattern = definition.Pattern is MagenticOrchestrationPattern magentic && magentic.Manager.Namespace is null
            ? magentic with { Manager = magentic.Manager with { Namespace = ownerNamespace } }
            : definition.Pattern;
        return definition with { Participants = participants, Pattern = pattern };
    }

    private async Task<StoredFlowRun> FinishParticipantStepAsync(
        StoredFlowRun stored,
        FlowParticipantResult result,
        CancellationToken token)
    {
        var now = timeProvider.GetUtcNow();
        var steps = stored.Value.Steps.Select(step => step.StepName == result.ParticipantId ? step with
        {
            Status = FlowStepRunStatus.Succeeded,
            ResolvedInput = stored.Value.Input.Clone(),
            Output = result.Output.Clone(),
            CompletedAt = now,
            AgentResourceId = result.AgentResourceId,
            AgentVersion = result.AgentVersion,
            ModelProfileResourceId = result.ModelProfileResourceId,
            Provider = result.Provider,
            Tools = result.Tools,
            Usage = result.Usage,
            Logs = [.. step.Logs, $"{result.ParticipantId} completed after {result.Turns.Count} turn(s)."]
        } : step).ToArray();
        var updated = await SaveAsync(stored, stored.Value with { Steps = steps }, token);
        await EmitAsync(stored.Value.WorkspaceId, stored.Value.Id, FlowRunEventType.StepRunCompleted, result.ParticipantId,
            JsonSerializer.SerializeToElement(new { turns = result.Turns.Count }), token);
        return updated;
    }
}

