using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Agentstration.Flow.Storage.Abstractions;
using Agentstration.Resources;

namespace Agentstration.Flow.Application;

public sealed partial class FlowRunService
{
    public async Task ExecuteAsync(FlowRunQueueItem item, CancellationToken stoppingToken)
    {
        var runId = item.RunId;
        var workspaceId = item.Scope.WorkspaceId;
        var stored = await RequiredAsync(workspaceId, runId, stoppingToken);
        if (stored.Value.Scope != item.Scope || stored.Value.WorkspaceId != workspaceId)
            throw new FlowValidationException("flow_run_scope_mismatch", "The queued Flow execution scope does not match the persisted Run.");
        if (stored.Value.Status.IsTerminal()) return;
        var now = timeProvider.GetUtcNow();
        if (stored.Value.Status == FlowRunStatus.Running && stored.Value.ExecutionLeaseExpiresAt > now) return;
        var wasWaiting = stored.Value.Status == FlowRunStatus.WaitingForInput;
        StoredInputRequest? answeredInput = null;
        if (wasWaiting)
        {
            answeredInput = (await repository.ListInputRequestsAsync(workspaceId, runId, InputRequestStatus.Answered, stoppingToken)).LastOrDefault();
            if (answeredInput is null) return;
        }
        using var activity = ActivitySource.StartActivity("flow.run.execute", ActivityKind.Internal);
        activity?.SetTag("flow.id", stored.Value.FlowId.Value);
        activity?.SetTag("flow.run.id", runId);
        activity?.SetTag("agentstration.workspace.id", workspaceId.ToString());
        activity?.SetTag("flow.version", stored.Value.FlowVersion);
        activity?.SetTag("flow.definition.state", stored.Value.DefinitionState.ToString());
        var key = new FlowRunKey(workspaceId, runId);
        var runToken = cancellations.Register(key, stoppingToken);
        using var timeout = stored.Value.DefinitionSnapshot.Definition is OrchestrationFlowDefinition
            ? new CancellationTokenSource(executionOptions.OrchestrationTimeout, timeProvider)
            : null;
        using var executionTimeoutLink = timeout is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(runToken, timeout.Token);
        var executionToken = executionTimeoutLink?.Token ?? runToken;
        IDisposable? activeExecutionScope = null;
        try
        {
            await executionScope.ValidateAsync(stored.Value.Scope, stoppingToken);
            activeExecutionScope = executionScope.Enter(stored.Value.Scope);
            try
            {
                stored = await SaveAsync(stored, stored.Value with
                {
                    Status = FlowRunStatus.Running,
                    StartedAt = stored.Value.StartedAt ?? now,
                    ExecutionLeaseId = Guid.NewGuid().ToString("N"),
                    ExecutionLeaseExpiresAt = now + executionOptions.ExecutionLeaseDuration
                }, stoppingToken);
            }
            catch (FlowConcurrencyException)
            {
                return;
            }
            await EmitAsync(workspaceId, runId, wasWaiting ? FlowRunEventType.FlowRunResumed : FlowRunEventType.FlowRunStarted, null, null, stoppingToken);
            if (stored.Value.DefinitionSnapshot.Graph is not null)
            {
                await ExecuteGraphAsync(stored, stoppingToken, runToken);
                return;
            }
            if (stored.Value.DefinitionSnapshot.Definition is OrchestrationFlowDefinition orchestration)
            {
                await ExecuteOrchestrationAsync(stored, orchestration, answeredInput?.Value, stoppingToken, executionToken);
                return;
            }
            stored = await CompleteSimpleStepAsync(stored, "Input", stored.Value.Input, null, runToken);

            var target = SelectTarget(stored.Value.DefinitionSnapshot.Definition, stored.Value.Input);
            if (stored.Value.DefinitionSnapshot.Definition is RoutingFlowDefinition)
            {
                stored = await CompleteSimpleStepAsync(stored, "Router", JsonSerializer.SerializeToElement(new { selectedAgent = target.Id }), target.Id, runToken);
            }

            stored = await StartStepAsync(stored, "Agent", runToken);
            var execution = await agents.ExecuteAsync(target with { Namespace = target.Namespace ?? stored.Value.FlowId.Namespace }, stored.Value.Input, stored.Value.CorrelationId!, runToken);
            stored = await FinishAgentStepAsync(stored, execution, runToken);
            stored = await CompleteSimpleStepAsync(stored, "Output", execution.Output, null, runToken);
            var completedAt = timeProvider.GetUtcNow();
            await SaveAsync(stored, stored.Value with
            {
                Status = FlowRunStatus.Succeeded,
                Output = execution.Output.Clone(),
                CompletedAt = completedAt,
                ExecutionLeaseId = null,
                ExecutionLeaseExpiresAt = null
            }, stoppingToken);
            RecordCompletion(stored.Value.CreatedAt, completedAt, stored.Value.DefinitionState);
            await EmitAsync(workspaceId, runId, FlowRunEventType.FlowRunCompleted, null, null, stoppingToken);
        }
        catch (OperationCanceledException) when (timeout?.IsCancellationRequested == true && !runToken.IsCancellationRequested)
        {
            await FailAsync(workspaceId, runId, FlowRunStatus.TimedOut, "flow_run_timed_out", "The Flow Run exceeded its execution timeout.", stoppingToken);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            await FailAsync(workspaceId, runId, FlowRunStatus.Cancelled, "flow_run_cancelled", "The Flow Run was cancelled.", stoppingToken);
        }
        catch (FlowValidationException exception)
        {
            await FailAsync(workspaceId, runId, FlowRunStatus.Failed, exception.Code, exception.Message, stoppingToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await FailAsync(workspaceId, runId, FlowRunStatus.Failed, "flow_run_execution_failed", "The Flow Run could not complete.", stoppingToken, exception.Message);
        }
        finally
        {
            activeExecutionScope?.Dispose();
            cancellations.Complete(key);
        }
    }

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

    private static void ValidateInput(JsonElement? schema, JsonElement input)
    {
        if (schema is null || schema.Value.ValueKind != JsonValueKind.Object) return;
        if (schema.Value.TryGetProperty("type", out var rootType) && rootType.GetString() == "object" && input.ValueKind != JsonValueKind.Object)
            throw new FlowValidationException("flow_input_invalid", "Flow input must be a JSON object.");
        if (input.ValueKind != JsonValueKind.Object) return;
        if (schema.Value.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var property in required.EnumerateArray().Select(item => item.GetString()).Where(name => !string.IsNullOrWhiteSpace(name)))
                if (!input.TryGetProperty(property!, out _)) throw new FlowValidationException("flow_input_required", $"Flow input property '{property}' is required.");
        }
        if (!schema.Value.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object) return;
        foreach (var propertySchema in properties.EnumerateObject())
        {
            if (!input.TryGetProperty(propertySchema.Name, out var value) || !propertySchema.Value.TryGetProperty("type", out var type)) continue;
            var valid = type.GetString() switch
            {
                "string" => value.ValueKind == JsonValueKind.String,
                "number" => value.ValueKind == JsonValueKind.Number,
                "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "object" => value.ValueKind == JsonValueKind.Object,
                "array" => value.ValueKind == JsonValueKind.Array,
                _ => true
            };
            if (!valid) throw new FlowValidationException("flow_input_type_invalid", $"Flow input property '{propertySchema.Name}' has an invalid type.");
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

    private static void RecordCompletion(DateTimeOffset createdAt, DateTimeOffset completedAt, FlowDefinitionState definitionState)
    {
        var tag = new KeyValuePair<string, object?>("flow.definition.state", definitionState.ToString());
        RunsCompleted.Add(1, tag);
        RunDuration.Record(Math.Max(0, (completedAt - createdAt).TotalSeconds), tag);
    }

    private static FlowTargetReference SelectTarget(FlowDefinition definition, JsonElement input) => definition switch
    {
        DirectFlowDefinition direct => direct.Target,
        RoutingFlowDefinition routing => routing.Destinations.FirstOrDefault(destination => InputContains(input, destination.Id))
            ?? routing.Fallback ?? routing.Destinations[0],
        _ => throw new FlowValidationException("flow_run_kind_unsupported", "This first execution increment supports Direct and Routing Flows.")
    };

    private static bool InputContains(JsonElement input, string targetId) =>
        input.GetRawText().Contains(targetId, StringComparison.OrdinalIgnoreCase)
        || targetId.Split('-', StringSplitOptions.RemoveEmptyEntries).Any(part => part.Length > 2 && input.GetRawText().Contains(part, StringComparison.OrdinalIgnoreCase));

    private async Task<StoredFlowRun> CompleteSimpleStepAsync(StoredFlowRun stored, string name, JsonElement output, string? transition, CancellationToken token)
    {
        stored = await StartStepAsync(stored, name, token);
        var now = timeProvider.GetUtcNow();
        var steps = stored.Value.Steps.Select(step => step.StepName == name ? step with
        {
            Status = FlowStepRunStatus.Succeeded,
            ResolvedInput = stored.Value.Input.Clone(),
            Output = output.Clone(),
            SelectedTransition = transition,
            CompletedAt = now,
            Logs = [.. step.Logs, $"{name} completed."]
        } : step).ToArray();
        var updated = await SaveAsync(stored, stored.Value with { Steps = steps }, token);
        await EmitAsync(stored.Value.WorkspaceId, stored.Value.Id, FlowRunEventType.StepRunCompleted, name, JsonSerializer.SerializeToElement(new { transition }), token);
        return updated;
    }

    private async Task<StoredFlowRun> StartStepAsync(StoredFlowRun stored, string name, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var now = timeProvider.GetUtcNow();
        var steps = stored.Value.Steps.Select(step => step.StepName == name ? step with
        {
            Status = FlowStepRunStatus.Running,
            StartedAt = now,
            Attempt = step.Attempt + 1,
            Logs = [.. step.Logs, $"{name} started."]
        } : step).ToArray();
        var updated = await SaveAsync(stored, stored.Value with { Steps = steps }, token);
        await EmitAsync(stored.Value.WorkspaceId, stored.Value.Id, FlowRunEventType.StepRunStarted, name, null, token);
        return updated;
    }

    private async Task<StoredFlowRun> FinishAgentStepAsync(StoredFlowRun stored, FlowAgentExecutionResult execution, CancellationToken token, string? selectedTransition = null, string stepName = "Agent")
    {
        var now = timeProvider.GetUtcNow();
        var steps = stored.Value.Steps.Select(step => step.StepName == stepName ? step with
        {
            Status = FlowStepRunStatus.Succeeded,
            ResolvedInput = stored.Value.Input.Clone(),
            Output = execution.Output.Clone(),
            SelectedTransition = selectedTransition,
            CompletedAt = now,
            AgentResourceId = execution.AgentResourceId,
            AgentVersion = execution.AgentVersion,
            ModelProfileResourceId = execution.ModelProfileResourceId,
            Provider = execution.Provider,
            Usage = execution.Usage,
            Tools = execution.Tools,
            Logs = [.. step.Logs, .. execution.Logs, "Agent completed."]
        } : step).ToArray();
        var updated = await SaveAsync(stored, stored.Value with { Steps = steps }, token);
        await EmitAsync(stored.Value.WorkspaceId, stored.Value.Id, FlowRunEventType.StepRunCompleted, stepName, JsonSerializer.SerializeToElement(new { selectedTransition }), token);
        return updated;
    }

    private async Task FailAsync(WorkspaceId workspaceId, string runId, FlowRunStatus status, string code, string message, CancellationToken token, string? details = null)
    {
        var stored = await RequiredAsync(workspaceId, runId, token);
        if (stored.Value.Status.IsTerminal()) return;
        var now = timeProvider.GetUtcNow();
        var error = new FlowRunError(code, message, details);
        var steps = stored.Value.Steps.Select(step => step.Status == FlowStepRunStatus.Running
            ? step with { Status = status == FlowRunStatus.Cancelled ? FlowStepRunStatus.Cancelled : FlowStepRunStatus.Failed, CompletedAt = now, Error = error }
            : step).ToArray();
        await SaveAsync(stored, stored.Value with
        {
            Status = status,
            CompletedAt = now,
            Error = error,
            Steps = steps,
            ExecutionLeaseId = null,
            ExecutionLeaseExpiresAt = null
        }, token);
        if (status is FlowRunStatus.Failed or FlowRunStatus.TimedOut)
            RunsFailed.Add(1, new KeyValuePair<string, object?>("flow.definition.state", stored.Value.DefinitionState.ToString()));
        RunDuration.Record(Math.Max(0, (now - stored.Value.CreatedAt).TotalSeconds), new KeyValuePair<string, object?>("flow.status", status.ToString()));
        var eventType = status switch
        {
            FlowRunStatus.Cancelled => FlowRunEventType.FlowRunCancelled,
            FlowRunStatus.TimedOut => FlowRunEventType.FlowRunTimedOut,
            _ => FlowRunEventType.FlowRunFailed
        };
        await EmitAsync(workspaceId, runId, eventType,
            steps.FirstOrDefault(step => step.Status == FlowStepRunStatus.Failed)?.StepName, JsonSerializer.SerializeToElement(error), token);
    }

    private Task<StoredFlowRun> SaveAsync(StoredFlowRun stored, FlowRun value, CancellationToken token) => repository.UpdateRunAsync(value, stored.ETag, token);

    private async Task EmitAsync(WorkspaceId workspaceId, string runId, FlowRunEventType type, string? stepId, JsonElement? payload, CancellationToken token)
    {
        var runEvent = await repository.AppendRunEventAsync(new FlowRunEvent(workspaceId, runId, 0, type, stepId, payload?.Clone(), timeProvider.GetUtcNow()), token);
        await eventSink.PublishAsync(runEvent, token);
    }
}

