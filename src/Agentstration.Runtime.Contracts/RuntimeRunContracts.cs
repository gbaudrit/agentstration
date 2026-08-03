using Agentstration.Runtime.Abstractions;

namespace Agentstration.Runtime.Contracts;

public sealed record CreateRuntimeRunRequest
{
    public required RuntimeAgentReference Agent { get; init; }
    public required RuntimeRunInput Input { get; init; }
    public RuntimeExecutionOptions Execution { get; init; } = new();
    public RuntimeRunOrigin Origin { get; init; } = RuntimeRunOrigin.Api;
    public string? Initiator { get; init; }
}

public sealed record RuntimeRunPageResponse(IReadOnlyList<RuntimeRun> Value, string? NextLink = null);

public sealed record AgentRuntimeReadinessResponse(
    string AgentResourceId,
    long Generation,
    bool Ready,
    string State,
    string? DeploymentId,
    string? RevisionId,
    string? Error,
    string? RuntimeProfileId = null,
    string? ModelProfileId = null);

public sealed record PrepareAgentRuntimeResponse(
    string AgentResourceId,
    long Generation,
    string DeploymentId,
    string RevisionId,
    string State);
