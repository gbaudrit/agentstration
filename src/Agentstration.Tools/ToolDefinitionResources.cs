using System.Text.Json;
using Agentstration.Resources;

namespace Agentstration.Tools;

public static class AgentstrationToolProvider
{
    public const string Name = "agentstration";
    public static string ToolResourceName(string definitionName) => $"{Name}.{definitionName}";
}

public sealed record ToolDefinitionFlowTarget
{
    public required string Name { get; init; }
    public ResourceNamespace? Namespace { get; init; }
    public string? Version { get; init; }
    public bool UseActiveVersion { get; init; } = true;
}

public sealed record ToolDefinitionProperties
{
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public bool Enabled { get; init; } = true;
    public bool RequiresApproval { get; init; }
    public required JsonElement InputSchema { get; init; }
    public JsonElement? OutputSchema { get; init; }
    public required ToolDefinitionFlowTarget Flow { get; init; }
    public int InvocationTimeoutSeconds { get; init; } = 90;
}

public sealed record ToolDefinitionResource : Resource
{
    public ToolDefinitionProperties Definition { get; init; } = null!;
}

public sealed record ResolvedToolDefinitionFlowContract(
    string Name,
    ResourceNamespace Namespace,
    string Version,
    JsonElement? InputSchema,
    JsonElement? OutputSchema);

public interface IToolDefinitionFlowResolver
{
    Task<ResolvedToolDefinitionFlowContract> ResolveAsync(
        ResourceScopeRef scopeRef,
        ResourceNamespace ownerNamespace,
        ToolDefinitionFlowTarget target,
        CancellationToken cancellationToken);
}

public enum ToolDefinitionCallerKind { Mcp, Agent, Flow }

public sealed record ToolDefinitionInvocation(
    Guid TenantId,
    WorkspaceId WorkspaceId,
    Guid PrincipalId,
    ResourceNamespace Namespace,
    string ToolName,
    string CallId,
    string? CorrelationId,
    JsonElement Arguments,
    ToolDefinitionCallerKind CallerKind,
    string? CallerId = null);

public sealed record ToolDefinitionOperationReceipt(
    string WorkItemId,
    string FlowRunId,
    string FlowName,
    ResourceNamespace FlowNamespace,
    string FlowVersion,
    string CorrelationId,
    bool Recovered);

public sealed record ToolDefinitionInvocationResult(JsonElement? Output, ToolDefinitionOperationReceipt Receipt);

public interface IToolDefinitionExecutor
{
    Task<ToolDefinitionInvocationResult> ExecuteAsync(ToolDefinitionInvocation invocation, CancellationToken cancellationToken);
}

public sealed class ToolDefinitionInvocationException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}

public sealed record InternalMcpToolDefinition(
    string Name,
    string DisplayName,
    string? Description,
    JsonElement InputSchema,
    JsonElement? OutputSchema = null,
    bool RequiresApproval = false);

public sealed record InternalMcpToolInvocation(
    Guid TenantId,
    WorkspaceId WorkspaceId,
    Guid PrincipalId,
    string CallId,
    string? CorrelationId,
    JsonElement Arguments,
    ToolDefinitionCallerKind CallerKind,
    string? CallerId = null,
    string? RunId = null,
    string? FlowStepId = null);

public interface IInternalMcpToolDefinitionProvider
{
    InternalMcpToolDefinition Definition { get; }
}

public interface IInternalMcpToolHandler : IInternalMcpToolDefinitionProvider
{
    Task<JsonElement?> ExecuteAsync(InternalMcpToolInvocation invocation, CancellationToken cancellationToken);
}

public static class AgentstrationInternalTools
{
    public const string NotificationCreate = "work.notification.create";
}
