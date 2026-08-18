using System.Text.Json;
using Agentstration.Flow;
using Agentstration.Flow.Storage.Abstractions;
using Agentstration.Runtime.Abstractions;

namespace Agentstration.Infrastructure.Runtime;

public sealed class ToolGovernanceAuditReader(
    IRuntimeRunStore runtimeRuns,
    IFlowRepository flowRuns) : IToolGovernanceAuditReader
{
    public async Task<ToolGovernanceAuditPage> ListAsync(
        ToolGovernanceAuditQuery query,
        CancellationToken cancellationToken = default)
    {
        Validate(query);
        var records = query.OwnerKind switch
        {
            ToolExecutionOwnerKind.RuntimeRun => await RuntimeRecordsAsync(query, cancellationToken),
            ToolExecutionOwnerKind.FlowRun => await FlowRecordsAsync(query, cancellationToken),
            _ => throw new ToolGovernanceAuditValidationException(
                "invalid_owner_kind",
                "Tool governance audit supports RuntimeRun and FlowRun owners.")
        };
        var filtered = records.Where(record => Matches(record, query)).Take(query.Limit + 1).ToArray();
        var hasMore = filtered.Length > query.Limit;
        var items = filtered.Take(query.Limit).ToArray();
        return new ToolGovernanceAuditPage(items, hasMore ? items[^1].Sequence : null);
    }

    private async Task<IReadOnlyList<ToolGovernanceAuditRecord>> RuntimeRecordsAsync(
        ToolGovernanceAuditQuery query,
        CancellationToken cancellationToken)
    {
        if (await runtimeRuns.GetAsync(query.WorkspaceId, query.RunId, cancellationToken) is null)
            throw new ToolGovernanceAuditRunNotFoundException(query.RunId);
        var events = await runtimeRuns.ListEventsAsync(
            query.WorkspaceId,
            query.RunId,
            query.AfterSequence,
            cancellationToken);
        return events
            .Where(runEvent => runEvent.Kind == RuntimeRunEventKind.ToolCallGovernanceEvaluated && runEvent.ToolCall is not null)
            .Select(runEvent => FromRuntime(runEvent, query.OwnerKind))
            .ToArray();
    }

    private async Task<IReadOnlyList<ToolGovernanceAuditRecord>> FlowRecordsAsync(
        ToolGovernanceAuditQuery query,
        CancellationToken cancellationToken)
    {
        if (await flowRuns.GetRunAsync(query.WorkspaceId, query.RunId, cancellationToken) is null)
            throw new ToolGovernanceAuditRunNotFoundException(query.RunId);
        var events = await flowRuns.ListRunEventsAsync(
            query.WorkspaceId,
            query.RunId,
            query.AfterSequence,
            cancellationToken);
        return events
            .Where(runEvent => runEvent.Type == FlowRunEventType.ToolCallGovernanceEvaluated && runEvent.Payload is not null)
            .Select(runEvent => FromFlow(runEvent, query.OwnerKind))
            .ToArray();
    }

    private static ToolGovernanceAuditRecord FromRuntime(RuntimeRunEvent runEvent, ToolExecutionOwnerKind ownerKind)
    {
        var call = runEvent.ToolCall!;
        return new ToolGovernanceAuditRecord
        {
            OwnerKind = ownerKind,
            RunId = runEvent.RunId,
            Sequence = runEvent.Sequence,
            Timestamp = runEvent.Timestamp,
            ToolCallId = call.Id,
            InvocationId = call.InvocationId,
            ToolId = call.ToolId,
            ToolName = call.Name,
            ProviderId = call.ProviderId,
            ExternalToolId = call.ExternalToolId,
            CorrelationId = call.CorrelationId,
            Evaluations = call.Governance
        };
    }

    private static ToolGovernanceAuditRecord FromFlow(FlowRunEvent runEvent, ToolExecutionOwnerKind ownerKind)
    {
        var payload = runEvent.Payload!.Value;
        return new ToolGovernanceAuditRecord
        {
            OwnerKind = ownerKind,
            RunId = runEvent.RunId,
            Sequence = runEvent.Sequence,
            Timestamp = runEvent.Timestamp,
            ToolCallId = RequiredString(payload, "ToolCallId"),
            InvocationId = RequiredString(payload, "InvocationId"),
            ToolId = RequiredString(payload, "ToolId"),
            ToolName = RequiredString(payload, "ToolName"),
            ProviderId = OptionalString(payload, "ProviderId"),
            ExternalToolId = OptionalString(payload, "ExternalToolId"),
            AgentId = OptionalString(payload, "AgentId"),
            CorrelationId = OptionalString(payload, "CorrelationId"),
            Evaluations = Property(payload, "Governance") is { ValueKind: JsonValueKind.Array } governance
                ? governance.Deserialize<ToolExecutionHookEvaluation[]>() ?? []
                : []
        };
    }

    private static bool Matches(ToolGovernanceAuditRecord record, ToolGovernanceAuditQuery query) =>
        (query.ToolId is null || string.Equals(record.ToolId, query.ToolId, StringComparison.Ordinal))
        && (query.HookId is null || record.Evaluations.Any(evaluation =>
            string.Equals(evaluation.Hook.Id, query.HookId, StringComparison.Ordinal)
            || string.Equals(evaluation.Hook.ResourceId, query.HookId, StringComparison.Ordinal)))
        && (query.Decision is null || record.Evaluations.Any(evaluation => evaluation.Decision == query.Decision));

    private static void Validate(ToolGovernanceAuditQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrWhiteSpace(query.RunId))
            throw new ToolGovernanceAuditValidationException("run_id_required", "A Run identity is required.");
        if (query.AfterSequence < 0)
            throw new ToolGovernanceAuditValidationException("invalid_after_sequence", "afterSequence must be zero or greater.");
        if (query.Limit is < 1 or > 200)
            throw new ToolGovernanceAuditValidationException("invalid_limit", "limit must be between 1 and 200.");
    }

    private static string RequiredString(JsonElement payload, string name) =>
        OptionalString(payload, name)
        ?? throw new InvalidOperationException($"Flow Tool governance event is missing '{name}'.");

    private static string? OptionalString(JsonElement payload, string name) =>
        Property(payload, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static JsonElement? Property(JsonElement payload, string name)
    {
        if (payload.TryGetProperty(name, out var value)) return value;
        var camelCase = JsonNamingPolicy.CamelCase.ConvertName(name);
        return payload.TryGetProperty(camelCase, out value) ? value : null;
    }
}
