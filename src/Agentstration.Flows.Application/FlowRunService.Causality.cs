using System.Globalization;
using System.Text.Json;

namespace Agentstration.Flows.Application;

public sealed partial class FlowRunService
{
    public async Task<FlowRunCausalityPage> GetCausalityAsync(
        string runId,
        int skip,
        int take,
        FlowRunScope scope,
        CancellationToken cancellationToken)
    {
        if (skip < 0) throw new FlowValidationException("flow_run_causality_skip_invalid", "Causality pagination offset cannot be negative.");
        if (take is < 1 or > 100) throw new FlowValidationException("flow_run_causality_take_invalid", "Causality page size must be between 1 and 100.");

        var requested = await RequiredAsync(runId, scope, cancellationToken);
        var rootId = requested.Value.RootFlowRunId ?? requested.Value.Id;
        var root = string.Equals(rootId, requested.Value.Id, StringComparison.Ordinal)
            ? requested
            : await RequiredAsync(rootId, scope, cancellationToken);
        var flattened = await FlattenCausalityAsync(root.Value, scope, cancellationToken);
        var page = flattened.Skip(skip).Take(take).ToArray();
        return new FlowRunCausalityPage(
            new FlowRunCausalityOrigin(
                root.Value.Id,
                root.Value.InvocationOrigin,
                root.Value.Trigger,
                root.Value.CallerId,
                root.Value.CausationId,
                root.Value.CorrelationId,
                root.Value.WorkItemResourceId),
            page,
            flattened.Count,
            skip + page.Length < flattened.Count);
    }

    private async Task<IReadOnlyList<FlowRunCausalityNode>> FlattenCausalityAsync(
        FlowRun root,
        FlowRunScope scope,
        CancellationToken cancellationToken)
    {
        var result = new List<FlowRunCausalityNode>();
        var queue = new Queue<(FlowRun Run, string? ParentStepName)>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        queue.Enqueue((root, null));

        while (queue.TryDequeue(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(current.Run.Id)) continue;
            var events = await ListEventsAsync(scope, current.Run.Id, 0, cancellationToken);
            result.Add(ProjectCausalityNode(current.Run, current.ParentStepName, events));

            foreach (var step in current.Run.Steps.Where(value => !string.IsNullOrWhiteSpace(value.ChildFlowRunId)))
            {
                var child = await GetAsync(step.ChildFlowRunId!, scope, cancellationToken);
                if (child is null
                    || child.Value.ParentFlowRunId != current.Run.Id
                    || child.Value.RootFlowRunId != root.Id
                    || child.Value.NestingDepth != current.Run.NestingDepth + 1)
                    continue;
                queue.Enqueue((child.Value, step.StepName));
            }
        }

        return result;
    }

    private static FlowRunCausalityNode ProjectCausalityNode(
        FlowRun run,
        string? parentStepName,
        IReadOnlyList<FlowRunEvent> events)
    {
        var agents = run.Steps
            .Where(step => !string.IsNullOrWhiteSpace(step.AgentResourceId))
            .Select(step => new FlowRunAgentExecution(
                step.StepName,
                step.Status,
                step.AgentResourceId!,
                step.AgentVersion,
                step.ModelProfileResourceId,
                step.Provider,
                step.StartedAt,
                step.CompletedAt,
                step.Error?.Code))
            .ToArray();
        return new FlowRunCausalityNode(
            run.Id,
            run.FlowId,
            run.FlowVersion,
            run.ResolvedFromActiveReference,
            run.DefinitionState,
            run.Status,
            run.ParentFlowRunId,
            parentStepName,
            run.NestingDepth,
            run.CreatedAt,
            run.StartedAt,
            run.CompletedAt,
            run.Error?.Code,
            agents,
            ProjectToolCalls(events));
    }

    private static IReadOnlyList<FlowRunToolCall> ProjectToolCalls(IReadOnlyList<FlowRunEvent> events)
    {
        var toolEvents = events
            .Where(value => value.Type is FlowRunEventType.ToolCallStarted
                or FlowRunEventType.ToolCallGovernanceEvaluated
                or FlowRunEventType.ToolCallCompleted
                or FlowRunEventType.ToolCallFailed)
            .Select(value => new ToolEvent(value, Property(value.Payload, "ToolCallId"), Property(value.Payload, "InvocationId")))
            .Where(value => !string.IsNullOrWhiteSpace(value.ToolCallId) && !string.IsNullOrWhiteSpace(value.InvocationId))
            .ToArray();

        return toolEvents
            .GroupBy(value => value.ToolCallId!, StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.OrderBy(value => value.Event.Sequence).First();
                var last = group.OrderBy(value => value.Event.Sequence).Last();
                var attempts = group
                    .GroupBy(value => value.InvocationId!, StringComparer.Ordinal)
                    .Select((attempt, index) => ProjectAttempt(attempt, index + 1))
                    .OrderBy(value => value.Attempt)
                    .ThenBy(value => value.StartedAt)
                    .ToArray();
                return new FlowRunToolCall(
                    group.Key,
                    first.Event.StepId ?? Property(first.Event.Payload, "FlowStepId"),
                    Property(first.Event.Payload, "ToolId"),
                    Property(first.Event.Payload, "ToolNamespace"),
                    Property(first.Event.Payload, "ToolName"),
                    Property(first.Event.Payload, "ProviderId"),
                    Property(first.Event.Payload, "ProviderNamespace"),
                    Property(first.Event.Payload, "ExternalToolId"),
                    Property(last.Event.Payload, "Outcome") ?? ToolStatus(last.Event.Type),
                    Property(first.Event.Payload, "CorrelationId"),
                    attempts);
            })
            .OrderBy(value => value.Attempts.FirstOrDefault()?.StartedAt)
            .ThenBy(value => value.LogicalCallId, StringComparer.Ordinal)
            .ToArray();
    }

    private static FlowRunToolAttempt ProjectAttempt(IGrouping<string, ToolEvent> group, int ordinal)
    {
        var ordered = group.OrderBy(value => value.Event.Sequence).ToArray();
        var first = ordered[0];
        var last = ordered[^1];
        var completed = ordered.FirstOrDefault(value => value.Event.Type is FlowRunEventType.ToolCallCompleted or FlowRunEventType.ToolCallFailed);
        var attempt = ParseAttempt(group.Key) ?? ordinal;
        return new FlowRunToolAttempt(
            group.Key,
            attempt,
            Property(last.Event.Payload, "Outcome") ?? ToolStatus(last.Event.Type),
            first.Event.Timestamp,
            completed?.Event.Timestamp,
            Number(completed?.Event.Payload, "DurationMilliseconds"),
            Property(completed?.Event.Payload, "ErrorCode"),
            Property(completed?.Event.Payload, "FailureKind"),
            ordered.Where(value => value.Event.Type == FlowRunEventType.ToolCallGovernanceEvaluated)
                .Sum(value => ArrayLength(value.Event.Payload, "Governance")));
    }

    private static string ToolStatus(FlowRunEventType type) => type switch
    {
        FlowRunEventType.ToolCallStarted => "running",
        FlowRunEventType.ToolCallGovernanceEvaluated => "governed",
        FlowRunEventType.ToolCallCompleted => "succeeded",
        FlowRunEventType.ToolCallFailed => "failed",
        _ => "unknown"
    };

    private static int? ParseAttempt(string invocationId)
    {
        const string marker = ":attempt:";
        var index = invocationId.LastIndexOf(marker, StringComparison.Ordinal);
        return index >= 0 && int.TryParse(invocationId[(index + marker.Length)..], NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static string? Property(JsonElement? payload, string name) =>
        payload is { ValueKind: JsonValueKind.Object } value
        && value.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static double? Number(JsonElement? payload, string name) =>
        payload is { ValueKind: JsonValueKind.Object } value
        && value.TryGetProperty(name, out var property)
        && property.TryGetDouble(out var result)
            ? result
            : null;

    private static int ArrayLength(JsonElement? payload, string name) =>
        payload is { ValueKind: JsonValueKind.Object } value
        && value.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.Array
            ? property.GetArrayLength()
            : 0;

    private sealed record ToolEvent(FlowRunEvent Event, string? ToolCallId, string? InvocationId);
}
