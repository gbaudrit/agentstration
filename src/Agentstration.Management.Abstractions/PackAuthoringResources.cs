using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.Resources;

namespace Agentstration.Management.Abstractions;

public static class PackAuthoringKinds
{
    public const string PackProject = "PackProject";
    public const string PackProjectBuild = "PackProjectBuild";
}

public sealed record PackArtifactReference(string StorageKey, string Sha256, long Length, string FileName);

public interface IPackArtifactStore
{
    Task<PackArtifactReference> SaveAsync(ReadOnlyMemory<byte> content, string fileName, CancellationToken cancellationToken);
    Task<Stream> OpenReadAsync(PackArtifactReference reference, CancellationToken cancellationToken);
}

[JsonConverter(typeof(JsonStringEnumConverter<PackProjectState>))]
public enum PackProjectState { Draft, Invalid, Ready, Building, BuildFailed, Built }

[JsonConverter(typeof(JsonStringEnumConverter<PackBuildState>))]
public enum PackBuildState { Succeeded, Failed }

public sealed record PackProjectOrigin(
    string Publisher,
    string Name,
    string Version,
    string SourceSha256,
    string InstalledPackResourceName);

[JsonConverter(typeof(JsonStringEnumConverter<PackProjectSourceKind>))]
public enum PackProjectSourceKind { Fork, WorkspaceSnapshot }

public sealed record PackProjectSourceResource(
    string Kind,
    string Name,
    string Namespace,
    string Path,
    bool ExplicitlySelected);

public sealed record PackProjectSourceDocument(string Path, string Kind, string Name, string Source);
public sealed record UpdatePackProjectSourceCommand(string Path, string Source);

public sealed record PackProjectProperties
{
    public required string Publisher { get; init; }
    public required string PackName { get; init; }
    public required string Version { get; init; }
    public PackAudience Audience { get; init; } = PackAudience.Universal;
    public PackPurpose Purpose { get; init; } = PackPurpose.Standard;
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string> Categories { get; init; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];
    public PackProjectSourceKind SourceKind { get; init; } = PackProjectSourceKind.Fork;
    public PackProjectOrigin? Origin { get; init; }
    public IReadOnlyList<PackProjectSourceResource> SourceResources { get; init; } = [];
    public required PackArtifactReference SourceArtifact { get; init; }
    public PackProjectState State { get; init; } = PackProjectState.Draft;
    public long Revision { get; init; } = 1;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public Guid? LastBuildId { get; init; }
}

public sealed record PackProjectResource : Resource
{
    public PackProjectProperties Definition { get; init; } = null!;
}

public sealed record PackProjectBuildProperties
{
    public required Guid ProjectId { get; init; }
    public required long ProjectRevision { get; init; }
    public required string Publisher { get; init; }
    public required string PackName { get; init; }
    public required string Version { get; init; }
    public required PackArtifactReference Artifact { get; init; }
    public PackBuildState State { get; init; } = PackBuildState.Succeeded;
    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed record PackProjectBuildResource : Resource
{
    public PackProjectBuildProperties Definition { get; init; } = null!;
}

public sealed record ForkPackCommand(
    string Publisher,
    string Name,
    string Version,
    string? DisplayName = null,
    string? Description = null,
    PackAudience? Audience = null,
    PackPurpose? Purpose = null);

public sealed record UpdatePackProjectCommand(
    string Version,
    string? DisplayName,
    string? Description,
    IReadOnlyList<string>? Categories = null,
    IReadOnlyList<string>? Tags = null,
    PackAudience? Audience = null,
    PackPurpose? Purpose = null);

public sealed record PackCompositionResourceKey
{
    [JsonConstructor]
    public PackCompositionResourceKey(string kind, string name, string @namespace = ResourceNamespace.DefaultValue)
    {
        Kind = kind;
        Name = name;
        Namespace = @namespace;
    }

    public PackCompositionResourceKey(string kind, string name, ResourceNamespace @namespace)
        : this(kind, name, @namespace.Value) { }

    public string Kind { get; }
    public string Name { get; }
    public string Namespace { get; }

    [JsonIgnore]
    public ResourceNamespace NamespaceValue => new(Namespace);

    [JsonIgnore]
    public ResourceAddress Address => ResourceAddress.Create(NamespaceValue, Kind, Name);
}

[JsonConverter(typeof(JsonStringEnumConverter<PackCompositionAvailability>))]
public enum PackCompositionAvailability { Selectable, BindingOnly, Unsupported }

public sealed record PackCompositionCatalogItem
{
    public required PackCompositionResourceKey Resource { get; init; }
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public string? Version { get; init; }
    public string Status { get; init; } = "Ready";
    public PackCompositionAvailability Availability { get; init; } = PackCompositionAvailability.Selectable;
    public string? AvailabilityReason { get; init; }
    public int DependencyCount { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<PackCompositionDependencyMode>))]
public enum PackCompositionDependencyMode { Include, Binding, Unsupported }

public sealed record PackCompositionDependency
{
    public required PackCompositionResourceKey Target { get; init; }
    public required string Relationship { get; init; }
    public PackCompositionDependencyMode Mode { get; init; } = PackCompositionDependencyMode.Include;
    public PackBindingTargetKind? BindingTargetKind { get; init; }
    public bool Required { get; init; } = true;
}

public sealed record PackCompositionResourceSnapshot(
    PackCompositionCatalogItem Resource,
    IReadOnlyList<PackCompositionDependency> Dependencies);

public interface IPackWorkspaceResourceCatalog
{
    Task<IReadOnlyList<PackCompositionCatalogItem>> ListAsync(CancellationToken cancellationToken);
    Task<PackCompositionResourceSnapshot?> GetAsync(PackCompositionResourceKey resource, CancellationToken cancellationToken);
    Task<JsonElement> ExportAsync(
        PackCompositionResourceKey resource,
        IReadOnlyDictionary<ResourceAddress, string> bindings,
        CancellationToken cancellationToken);
}

public sealed record PreviewPackCompositionCommand(
    IReadOnlyList<PackCompositionResourceKey> Resources);

[JsonConverter(typeof(JsonStringEnumConverter<PackCompositionIssueSeverity>))]
public enum PackCompositionIssueSeverity { Warning, Error }

public sealed record PackCompositionIssue(
    string Code,
    string Message,
    PackCompositionIssueSeverity Severity,
    PackCompositionResourceKey? Resource = null);

public sealed record PackCompositionPreviewResource(
    PackCompositionResourceKey Resource,
    string DisplayName,
    string Path,
    bool ExplicitlySelected,
    IReadOnlyList<PackCompositionDependency> Dependencies);

public sealed record PackCompositionPreviewBinding(
    string Name,
    PackBindingTargetKind TargetKind,
    string DisplayName,
    PackCompositionResourceKey WorkspaceResource,
    IReadOnlyList<PackCompositionResourceKey> UsedBy,
    bool Required = true);

public sealed record PackCompositionPreview(
    IReadOnlyList<PackCompositionPreviewResource> Resources,
    IReadOnlyList<PackCompositionPreviewBinding> Bindings,
    IReadOnlyList<PackCompositionIssue> Issues)
{
    public bool CanCreate => Resources.Count > 0 && Issues.All(issue => issue.Severity != PackCompositionIssueSeverity.Error);
}

public sealed record CreatePackProjectFromWorkspaceCommand
{
    public required string Publisher { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public PackAudience Audience { get; init; } = PackAudience.Universal;
    public PackPurpose Purpose { get; init; } = PackPurpose.Standard;
    public IReadOnlyList<string> Categories { get; init; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<PackCompositionResourceKey> Resources { get; init; } = [];
}
