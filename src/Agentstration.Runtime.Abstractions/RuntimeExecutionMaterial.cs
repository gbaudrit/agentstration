using System.Text.Json;
using Agentstration.Resources;

namespace Agentstration.Runtime.Abstractions;

public static class RuntimeExecutionMaterialVersions
{
    public const string V1 = "1.0";
}

public sealed record RuntimeExecutionToolMaterial(
    string Id,
    ResourceNamespace Namespace,
    string Name,
    string? Description,
    JsonElement InputSchema,
    JsonElement? OutputSchema,
    bool RequiresApproval,
    string? ProviderId,
    ResourceNamespace? ProviderNamespace,
    string? ExternalId);

public sealed record RuntimeExecutionAgentMaterial(
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
    ResourceNamespace ModelProfileNamespace,
    IReadOnlyList<RuntimeExecutionToolMaterial> Tools);

public abstract record RuntimeExecutionMaterial(
    string MaterialId,
    string SchemaVersion,
    string Digest,
    RuntimeAssignmentTargetKind TargetKind,
    WorkspaceId WorkspaceId,
    Guid TenantId,
    Guid PrincipalId,
    string RunId);

public sealed record RuntimeDirectAgentExecutionMaterial(
    string MaterialId,
    string SchemaVersion,
    string Digest,
    WorkspaceId WorkspaceId,
    Guid TenantId,
    Guid PrincipalId,
    string RunId,
    RuntimeRunInput Input,
    RuntimeExecutionOptions Execution,
    RuntimeExecutionAgentMaterial Agent)
    : RuntimeExecutionMaterial(MaterialId, SchemaVersion, Digest, RuntimeAssignmentTargetKind.RuntimeRun, WorkspaceId, TenantId, PrincipalId, RunId);

public sealed record RuntimeRootFlowExecutionMaterial(
    string MaterialId,
    string SchemaVersion,
    string Digest,
    WorkspaceId WorkspaceId,
    Guid TenantId,
    Guid PrincipalId,
    string RunId,
    string FlowId,
    string FlowNamespace,
    string FlowVersion,
    string FlowDefinitionHash,
    JsonElement Input,
    JsonElement Definition,
    IReadOnlyList<RuntimeExecutionAgentMaterial> Agents,
    RuntimeFlowResumeMaterial? Resume = null)
    : RuntimeExecutionMaterial(MaterialId, SchemaVersion, Digest, RuntimeAssignmentTargetKind.FlowRun, WorkspaceId, TenantId, PrincipalId, RunId);

public sealed record RuntimeFlowResumeMaterial(
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

public sealed record RuntimeFlowStepMaterial(
    string FlowRunId,
    string FlowVersion,
    string FlowDefinitionHash,
    string StepDefinitionId,
    string StepName,
    string StepType,
    int DefinitionPosition);

public interface IRuntimeExecutionMaterialResolver
{
    Task<RuntimeExecutionMaterial> ResolveAsync(RuntimeWorkerAssignment assignment, CancellationToken cancellationToken);
    Task<RuntimeRootFlowExecutionMaterial> ResolveFlowRunAsync(
        RuntimeWorkerAssignment assignment,
        string flowRunId,
        CancellationToken cancellationToken) =>
        Task.FromException<RuntimeRootFlowExecutionMaterial>(new RuntimeExecutionMaterialException(
            "flow_material_unsupported", "The execution material resolver does not support child Flows."));
    Task<RuntimeFlowStepMaterial> ResolveStepAsync(
        RuntimeWorkerAssignment assignment,
        string flowRunId,
        string flowVersion,
        string flowDefinitionHash,
        string stepDefinitionId,
        CancellationToken cancellationToken);
}

public sealed class RuntimeExecutionMaterialException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
