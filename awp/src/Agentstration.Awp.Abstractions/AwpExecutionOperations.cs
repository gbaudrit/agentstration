using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentstration.Awp.Abstractions;

public sealed record AwpExecutionToolMaterial(
    string Id,
    string Namespace,
    string Name,
    string? Description,
    JsonElement InputSchema,
    JsonElement? OutputSchema,
    bool RequiresApproval);

public sealed record AwpExecutionAgentMaterial(
    string MaterialId,
    string ParticipantId,
    Guid AgentId,
    string AgentName,
    long Generation,
    string RevisionId,
    string DefinitionHash,
    string Handler,
    string DisplayName,
    string Description,
    string Instructions,
    string ModelProfileName,
    string ModelProfileNamespace,
    IReadOnlyList<AwpExecutionToolMaterial> Tools);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(AwpDirectAgentExecutionMaterial), "directAgent")]
[JsonDerivedType(typeof(AwpRootFlowExecutionMaterial), "rootFlow")]
public abstract record AwpExecutionMaterial(
    string MaterialId,
    string SchemaVersion,
    string Digest,
    string RunId);

public sealed record AwpDirectAgentExecutionMaterial(
    string MaterialId,
    string SchemaVersion,
    string Digest,
    string RunId,
    IReadOnlyList<AwpExecutionMessage> Messages,
    string? Context,
    AwpExecutionOptions Execution,
    AwpExecutionAgentMaterial Agent)
    : AwpExecutionMaterial(MaterialId, SchemaVersion, Digest, RunId);

public sealed record AwpRootFlowExecutionMaterial(
    string MaterialId,
    string SchemaVersion,
    string Digest,
    string RunId,
    string FlowId,
    string FlowNamespace,
    string FlowVersion,
    string FlowDefinitionHash,
    JsonElement Input,
    JsonElement Definition,
    IReadOnlyList<AwpExecutionAgentMaterial> Agents,
    AwpFlowResumeMaterial? Resume = null)
    : AwpExecutionMaterial(MaterialId, SchemaVersion, Digest, RunId);

public sealed record AwpFlowResumeMaterial(
    string RuntimeType,
    string StateId,
    string InputRequestId,
    string RuntimeRequestId,
    string Prompt,
    string InputType,
    IReadOnlyList<string> Options,
    string? Source,
    JsonElement Response,
    DateTimeOffset RespondedAt,
    string PrincipalId);

public sealed record AwpExecutionMessage(string Role, string Content);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(AwpModelTextContent), "text")]
[JsonDerivedType(typeof(AwpModelToolCallContent), "toolCall")]
[JsonDerivedType(typeof(AwpModelToolResultContent), "toolResult")]
public abstract record AwpModelContent;

public sealed record AwpModelTextContent(string Text) : AwpModelContent;
public sealed record AwpModelToolCallContent(string CallId, string Name, JsonElement Arguments) : AwpModelContent;
public sealed record AwpModelToolResultContent(string CallId, JsonElement Result) : AwpModelContent;
public sealed record AwpModelMessage(string Role, IReadOnlyList<AwpModelContent> Contents);

public sealed record AwpExecutionOptions(
    int TimeoutSeconds,
    string Streaming,
    bool? PersistToolArguments,
    IReadOnlyDictionary<string, JsonElement> Parameters);

public sealed record AwpGetExecutionMaterialRequest(AwpAssignmentCommandContext Context);

public sealed record AwpGetExecutionMaterialResponse(DateTimeOffset ServerTime, AwpExecutionMaterial Material);

public sealed record AwpStoreCheckpointRequest(
    AwpAssignmentCommandContext Context,
    string CheckpointId,
    string SchemaVersion,
    string CompatibilityKey,
    JsonElement Payload);

public sealed record AwpCheckpointResponse(
    DateTimeOffset ServerTime,
    string CheckpointId,
    string SchemaVersion,
    string CompatibilityKey,
    JsonElement Payload,
    DateTimeOffset PersistedAt);

public sealed record AwpGetCheckpointRequest(AwpAssignmentCommandContext Context, string CheckpointId);

public sealed record AwpInvokeModelRequest(
    AwpAssignmentCommandContext Context,
    string ParticipantId,
    AwpTurnId TurnId,
    AwpTurnAttemptId TurnAttemptId,
    IReadOnlyList<AwpModelMessage> Messages);

public sealed record AwpInvokeModelResponse(
    DateTimeOffset ServerTime,
    IReadOnlyList<AwpModelContent> Contents,
    string? ModelId,
    string? FinishReason,
    int? InputTokens,
    int? OutputTokens);

public sealed record AwpInvokeToolRequest(
    AwpAssignmentCommandContext Context,
    string ParticipantId,
    AwpTurnId TurnId,
    AwpTurnAttemptId TurnAttemptId,
    AwpToolCallId ToolCallId,
    string ToolId,
    JsonElement? Arguments);

public sealed record AwpInvokeToolResponse(DateTimeOffset ServerTime, JsonElement? Result);

public sealed record AwpStoreArtifactRequest(
    AwpAssignmentCommandContext Context,
    string Name,
    string ContentType,
    byte[] Content);

public sealed record AwpGetArtifactRequest(AwpAssignmentCommandContext Context, Guid ArtifactId);

public sealed record AwpArtifactResponse(
    DateTimeOffset ServerTime,
    Guid ArtifactId,
    string Name,
    string ContentType,
    long Length,
    byte[] Content);

public sealed record AwpCreateChildFlowRequest(
    AwpAssignmentCommandContext Context,
    AwpStepExecutionId StepExecutionId,
    JsonElement Input);

public sealed record AwpChildFlowResponse(
    DateTimeOffset ServerTime,
    string RunId,
    string Status,
    JsonElement? Output,
    AwpRootFlowExecutionMaterial? Material = null);
