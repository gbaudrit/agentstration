using System.Text.Json;
using Agentstration.Resources;

namespace Agentstration.Tools;

public sealed record ToolSetMember
{
    public required ResourceReference Tool { get; init; }
    public required string Capability { get; init; }
    public required string Route { get; init; }
}

public sealed record ToolSetProperties
{
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public bool Enabled { get; init; } = true;
    public ResourceReference? Category { get; init; }
    public string Version { get; init; } = "1.0.0";
    public bool Publish { get; init; }
    public IReadOnlyList<ToolSetMember> Members { get; init; } = [];
}

public sealed record ToolSetResource : Resource
{
    public ToolSetProperties Definition { get; init; } = null!;
}

public sealed record PublishedToolSetMember
{
    public required string Capability { get; init; }
    public required string Route { get; init; }
    public required string ToolName { get; init; }
    public required ResourceNamespace ToolNamespace { get; init; }
    public required Guid ToolUid { get; init; }
    public required long ToolGeneration { get; init; }
    public required string ProviderName { get; init; }
    public required ResourceNamespace ProviderNamespace { get; init; }
    public required string ExternalToolId { get; init; }
    public required JsonElement InputSchema { get; init; }
    public JsonElement? OutputSchema { get; init; }
    public bool RequiresApproval { get; init; }
}

public sealed record ToolSetVersionResource : Resource
{
    public required Guid ToolSetUid { get; init; }
    public required string ToolSetName { get; init; }
    public required long ToolSetGeneration { get; init; }
    public required string Version { get; init; }
    public required string DefinitionHash { get; init; }
    public required DateTimeOffset PublishedAt { get; init; }
    public IReadOnlyList<PublishedToolSetMember> Members { get; init; } = [];
}

public sealed record ToolSetRouteSelection(
    string ToolSetName,
    ResourceNamespace ToolSetNamespace,
    string ToolSetVersion,
    PublishedToolSetMember Member);

public sealed class ToolSetValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public interface IToolSetDeletionGuard
{
    Task ValidateDeleteAsync(
        ResourceScopeRef scopeRef,
        ResourceNamespace @namespace,
        string name,
        CancellationToken cancellationToken);
}
