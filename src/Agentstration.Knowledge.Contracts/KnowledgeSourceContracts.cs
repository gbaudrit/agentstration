using System.Text.Json;
using Agentstration.Resources;

namespace Agentstration.Knowledge.Contracts;

public static class KnowledgeResourceKinds
{
    public const string KnowledgeSource = "KnowledgeSource";
    public const string KnowledgeSourceToolExposure = "KnowledgeSourceToolExposure";
}

public enum KnowledgeSourceOperation { Search, Query, Read }

public readonly record struct KnowledgeSourceId(string Value, ResourceNamespace Namespace = default)
{
    public override string ToString() => $"{Namespace}/{Value}";
}

public sealed record KnowledgeFlowTarget
{
    public required string Name { get; init; }
    public ResourceNamespace? Namespace { get; init; }
    public string? Version { get; init; }
    public bool UseActiveVersion { get; init; } = true;
}

public sealed record KnowledgeSourceProperties
{
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public bool Enabled { get; init; } = true;
    public KnowledgeFlowTarget? IngestionFlow { get; init; }
    public KnowledgeFlowTarget? RetrievalFlow { get; init; }
}

public sealed record KnowledgeSourceResource : Resource
{
    public KnowledgeSourceProperties Definition { get; init; } = null!;
}

public sealed record ResolvedKnowledgeFlowBinding(
    string Name,
    ResourceNamespace Namespace,
    string Version,
    bool UsesActiveVersion,
    JsonElement? InputSchema,
    JsonElement? OutputSchema);

public sealed record KnowledgeSourceReadiness(
    bool Ready,
    bool Enabled,
    ResolvedKnowledgeFlowBinding? Ingestion,
    ResolvedKnowledgeFlowBinding? Retrieval,
    IReadOnlyList<string> Issues);

public sealed record KnowledgeSourceToolOperationExposure
{
    public required KnowledgeSourceOperation Operation { get; init; }
    public required ResourceReference Tool { get; init; }
    public required string Capability { get; init; }
    public required string Route { get; init; }
}

public sealed record KnowledgeSourceToolExposureResource : Resource
{
    public required Guid KnowledgeSourceUid { get; init; }
    public required string KnowledgeSourceName { get; init; }
    public required long KnowledgeSourceGeneration { get; init; }
    public required ResolvedKnowledgeFlowBinding RetrievalFlow { get; init; }
    public required ResourceReference ToolSet { get; init; }
    public required string ToolSetVersion { get; init; }
    public IReadOnlyList<KnowledgeSourceToolOperationExposure> Operations { get; init; } = [];
}

public sealed record CreateKnowledgeSourceRequest(
    string Name,
    KnowledgeSourceProperties Properties,
    string? Namespace = null);

public sealed record PutKnowledgeSourceRequest(KnowledgeSourceProperties Properties);
public sealed record SetKnowledgeSourceEnabledRequest(bool Enabled);
public sealed record PublishKnowledgeSourceToolExposureRequest
{
    public string Version { get; init; } = "1.0.0";
    public bool RequiresApproval { get; init; }
}
