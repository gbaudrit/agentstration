using System.Text.Json.Serialization;
using Agentstration.Resources;

namespace Agentstration.ResourceManagement.Contracts;

public record ResourceDeclaration<TDefinition>
{
    public required string ApiVersion { get; init; }
    public required string Kind { get; init; }
    public required ResourceMetadata Metadata { get; init; }
    public required TDefinition Definition { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResourceScopeRef? ScopeRef { get; init; }
}

public sealed record ResourceScopeTargetResponse(ResourceScopeRef ScopeRef, ResourceScopeKind Kind, string DisplayName, bool CanWrite);
public sealed record ResourceScopeInventoryItemResponse(
    Guid Uid,
    ResourceNamespace Namespace,
    string Kind,
    string Name,
    DateTimeOffset UpdatedAt);
public sealed record ResourceScopeInventoryNodeResponse(
    ResourceScopeRef ScopeRef,
    ResourceScopeKind Kind,
    string DisplayName,
    ResourceScopeRef? ParentScopeRef,
    bool IsCurrent,
    IReadOnlyList<ResourceScopeInventoryItemResponse> Resources);
public sealed record ResourceScopeInventoryResponse(IReadOnlyList<ResourceScopeInventoryNodeResponse> Scopes);
