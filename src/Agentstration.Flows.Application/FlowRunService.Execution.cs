using Agentstration.Secrets.Abstractions;
using System.Diagnostics;
using System.Text.Json;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Resources;

namespace Agentstration.Flows.Application;

public sealed partial class FlowRunService
{
    private static IReadOnlyList<FlowStepRun> CreateSteps(FlowVersion version, JsonElement input)
    {
        if (version.Graph is not null) return version.Graph.Steps.Select(step => new FlowStepRun { StepName = step.Name, StepType = step.Type(), DeclaredInput = step is InputFlowStepDefinition ? input.Clone() : StepDeclaredInput(step) }).ToArray();
        if (version.Definition is OrchestrationFlowDefinition orchestration)
        {
            return
            [
                new FlowStepRun { StepName = "Input", StepType = "input", DeclaredInput = input.Clone() },
                .. orchestration.Participants.Select(participant => new FlowStepRun
                {
                    StepName = participant.Id,
                    StepType = "agent",
                    DeclaredInput = input.Clone(),
                    AgentResourceId = participant.Id
                }),
                new FlowStepRun { StepName = "Output", StepType = "output" }
            ];
        }
        var steps = new List<FlowStepRun> { new() { StepName = "Input", StepType = "input", DeclaredInput = input.Clone() } };
        if (version.Definition is RoutingFlowDefinition) steps.Add(new() { StepName = "Router", StepType = "router", DeclaredInput = input.Clone() });
        steps.Add(new() { StepName = "Agent", StepType = "agent", DeclaredInput = input.Clone() });
        steps.Add(new() { StepName = "Output", StepType = "output" });
        return steps;
    }

    public static void ValidateInput(JsonElement? schema, JsonElement input, IReadOnlyList<SecretInputReference>? secretInputs = null)
    {
        try { Agentstration.Schemas.DataContracts.Validate(Agentstration.Schemas.DataContracts.Declared(schema, "flow-input"), input, "flow-input", isProtectedString: secretInputs is { Count: > 0 } ? value => SecretInputs.IsReference(value, secretInputs) : null); }
        catch (Agentstration.Schemas.DataContractException exception)
        {
            var code = exception.Failure.Rule == "json_schema_instance_required" ? "flow_input_required"
                : exception.Failure.Path == "$" && exception.Failure.Expected == "object" ? "flow_input_invalid" : "flow_input_type_invalid";
            throw new FlowValidationException(code, exception.Message) { ContractFailure = exception.Failure };
        }
    }

    private static void ValidateInputResponse(InputRequest request, JsonElement value)
    {
        switch (request.Type)
        {
            case InputRequestType.Text when value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()):
                throw new FlowValidationException("input_response_invalid", "A non-empty text response is required.");
            case InputRequestType.Choice when value.ValueKind != JsonValueKind.String || !request.Options.Contains(value.GetString()!, StringComparer.Ordinal):
                throw new FlowValidationException("input_response_invalid", "The response must be one of the available choices.");
            case InputRequestType.Confirmation when value.ValueKind is not JsonValueKind.True and not JsonValueKind.False:
                throw new FlowValidationException("input_response_invalid", "A boolean confirmation response is required.");
        }
    }

    private async Task FailAsync(ResourceScopeRef executionScopeRef, string runId, FlowRunStatus status, string code, string message, CancellationToken token, string? details = null)
    {
        var stored = await RequiredExactAsync(executionScopeRef, runId, token);
        if (stored.Value.Status.IsTerminal()) return;
        var now = timeProvider.GetUtcNow();
        var error = new FlowRunError(code, message, details);
        var steps = stored.Value.Steps.Select(step => step.Status == FlowStepRunStatus.Running
            ? step with { Status = status == FlowRunStatus.Cancelled ? FlowStepRunStatus.Cancelled : FlowStepRunStatus.Failed, CompletedAt = now, Error = error }
            : step).ToArray();
        var terminal = await SaveAsync(stored, stored.Value with
        {
            Status = status,
            CompletedAt = now,
            Error = error,
            Steps = steps,
            ExecutionLeaseId = null,
            ExecutionLeaseExpiresAt = null
        }, token);
        await TryCleanupStepArtifactsAsync(terminal.Value, token);
        var eventType = status switch
        {
            FlowRunStatus.Cancelled => FlowRunEventType.FlowRunCancelled,
            FlowRunStatus.TimedOut => FlowRunEventType.FlowRunTimedOut,
            _ => FlowRunEventType.FlowRunFailed
        };
        await EmitAsync(executionScopeRef, runId, eventType,
            steps.FirstOrDefault(step => step.Status == FlowStepRunStatus.Failed)?.StepName, JsonSerializer.SerializeToElement(error), token);
    }

    private async Task<StoredFlowRun> SaveAsync(StoredFlowRun stored, FlowRun value, CancellationToken token)
    {
        if (value.Status.IsTerminal())
            value = value with { TokenUsage = await tokenUsageConsolidator.FinalizeAsync(value, token) };
        return await repository.UpdateRunAsync(value, stored.ETag, token);
    }

    private async Task CleanupStepArtifactsAsync(FlowRun run, FlowGraphDefinition graph, CancellationToken cancellationToken)
    {
        foreach (var stepRun in run.Steps)
        {
            var definition = graph.Steps.Single(step => step.Name == stepRun.StepName).ArtifactOutput;
            if (definition is null || definition.Clean == FlowStepArtifactCleanupMode.Never) continue;
            foreach (var artifact in stepRun.Artifacts)
                await artifactCapture.CleanupAsync(run.Scope, run.Id, stepRun.StepName, artifact, cancellationToken);
        }
    }

    private async Task TryCleanupStepArtifactsAsync(FlowRun run, CancellationToken cancellationToken)
    {
        if (run.DefinitionSnapshot.Graph is not { } graph) return;
        try
        {
            await CleanupStepArtifactsAsync(run, graph, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Activity.Current?.AddEvent(new ActivityEvent("flow.artifact.cleanup.failed", tags:
                new ActivityTagsCollection
                {
                    ["flow.run.id"] = run.Id,
                    ["error.type"] = exception.GetType().FullName,
                    ["error.message"] = exception.Message
                }));
        }
    }

    private async Task EmitAsync(ResourceScopeRef executionScopeRef, string runId, FlowRunEventType type, string? stepId, JsonElement? payload, CancellationToken token)
    {
        var runEvent = await repository.AppendRunEventAsync(new FlowRunEvent(executionScopeRef, runId, 0, type, stepId, payload?.Clone(), timeProvider.GetUtcNow()), token);
        try
        {
            await eventSink.PublishAsync(runEvent, token);
        }
        finally
        {
            if (type is FlowRunEventType.FlowRunCompleted or FlowRunEventType.FlowRunFailed or FlowRunEventType.FlowRunCancelled or FlowRunEventType.FlowRunTimedOut)
            {
                var terminal = await repository.GetRunExactAsync(executionScopeRef, runId, token);
                if (terminal is not null) await ResumeParentAfterChildAsync(terminal.Value, token);
            }
        }
    }

    private static JsonElement? StepDeclaredInput(FlowStepDefinition step) => step switch
    {
        AgentFlowStepDefinition agent => agent.InputMapping?.Clone(),
        FlowCallStepDefinition flow => flow.InputMapping?.Clone(),
        RepeatFlowStepDefinition repeat => repeat.InputMapping?.Clone(),
        ToolFlowStepDefinition tool => tool.ArgumentsMapping?.Clone(),
        ToolRouteFlowStepDefinition route => route.ArgumentsMapping?.Clone(),
        TransformFlowStepDefinition transform => transform.Mapping?.Clone(),
        OutputFlowStepDefinition output => output.OutputMapping?.Clone(),
        _ => null
    };
}

