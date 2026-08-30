using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Agentstration.Flow.Storage.Abstractions;
using Agentstration.Resources;

namespace Agentstration.Flow.Application;

public sealed partial class FlowRunService
{
    private async Task ExecuteGraphAsync(StoredFlowRun initial, CancellationToken stoppingToken, CancellationToken runToken)
    {
        var stored = initial;
        var graph = stored.Value.DefinitionSnapshot.Graph!;
        var outputs = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
        var currentName = graph.EntryStep;
        var executed = new HashSet<string>(StringComparer.Ordinal);
        JsonElement? finalOutput = null;
        for (var count = 0; count < graph.Steps.Count; count++)
        {
            runToken.ThrowIfCancellationRequested();
            var step = graph.Steps.Single(item => item.Name == currentName);
            if (!executed.Add(step.Name)) throw new FlowValidationException("flow_cycle_detected", $"Step '{step.Name}' was reached more than once.");
            stored = await StartStepAsync(stored, step.Name, runToken);
            var context = new FlowExecutionContext(stored.Value.Input, outputs);
            JsonElement? output;
            string eventName;
            FlowAgentExecutionResult? agentResult = null;
            FlowRunError? stepError = null;
            switch (step)
            {
                case InputFlowStepDefinition:
                    output = stored.Value.Input.Clone(); eventName = "completed"; break;
                case RouterFlowStepDefinition router:
                    var selection = SelectRoute(router, stored.Value.Input);
                    output = selection is null ? null : JsonSerializer.SerializeToElement(new { selectedRoute = selection.Value.Route, selectedAgent = selection.Value.Agent.ResourceId, confidence = selection.Value.Confidence, reason = selection.Value.Reason });
                    eventName = selection is null ? "failed" : "selected";
                    if (selection is null) stepError = new FlowRunError("router_no_route", "The Router could not select a route and has no fallback.");
                    break;
                case AgentFlowStepDefinition agent:
                    var agentId = await ResolveStringAsync(agent.Agent.ResourceId, context, runToken) ?? throw new FlowValidationException("agent_reference_unresolved", $"Agent reference for step '{step.Name}' could not be resolved.");
                    var resolvedInput = agent.InputMapping is null ? stored.Value.Input.Clone() : await ResolveJsonAsync(agent.InputMapping.Value, context, runToken);
                    try
                    {
                        agentResult = await agents.ExecuteAsync(new FlowTargetReference(FlowTargetKind.Agent, agentId, Namespace: agent.Agent.Namespace ?? stored.Value.FlowId.Namespace), resolvedInput, stored.Value.CorrelationId!, runToken);
                        output = agentResult.Output.Clone(); eventName = "completed";
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        output = JsonSerializer.SerializeToElement(new { error = exception.Message }); eventName = "failed";
                        stepError = new FlowRunError("agent_step_failed", "The Agent step failed.", exception.Message);
                    }
                    break;
                case ConditionFlowStepDefinition condition:
                    var conditionResult = await EvaluateConditionAsync(condition, context, runToken);
                    output = JsonSerializer.SerializeToElement(conditionResult); eventName = conditionResult ? "true" : "false"; break;
                case TransformFlowStepDefinition transform:
                    output = transform.Mode.Equals("Expression", StringComparison.OrdinalIgnoreCase)
                        ? await EvaluateExpressionAsync(transform.Expression!, context, runToken)
                        : transform.Mapping is null ? JsonSerializer.SerializeToElement(new { }) : await ResolveJsonAsync(transform.Mapping.Value, context, runToken);
                    eventName = "completed"; break;
                case OutputFlowStepDefinition terminal:
                    output = terminal.OutputMapping is null ? outputs.Values.LastOrDefault(value => value is not null)?.Clone() : await ResolveJsonAsync(terminal.OutputMapping.Value, context, runToken);
                    finalOutput = output; eventName = "completed"; break;
                case FailureFlowStepDefinition failure:
                    throw new FlowValidationException(failure.Code, failure.Message);
                default:
                    throw new FlowValidationException("flow_step_type_unsupported", $"Step '{step.Name}' has an unsupported type.");
            }
            outputs[step.Name] = output?.Clone();
            var transition = await SelectTransitionAsync(graph, step.Name, eventName, new FlowExecutionContext(stored.Value.Input, outputs), runToken);
            if (stepError is not null) stored = await FinishFailedStepAsync(stored, step.Name, output, transition?.Id, stepError, runToken);
            else if (agentResult is not null) stored = await FinishAgentStepAsync(stored, agentResult, runToken, transition?.Id, step.Name);
            else stored = await FinishGraphStepAsync(stored, step.Name, output, transition?.Id, runToken);
            if (step is OutputFlowStepDefinition) break;
            if (transition is null && stepError is not null)
                throw new FlowValidationException(stepError.Code, stepError.Details ?? stepError.Message);
            if (transition is null) throw new FlowValidationException("flow_transition_missing", $"No '{eventName}' transition leaves step '{step.Name}'.");
            currentName = transition.ToStep;
        }
        if (finalOutput is null) throw new FlowValidationException("flow_output_missing", "The Flow completed without reaching an Output step.");
        var now = timeProvider.GetUtcNow();
        var finalSteps = stored.Value.Steps.Select(step => step.Status == FlowStepRunStatus.NotStarted ? step with { Status = FlowStepRunStatus.Skipped, CompletedAt = now } : step).ToArray();
        await SaveAsync(stored, stored.Value with
        {
            Status = FlowRunStatus.Succeeded,
            Output = finalOutput.Value.Clone(),
            CompletedAt = now,
            Steps = finalSteps,
            ExecutionLeaseId = null,
            ExecutionLeaseExpiresAt = null
        }, stoppingToken);
        RecordCompletion(stored.Value.CreatedAt, now, stored.Value.DefinitionState);
        await EmitAsync(stored.Value.WorkspaceId, stored.Value.Id, FlowRunEventType.FlowRunCompleted, null, null, stoppingToken);
    }

    private async Task<StoredFlowRun> FinishGraphStepAsync(StoredFlowRun stored, string name, JsonElement? output, string? transition, CancellationToken token)
    {
        var now = timeProvider.GetUtcNow();
        var steps = stored.Value.Steps.Select(step => step.StepName == name ? step with { Status = FlowStepRunStatus.Succeeded, ResolvedInput = stored.Value.Input.Clone(), Output = output?.Clone(), SelectedTransition = transition, CompletedAt = now, Logs = [.. step.Logs, $"{name} completed."] } : step).ToArray();
        var updated = await SaveAsync(stored, stored.Value with { Steps = steps }, token);
        await EmitAsync(stored.Value.WorkspaceId, stored.Value.Id, FlowRunEventType.StepRunCompleted, name, JsonSerializer.SerializeToElement(new { transition }), token);
        return updated;
    }

    private async Task<StoredFlowRun> FinishFailedStepAsync(StoredFlowRun stored, string name, JsonElement? output, string? transition, FlowRunError error, CancellationToken token)
    {
        var now = timeProvider.GetUtcNow();
        var steps = stored.Value.Steps.Select(step => step.StepName == name ? step with { Status = FlowStepRunStatus.Failed, ResolvedInput = stored.Value.Input.Clone(), Output = output?.Clone(), SelectedTransition = transition, CompletedAt = now, Error = error, Logs = [.. step.Logs, $"{name} failed: {error.Message}"] } : step).ToArray();
        var updated = await SaveAsync(stored, stored.Value with { Steps = steps }, token);
        await EmitAsync(stored.Value.WorkspaceId, stored.Value.Id, FlowRunEventType.StepRunFailed, name, JsonSerializer.SerializeToElement(new { transition, error.Code, error.Message }), token);
        return updated;
    }

    private async Task<FlowTransitionDefinition?> SelectTransitionAsync(FlowGraphDefinition graph, string from, string eventName, FlowExecutionContext context, CancellationToken token)
    {
        foreach (var transition in graph.Transitions.Where(item => item.FromStep == from && item.Event.Equals(eventName, StringComparison.OrdinalIgnoreCase)).OrderBy(item => item.Priority ?? int.MaxValue))
        {
            if (transition.Condition is null) return transition;
            var evaluated = await EvaluateExpressionAsync(transition.Condition, context, token);
            if (evaluated?.ValueKind == JsonValueKind.True) return transition;
        }
        return null;
    }

    private async Task<bool> EvaluateConditionAsync(ConditionFlowStepDefinition condition, FlowExecutionContext context, CancellationToken token)
    {
        if (condition.Mode.Equals("Advanced", StringComparison.OrdinalIgnoreCase)) return (await EvaluateExpressionAsync(condition.Expression!, context, token))?.ValueKind == JsonValueKind.True;
        var left = condition.Left?.StartsWith("${", StringComparison.Ordinal) == true ? await EvaluateExpressionAsync(condition.Left, context, token) : JsonSerializer.SerializeToElement(condition.Left);
        var right = condition.Right;
        var leftText = left?.ToString() ?? string.Empty;
        return condition.Operator.ToLowerInvariant() switch
        {
            "equals" => string.Equals(leftText, right, StringComparison.OrdinalIgnoreCase),
            "not equals" => !string.Equals(leftText, right, StringComparison.OrdinalIgnoreCase),
            "contains" => leftText.Contains(right ?? string.Empty, StringComparison.OrdinalIgnoreCase),
            "starts with" => leftText.StartsWith(right ?? string.Empty, StringComparison.OrdinalIgnoreCase),
            "ends with" => leftText.EndsWith(right ?? string.Empty, StringComparison.OrdinalIgnoreCase),
            "greater than" => CompareCondition(leftText, right) > 0,
            "greater than or equal" => CompareCondition(leftText, right) >= 0,
            "less than" => CompareCondition(leftText, right) < 0,
            "less than or equal" => CompareCondition(leftText, right) <= 0,
            "is empty" => string.IsNullOrEmpty(leftText),
            "is not empty" => !string.IsNullOrEmpty(leftText),
            _ => throw new FlowValidationException("condition_operator_unsupported", $"Condition operator '{condition.Operator}' is not supported.")
        };
    }

    private static int CompareCondition(string left, string? right)
    {
        if (decimal.TryParse(left, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var leftNumber)
            && decimal.TryParse(right, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var rightNumber))
            return leftNumber.CompareTo(rightNumber);
        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<JsonElement?> EvaluateExpressionAsync(string source, FlowExecutionContext context, CancellationToken token)
    {
        var parsed = expressionParser.Parse(source);
        if (!parsed.IsValid) throw new FlowValidationException("expression_invalid", parsed.Error!);
        return await expressions.EvaluateAsync(parsed.Expression!, context, token);
    }

    private async Task<string?> ResolveStringAsync(string source, FlowExecutionContext context, CancellationToken token) => source.StartsWith("${", StringComparison.Ordinal)
        ? (await EvaluateExpressionAsync(source, context, token))?.ToString()
        : source;

    private async Task<JsonElement> ResolveJsonAsync(JsonElement value, FlowExecutionContext context, CancellationToken token)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!;
            if (text.StartsWith("${", StringComparison.Ordinal) && text.EndsWith('}')) return (await EvaluateExpressionAsync(text, context, token))?.Clone() ?? JsonSerializer.SerializeToElement<object?>(null);
            return value.Clone();
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject()) result[property.Name] = await ResolveJsonAsync(property.Value, context, token);
            return JsonSerializer.SerializeToElement(result);
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            var result = new List<JsonElement>(); foreach (var item in value.EnumerateArray()) result.Add(await ResolveJsonAsync(item, context, token)); return JsonSerializer.SerializeToElement(result);
        }
        return value.Clone();
    }

    private static JsonElement? StepDeclaredInput(FlowStepDefinition step) => step switch { AgentFlowStepDefinition agent => agent.InputMapping?.Clone(), TransformFlowStepDefinition transform => transform.Mapping?.Clone(), OutputFlowStepDefinition output => output.OutputMapping?.Clone(), _ => null };
}

