using Agentstration.Flow;
using Agentstration.Management.Abstractions;
using Agentstration.Work;

namespace Agentstration.Infrastructure.Declarative;

public sealed record DeclarativeResourceEnvelope<T>
{
    public required string ApiVersion { get; init; }
    public required string Kind { get; init; }
    public ResourceMetadata Metadata { get; init; } = new();
    public T Definition { get; init; } = default!;
}

public sealed record DeclarativeFlowDefinition
{
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public string Version { get; init; } = string.Empty;
    public bool Enabled { get; init; } = true;
    public FlowDefinition Spec { get; init; } = null!;
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
    public FlowGraphDefinition? Graph { get; init; }
    public bool Publish { get; init; } = true;
    public bool Activate { get; init; } = true;
}

public sealed record DeclarativeEntryDefinition
{
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public EntryPresentation Presentation { get; init; } = null!;
    public EntryBinding Binding { get; init; } = null!;
    public EntryBehavior Behavior { get; init; } = new();
    public bool Publish { get; init; } = true;
}
