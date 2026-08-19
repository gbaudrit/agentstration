using System.Text.Json.Serialization;
using Agentstration.Resources;

namespace Agentstration.Runtime.Abstractions;

public sealed record ToolGovernanceAuditQuery
{
    public required ToolExecutionOwnerKind OwnerKind { get; init; }
    public required WorkspaceId WorkspaceId { get; init; }
    public required string RunId { get; init; }
    public long AfterSequence { get; init; }
    public int Limit { get; init; } = 100;
    public string? ToolCallId { get; init; }
    public string? InvocationId { get; init; }
    public string? ToolId { get; init; }
    public string? HookId { get; init; }
    public long? ResourceGeneration { get; init; }
    public ToolExecutionHookEvaluationKind? Decision { get; init; }
}

public sealed record ToolGovernanceAuditRecord
{
    public required ToolExecutionOwnerKind OwnerKind { get; init; }
    public required string RunId { get; init; }
    public required long Sequence { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required string ToolCallId { get; init; }
    public required string InvocationId { get; init; }
    public required string ToolId { get; init; }
    public required string ToolName { get; init; }
    public string? ProviderId { get; init; }
    public string? ExternalToolId { get; init; }
    public string? AgentId { get; init; }
    public string? CorrelationId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Arguments { get; init; }
    public IReadOnlyList<ToolExecutionHookEvaluation> Evaluations { get; init; } = [];
}

public sealed record ToolGovernanceAuditPage(
    IReadOnlyList<ToolGovernanceAuditRecord> Items,
    long? NextSequence);

public interface IToolGovernanceAuditReader
{
    Task<ToolGovernanceAuditPage> ListAsync(
        ToolGovernanceAuditQuery query,
        CancellationToken cancellationToken = default);
}

public sealed class ToolGovernanceAuditValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class ToolGovernanceAuditRunNotFoundException(string runId) : Exception($"Run '{runId}' was not found.");
