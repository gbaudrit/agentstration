using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.Resources;

namespace Agentstration.Flow;

public sealed record FlowResource(
    WorkspaceId WorkspaceId,
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
    WorkspaceId WorkspaceId,
    FlowId FlowId,
    string Version,
    string? Description,
    FlowDefinition Definition,
    IReadOnlyDictionary<string, string> Metadata,
    DateTimeOffset PublishedAt,
    FlowGraphDefinition? Graph = null,
    string? DefinitionHash = null,
    string? ReleaseNotes = null);
