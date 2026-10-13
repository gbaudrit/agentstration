using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.Resources;

namespace Agentstration.Flows;

public static class FlowResourceKinds
{
    public const string Flow = "Flow";
}

public sealed record FlowResource(
    ResourceScopeRef ScopeRef,
    FlowId Id,
    string Name,
    string? Description,
    string Version,
    bool Enabled,
    string? ActiveVersion,
    FlowDefinition Definition,
    IReadOnlyDictionary<string, string> Metadata,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? DisplayName = null,
    FlowGraphDefinition? Graph = null);

public sealed record FlowVersion(
    ResourceScopeRef ScopeRef,
    FlowId FlowId,
    string Version,
    string? Description,
    FlowDefinition Definition,
    IReadOnlyDictionary<string, string> Metadata,
    DateTimeOffset PublishedAt,
    FlowGraphDefinition? Graph = null,
    string? DefinitionHash = null,
    string? ReleaseNotes = null);
