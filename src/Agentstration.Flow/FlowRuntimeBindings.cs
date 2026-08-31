using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.Resources;

namespace Agentstration.Flow;

public sealed record RuntimeExecutionBinding
{
    public required string ParticipantId { get; init; }
    public required ResourceNamespace AgentNamespace { get; init; }
    public required string AgentResourceId { get; init; }
    public required long AgentGeneration { get; init; }
    public required string DeploymentId { get; init; }
    public required string RevisionId { get; init; }
    public required string RuntimeProfileName { get; init; }
    public ResourceNamespace RuntimeProfileNamespace { get; init; } = ResourceNamespace.Default;
    public required string ModelProfileName { get; init; }
}

public sealed record DurableRuntimeStateReference(
    string RuntimeType,
    string StateId,
    DateTimeOffset CreatedAt);

public sealed record InputResponse(
    DateTimeOffset ReceivedAt,
    JsonElement Value,
    string PrincipalId);

public sealed record InputRequest
{
    public required WorkspaceId WorkspaceId { get; init; }
    public required string Id { get; init; }
    public required string RunId { get; init; }
    public string? Source { get; init; }
    public required string RuntimeRequestId { get; init; }
    public required string Prompt { get; init; }
    public InputRequestType Type { get; init; } = InputRequestType.Text;
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public InputRequestStatus Status { get; init; } = InputRequestStatus.Pending;
    public IReadOnlyList<string> Options { get; init; } = [];
    public JsonElement? Schema { get; init; }
    public InputResponse? Response { get; init; }
}

public sealed record FlowTargetReference(FlowTargetKind Kind, string Id, string? Version = null, ResourceNamespace? Namespace = null)
{
    public ResourceAddress Resolve(ResourceNamespace ownerNamespace) =>
        ResourceAddress.Create(Namespace ?? ownerNamespace, Kind == FlowTargetKind.Agent ? "Agent" : "Flow", Id);
}
