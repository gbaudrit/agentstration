using System.Text.Json;
using System.Text.RegularExpressions;

namespace Agentstration.Flow.Application;

using Agentstration.Resources;

public sealed record FlowValidationContext(
    bool ResolveResources = true,
    WorkspaceId? WorkspaceId = null,
    FlowId? OwnerFlowId = null);

public sealed record ResolvedFlowCall(
    FlowId FlowId,
    string Version,
    JsonElement? InputSchema,
    JsonElement? OutputSchema);

public sealed record ResolvedFlowTool(
    string ResourceId,
    ResourceNamespace Namespace,
    JsonElement InputSchema,
    JsonElement? OutputSchema,
    bool Enabled,
    bool Available,
    bool RequiresApproval);

public interface IFlowDefinitionValidator
{
    ValueTask<FlowValidationResult> ValidateAsync(FlowGraphDefinition definition, FlowValidationContext context, CancellationToken cancellationToken);
}

public interface IFlowResourceReferenceResolver
{
    Task<bool> ExistsAsync(string resourceId, CancellationToken cancellationToken);
    Task<ResolvedFlowCall?> ResolveFlowAsync(
        WorkspaceId workspaceId,
        ResourceNamespace ownerNamespace,
        FlowCallReference reference,
        CancellationToken cancellationToken) => Task.FromResult<ResolvedFlowCall?>(null);

    Task<bool> CreatesFlowCycleAsync(
        WorkspaceId workspaceId,
        FlowId ownerFlowId,
        ResolvedFlowCall target,
        CancellationToken cancellationToken) => Task.FromResult(false);

    Task<ResolvedFlowTool?> ResolveToolAsync(
        WorkspaceId workspaceId,
        ResourceNamespace ownerNamespace,
        FlowToolReference reference,
        CancellationToken cancellationToken) => Task.FromResult<ResolvedFlowTool?>(null);
}

public sealed partial class FlowGraphValidator(IFlowResourceReferenceResolver resources) : IFlowDefinitionValidator
{
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    public async ValueTask<FlowValidationResult> ValidateAsync(FlowGraphDefinition definition, FlowValidationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var issues = new List<FlowValidationIssue>();
        var steps = new Dictionary<string, FlowStepDefinition>(StringComparer.Ordinal);
        foreach (var step in definition.Steps)
        {
            if (!NamePattern().IsMatch(step.Name)) issues.Add(Error("step_name_invalid", "Step names must contain letters, digits, '-' or '_'.", step.Name, property: "name"));
            if (!steps.TryAdd(step.Name, step)) issues.Add(Error("step_name_duplicate", $"Step '{step.Name}' is duplicated.", step.Name));
            await ValidateStepAsync(step, context, issues, cancellationToken);
        }

        if (!steps.ContainsKey(definition.EntryStep)) issues.Add(Error("entry_step_unknown", "The entry step does not exist.", property: "entryStep"));
        if (definition.Steps.Count(step => step is InputFlowStepDefinition) != 1) issues.Add(Error("input_step_required", "A Flow requires exactly one Input step."));
        if (!definition.Steps.Any(step => step is OutputFlowStepDefinition or FailureFlowStepDefinition)) issues.Add(Error("terminal_step_required", "A Flow requires an Output or Failure terminal step."));

        var transitionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var transition in definition.Transitions)
        {
            if (!transitionIds.Add(transition.Id)) issues.Add(Error("transition_id_duplicate", $"Transition '{transition.Id}' is duplicated.", transitionId: transition.Id));
            if (!steps.ContainsKey(transition.FromStep)) issues.Add(Error("transition_source_unknown", $"Transition source '{transition.FromStep}' does not exist.", transitionId: transition.Id));
            if (!steps.ContainsKey(transition.ToStep)) issues.Add(Error("transition_target_unknown", $"Transition target '{transition.ToStep}' does not exist.", transitionId: transition.Id));
            if (!string.IsNullOrWhiteSpace(transition.Condition)) ValidateExpression(transition.Condition, issues, transition.FromStep, transition.Id, "condition");
        }

        if (steps.ContainsKey(definition.EntryStep))
        {
            var reachable = Reachable(definition.EntryStep, definition.Transitions);
            foreach (var step in steps.Keys.Where(name => !reachable.Contains(name)))
                issues.Add(new FlowValidationIssue("step_unreachable", FlowValidationSeverity.Warning, $"Step '{step}' is unreachable.", step));
            if (HasCycle(definition.EntryStep, definition.Transitions)) issues.Add(Error("flow_cycle_not_supported", "Cycles are not supported in this execution increment."));
        }

        foreach (var terminal in definition.Steps.Where(step => step is OutputFlowStepDefinition or FailureFlowStepDefinition))
            if (definition.Transitions.Any(transition => transition.FromStep == terminal.Name)) issues.Add(Error("terminal_has_transition", $"Terminal step '{terminal.Name}' cannot have outgoing transitions.", terminal.Name));

        return new FlowValidationResult(issues);
    }

    private async Task ValidateStepAsync(FlowStepDefinition step, FlowValidationContext context, List<FlowValidationIssue> issues, CancellationToken token)
    {
        switch (step)
        {
            case AgentFlowStepDefinition agent:
                await ValidateResourceAsync(agent.Agent.ResourceId, step.Name, "agent.resourceId", context, issues, token);
                ValidateJsonExpressions(agent.InputMapping, issues, step.Name, "inputMapping");
                break;
            case RouterFlowStepDefinition router:
                if (router.Candidates.Count == 0) issues.Add(Error("router_candidates_required", "A Router requires at least one candidate.", step.Name));
                if (router.Fallback is null) issues.Add(new FlowValidationIssue("router_fallback_recommended", FlowValidationSeverity.Warning, "Configure an explicit Router fallback.", step.Name));
                var routes = new HashSet<string>(StringComparer.Ordinal);
                foreach (var candidate in router.Candidates)
                {
                    if (!NamePattern().IsMatch(candidate.Route)) issues.Add(Error("router_route_invalid", "Route keys must contain letters, digits, '-' or '_'.", step.Name, property: "candidates.route"));
                    if (!routes.Add(candidate.Route)) issues.Add(Error("router_route_duplicate", $"Route '{candidate.Route}' is duplicated.", step.Name));
                    await ValidateResourceAsync(candidate.Agent.ResourceId, step.Name, "candidates.agent.resourceId", context, issues, token);
                }
                if (router.Fallback is not null) await ValidateResourceAsync(router.Fallback.ResourceId, step.Name, "fallback.resourceId", context, issues, token);
                break;
            case ConditionFlowStepDefinition condition:
                if (condition.Mode.Equals("Advanced", StringComparison.OrdinalIgnoreCase)) ValidateExpression(condition.Expression, issues, step.Name, property: "expression");
                else if (string.IsNullOrWhiteSpace(condition.Left)) issues.Add(Error("condition_left_required", "A simple Condition requires a left value.", step.Name, property: "left"));
                break;
            case TransformFlowStepDefinition transform:
                if (transform.Mode.Equals("Expression", StringComparison.OrdinalIgnoreCase)) ValidateExpression(transform.Expression, issues, step.Name, property: "expression");
                else ValidateJsonExpressions(transform.Mapping, issues, step.Name, "mapping");
                break;
            case FlowCallStepDefinition flowCall:
                await ValidateFlowCallAsync(flowCall, context, issues, token);
                break;
            case ToolFlowStepDefinition tool:
                await ValidateToolAsync(tool, context, issues, token);
                break;
            case OutputFlowStepDefinition output:
                ValidateJsonExpressions(output.OutputMapping, issues, step.Name, "outputMapping");
                break;
        }
    }

    private async Task ValidateToolAsync(
        ToolFlowStepDefinition step,
        FlowValidationContext context,
        List<FlowValidationIssue> issues,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(step.Tool.ResourceId) || step.Tool.ResourceId.Contains('/', StringComparison.Ordinal))
        {
            issues.Add(Error("tool_reference_invalid", "Tool references must use a logical Tool name.", step.Name, property: "tool.resourceId"));
            return;
        }

        ValidateJsonExpressions(step.ArgumentsMapping, issues, step.Name, "argumentsMapping");
        if (!context.ResolveResources || context.WorkspaceId is null || context.OwnerFlowId is null) return;

        var target = await resources.ResolveToolAsync(context.WorkspaceId.Value, context.OwnerFlowId.Value.Namespace, step.Tool, token);
        if (target is null)
        {
            issues.Add(Error("tool_resource_not_found", $"Tool '{step.Tool.ResourceId}' was not found.", step.Name, property: "tool"));
            return;
        }
        if (!target.Enabled)
            issues.Add(Error("tool_disabled", $"Tool '{step.Tool.ResourceId}' is disabled.", step.Name, property: "tool"));
        if (!target.Available)
            issues.Add(Error("tool_unavailable", $"Tool '{step.Tool.ResourceId}' is unavailable from its provider.", step.Name, property: "tool"));
        if (!ValidContractSchema(target.InputSchema))
        {
            issues.Add(Error("tool_input_schema_invalid", "The selected Tool has an invalid or unsupported input schema.", step.Name, property: "tool.inputSchema"));
            return;
        }
        ValidateMappingAgainstSchema(step.Name, "argumentsMapping", step.ArgumentsMapping, target.InputSchema, "tool_argument", issues);
    }

    private async Task ValidateFlowCallAsync(
        FlowCallStepDefinition step,
        FlowValidationContext context,
        List<FlowValidationIssue> issues,
        CancellationToken token)
    {
        var reference = step.Flow;
        if (string.IsNullOrWhiteSpace(reference.ResourceId) || reference.ResourceId.Contains('/', StringComparison.Ordinal))
        {
            issues.Add(Error("flow_reference_invalid", "Flow references must use a logical Flow name.", step.Name, property: "flow.resourceId"));
            return;
        }

        if (reference.VersionStrategy == FlowCallVersionStrategy.Exact && string.IsNullOrWhiteSpace(reference.Version))
            issues.Add(Error("flow_version_required", "An exact Flow reference requires a version.", step.Name, property: "flow.version"));
        if (reference.VersionStrategy == FlowCallVersionStrategy.Active && reference.Version is not null)
            issues.Add(Error("flow_active_version_must_be_implicit", "An active Flow reference cannot declare an exact version.", step.Name, property: "flow.version"));

        ValidateJsonExpressions(step.InputMapping, issues, step.Name, "inputMapping");
        if (!context.ResolveResources || context.WorkspaceId is null || context.OwnerFlowId is null) return;

        var target = await resources.ResolveFlowAsync(context.WorkspaceId.Value, context.OwnerFlowId.Value.Namespace, reference, token);
        if (target is null)
        {
            issues.Add(Error("flow_resource_not_found", $"Published Flow '{reference.ResourceId}' was not found for the selected version strategy.", step.Name, property: "flow"));
            return;
        }

        if (!ValidContractSchema(target.InputSchema))
            issues.Add(Error("flow_input_schema_invalid", "The selected Flow has an invalid or unsupported input schema.", step.Name, property: "flow.inputSchema"));
        if (!ValidContractSchema(target.OutputSchema))
            issues.Add(Error("flow_output_schema_invalid", "The selected Flow has an invalid or unsupported output schema.", step.Name, property: "flow.outputSchema"));
        ValidateMappingAgainstSchema(step, target.InputSchema, issues);
        if (await resources.CreatesFlowCycleAsync(context.WorkspaceId.Value, context.OwnerFlowId.Value, target, token))
            issues.Add(Error("flow_dependency_cycle", $"Calling Flow '{target.FlowId}' would create a direct or indirect dependency cycle.", step.Name, property: "flow"));
    }

    private static void ValidateMappingAgainstSchema(FlowCallStepDefinition step, JsonElement? schema, List<FlowValidationIssue> issues)
    {
        if (schema is not { ValueKind: JsonValueKind.Object } value) return;
        ValidateMappingAgainstSchema(step.Name, "inputMapping", step.InputMapping, value, "flow_input_mapping", issues);
    }

    private static void ValidateMappingAgainstSchema(
        string stepName,
        string propertyPath,
        JsonElement? candidate,
        JsonElement schema,
        string codePrefix,
        List<FlowValidationIssue> issues)
    {
        if (candidate is not { ValueKind: JsonValueKind.Object } mapping)
        {
            issues.Add(Error($"{codePrefix}_object_required", "The selected resource requires an object mapping.", stepName, property: propertyPath));
            return;
        }

        var allowed = schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object
            ? properties.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal)
            : [];
        foreach (var property in mapping.EnumerateObject().Where(property => !allowed.Contains(property.Name)))
            issues.Add(Error($"{codePrefix}_unknown", $"Mapping property '{property.Name}' is not declared by the selected resource.", stepName, property: $"{propertyPath}.{property.Name}"));

        if (!schema.TryGetProperty("required", out var required) || required.ValueKind != JsonValueKind.Array) return;
        var mapped = mapping.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var property in required.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).Where(property => !mapped.Contains(property)))
            issues.Add(Error($"{codePrefix}_required", $"Required property '{property}' is not mapped.", stepName, property: $"{propertyPath}.{property}"));
    }

    private static bool ValidContractSchema(JsonElement? schema)
    {
        if (schema is null) return true;
        if (schema.Value.ValueKind != JsonValueKind.Object) return false;
        if (schema.Value.TryGetProperty("type", out var type)
            && (type.ValueKind != JsonValueKind.String || !string.Equals(type.GetString(), "object", StringComparison.Ordinal))) return false;
        return !schema.Value.TryGetProperty("properties", out var properties) || properties.ValueKind == JsonValueKind.Object;
    }

    private async Task ValidateResourceAsync(string resourceId, string step, string property, FlowValidationContext context, List<FlowValidationIssue> issues, CancellationToken token)
    {
        if (resourceId.StartsWith("${", StringComparison.Ordinal))
        {
            ValidateExpression(resourceId, issues, step, property: property);
            return;
        }
        if (string.IsNullOrWhiteSpace(resourceId) || resourceId.Contains('/', StringComparison.Ordinal))
        {
            issues.Add(Error("agent_reference_invalid", "Agent references must use a logical Agent name.", step, property: property));
            return;
        }
        if (context.ResolveResources && !await resources.ExistsAsync(resourceId, token)) issues.Add(Error("agent_resource_not_found", $"Agent '{resourceId}' was not found.", step, property: property));
    }

    private static void ValidateJsonExpressions(JsonElement? value, List<FlowValidationIssue> issues, string step, string property)
    {
        if (value is null) return;
        foreach (var expression in EnumerateStrings(value.Value).Where(text => text.Contains("${", StringComparison.Ordinal))) ValidateExpression(expression, issues, step, property: property);
    }

    private static IEnumerable<string> EnumerateStrings(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) { yield return value.GetString()!; yield break; }
        if (value.ValueKind == JsonValueKind.Object) foreach (var property in value.EnumerateObject()) foreach (var text in EnumerateStrings(property.Value)) yield return text;
        if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) foreach (var text in EnumerateStrings(item)) yield return text;
    }

    private static void ValidateExpression(string? expression, List<FlowValidationIssue> issues, string? step = null, string? transitionId = null, string? property = null)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            issues.Add(Error("expression_invalid", "An expression is required.", step, transitionId, property));
            return;
        }
        if (!FlowExpressionParser.TryParse(expression, out _, out var error)) issues.Add(Error("expression_invalid", error!, step, transitionId, property));
    }

    private static HashSet<string> Reachable(string entry, IReadOnlyList<FlowTransitionDefinition> transitions)
    {
        var result = new HashSet<string>(StringComparer.Ordinal) { entry };
        var queue = new Queue<string>(); queue.Enqueue(entry);
        while (queue.TryDequeue(out var current)) foreach (var next in transitions.Where(item => item.FromStep == current).Select(item => item.ToStep)) if (result.Add(next)) queue.Enqueue(next);
        return result;
    }

    private static bool HasCycle(string entry, IReadOnlyList<FlowTransitionDefinition> transitions)
    {
        var visiting = new HashSet<string>(StringComparer.Ordinal); var visited = new HashSet<string>(StringComparer.Ordinal);
        bool Visit(string node)
        {
            if (!visiting.Add(node)) return true;
            if (visited.Contains(node)) { visiting.Remove(node); return false; }
            foreach (var next in transitions.Where(item => item.FromStep == node).Select(item => item.ToStep)) if (Visit(next)) return true;
            visiting.Remove(node); visited.Add(node); return false;
        }
        return Visit(entry);
    }

    private static FlowValidationIssue Error(string code, string message, string? step = null, string? transitionId = null, string? property = null) =>
        new(code, FlowValidationSeverity.Error, message, step, transitionId, property);
}

public sealed record ParsedExpression(string Source, string Body);
public sealed record ExpressionParseResult(ParsedExpression? Expression, string? Error) { public bool IsValid => Expression is not null; }
public sealed record ExpressionValidationResult(bool IsValid, string? Error = null);
public sealed record FlowExpressionContext(IReadOnlyCollection<string> StepNames);
public sealed record FlowExecutionContext(JsonElement Input, IReadOnlyDictionary<string, JsonElement?> StepOutputs);

public interface IExpressionParser { ExpressionParseResult Parse(string expression); }
public interface IExpressionValidator { ExpressionValidationResult Validate(ParsedExpression expression, FlowExpressionContext context); }
public interface IExpressionEvaluator { ValueTask<JsonElement?> EvaluateAsync(ParsedExpression expression, FlowExecutionContext context, CancellationToken cancellationToken); }

public sealed class FlowExpressionParser : IExpressionParser, IExpressionValidator, IExpressionEvaluator
{
    public ExpressionParseResult Parse(string expression) => TryParse(expression, out var parsed, out var error) ? new(parsed, null) : new(null, error);

    public ExpressionValidationResult Validate(ParsedExpression expression, FlowExpressionContext context)
    {
        var path = ComparisonParts(expression.Body)[0];
        if (path.StartsWith("steps.", StringComparison.Ordinal))
        {
            var segments = path.Split('.');
            if (segments.Length < 3 || !context.StepNames.Contains(segments[1])) return new(false, $"Expression references unknown step '{(segments.Length > 1 ? segments[1] : path)}'.");
        }
        return new(true);
    }

    public ValueTask<JsonElement?> EvaluateAsync(ParsedExpression expression, FlowExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var parts = ComparisonParts(expression.Body);
        var left = Resolve(parts[0], context);
        if (parts.Length == 1) return ValueTask.FromResult(left);
        var right = ResolveLiteral(parts[2]);
        var result = Compare(left, parts[1], right);
        return ValueTask.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(result));
    }

    internal static bool TryParse(string expression, out ParsedExpression parsed, out string? error)
    {
        parsed = null!; error = null;
        if (string.IsNullOrWhiteSpace(expression) || !expression.StartsWith("${", StringComparison.Ordinal) || !expression.EndsWith('}')) { error = "Expressions must use '${...}'."; return false; }
        var body = expression[2..^1].Trim();
        if (body.Length == 0 || body.Contains(';') || body.Contains('(') || body.Contains(')')) { error = "The expression contains unsupported syntax."; return false; }
        var first = ComparisonParts(body)[0];
        if (!first.StartsWith("input", StringComparison.Ordinal) && !first.StartsWith("steps.", StringComparison.Ordinal)) { error = "Expressions may reference only input or step outputs."; return false; }
        parsed = new ParsedExpression(expression, body); return true;
    }

    private static string[] ComparisonParts(string body)
    {
        foreach (var op in new[] { ">=", "<=", "!=", "==", ">", "<" })
        {
            var index = body.IndexOf(op, StringComparison.Ordinal);
            if (index > 0) return [body[..index].Trim(), op, body[(index + op.Length)..].Trim()];
        }
        return [body.Trim()];
    }

    private static JsonElement? Resolve(string path, FlowExecutionContext context)
    {
        var segments = path.Split('.'); JsonElement? current;
        var offset = 1;
        if (segments[0] == "input") current = context.Input;
        else { if (segments.Length < 3 || !context.StepOutputs.TryGetValue(segments[1], out current)) return null; offset = segments[2] == "output" ? 3 : 2; }
        for (var index = offset; index < segments.Length; index++)
        {
            if (current is null || current.Value.ValueKind != JsonValueKind.Object || !current.Value.TryGetProperty(segments[index], out var property)) return null;
            current = property;
        }
        return current;
    }

    private static JsonElement ResolveLiteral(string value)
    {
        if ((value.StartsWith('"') && value.EndsWith('"')) || (value.StartsWith('\'') && value.EndsWith('\''))) return JsonSerializer.SerializeToElement(value[1..^1]);
        if (bool.TryParse(value, out var boolean)) return JsonSerializer.SerializeToElement(boolean);
        if (decimal.TryParse(value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var number)) return JsonSerializer.SerializeToElement(number);
        return JsonSerializer.SerializeToElement(value);
    }

    private static bool Compare(JsonElement? left, string op, JsonElement right)
    {
        if (left is null) return op == "!=";
        if (left.Value.ValueKind == JsonValueKind.Number && right.ValueKind == JsonValueKind.Number && left.Value.TryGetDecimal(out var l) && right.TryGetDecimal(out var r)) return op switch { "==" => l == r, "!=" => l != r, ">" => l > r, ">=" => l >= r, "<" => l < r, "<=" => l <= r, _ => false };
        var ls = left.Value.ToString(); var rs = right.ToString(); var comparison = string.Compare(ls, rs, StringComparison.OrdinalIgnoreCase);
        return op switch { "==" => comparison == 0, "!=" => comparison != 0, ">" => comparison > 0, ">=" => comparison >= 0, "<" => comparison < 0, "<=" => comparison <= 0, _ => false };
    }
}
