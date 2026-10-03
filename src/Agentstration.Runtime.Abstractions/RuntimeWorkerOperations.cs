using System.Text.Json;
using Agentstration.Resources;

namespace Agentstration.Runtime.Abstractions;

public abstract record RuntimeGovernedModelContent;
public sealed record RuntimeGovernedModelText(string Text) : RuntimeGovernedModelContent;
public sealed record RuntimeGovernedModelToolCall(string CallId, string Name, JsonElement Arguments) : RuntimeGovernedModelContent;
public sealed record RuntimeGovernedModelToolResult(string CallId, JsonElement Result) : RuntimeGovernedModelContent;
public sealed record RuntimeGovernedModelMessage(RuntimeMessageRole Role, IReadOnlyList<RuntimeGovernedModelContent> Contents);

public sealed record RuntimeGovernedModelRequest(
    RuntimeExecutionAgentMaterial Agent,
    IReadOnlyList<RuntimeGovernedModelMessage> Messages,
    RuntimeExecutionOptions Options);

public sealed record RuntimeGovernedModelResponse(
    IReadOnlyList<RuntimeGovernedModelContent> Contents,
    string? ModelId,
    string? FinishReason,
    int? InputTokens,
    int? OutputTokens);

public sealed record RuntimeGovernedToolRequest(
    RuntimeExecutionAgentMaterial Agent,
    RuntimeExecutionToolMaterial Tool,
    Guid ToolCallId,
    Guid TurnId,
    Guid TurnAttemptId,
    Guid? StepExecutionId,
    Guid TenantId,
    WorkspaceId WorkspaceId,
    Guid PrincipalId,
    string RunId,
    JsonElement? Arguments,
    bool? PersistArguments);

public sealed record RuntimeGovernedArtifact(
    Guid ArtifactId,
    string Name,
    string ContentType,
    long Length,
    byte[] Content);

public sealed record RuntimeGovernedChildFlow(
    string RunId,
    string Status,
    JsonElement? Output);

public interface IRuntimeWorkerOperationGateway
{
    Task<RuntimeGovernedModelResponse> InvokeModelAsync(RuntimeGovernedModelRequest request, CancellationToken cancellationToken);
    Task<JsonElement?> InvokeToolAsync(RuntimeGovernedToolRequest request, CancellationToken cancellationToken);
    Task<RuntimeGovernedArtifact> StoreArtifactAsync(WorkspaceId workspaceId, RuntimeAssignmentId assignmentId,
        string name, string contentType, byte[] content, CancellationToken cancellationToken);
    Task<RuntimeGovernedArtifact?> GetArtifactAsync(WorkspaceId workspaceId, RuntimeAssignmentId assignmentId,
        Guid artifactId, CancellationToken cancellationToken);
    Task<RuntimeGovernedChildFlow> CreateOrGetChildFlowAsync(WorkspaceId workspaceId, string parentRunId,
        string stepDefinitionId, JsonElement input, CancellationToken cancellationToken);
}
