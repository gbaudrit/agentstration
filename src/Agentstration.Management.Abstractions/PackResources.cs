using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.Resources;

namespace Agentstration.Management.Abstractions;

public static class PackKinds
{
    public const string Pack = "Pack";
}

public static class PackProvenanceAnnotations
{
    public const string Publisher = "agentstration.io/pack.publisher";
    public const string Name = "agentstration.io/pack.name";
    public const string Version = "agentstration.io/pack.version";
}

public static class ResourceProvenanceAnnotations
{
    public const string BuiltIn = "agentstration.io/builtin";
}

public sealed record PackManifest
{
    public required string ApiVersion { get; init; }
    public required string Kind { get; init; }
    public PackMetadata Metadata { get; init; } = new();
    public required PackDefinition Definition { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<PackAudience>))]
public enum PackAudience
{
    [JsonStringEnumMemberName("universal")] Universal,
    [JsonStringEnumMemberName("personal")] Personal,
    [JsonStringEnumMemberName("professional")] Professional
}

[JsonConverter(typeof(JsonStringEnumConverter<PackPurpose>))]
public enum PackPurpose
{
    [JsonStringEnumMemberName("standard")] Standard,
    [JsonStringEnumMemberName("sample")] Sample,
    [JsonStringEnumMemberName("template")] Template
}

public sealed record PackMetadata
{
    public string Name { get; init; } = string.Empty;
    public string Publisher { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public PackAudience Audience { get; init; } = PackAudience.Universal;
    public PackPurpose Purpose { get; init; } = PackPurpose.Standard;
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string> Categories { get; init; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];
}

public sealed record PackDefinition
{
    public IReadOnlyList<string> Resources { get; init; } = [];
    public IReadOnlyList<PackRequirement> Requirements { get; init; } = [];
    public IReadOnlyList<PackBindingRequirement> Bindings { get; init; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter<PackBindingTargetKind>))]
public enum PackBindingTargetKind
{
    [JsonStringEnumMemberName("modelProfile")] ModelProfile,
    [JsonStringEnumMemberName("modelProvider")] ModelProvider,
    [JsonStringEnumMemberName("runtimeProfile")] RuntimeProfile,
    [JsonStringEnumMemberName("extensionRegistration")] ExtensionRegistration,
    [JsonStringEnumMemberName("secret")] Secret
}

public sealed record PackBindingRequirement
{
    public required string Name { get; init; }
    public required PackBindingTargetKind TargetKind { get; init; }
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public bool Required { get; init; } = true;
}

public sealed record PackBindingSelection(string Name, ResourceReference Target);

public sealed record PackBuildInstallRequest(
    bool ReplaceExisting = false,
    IReadOnlyList<PackBindingSelection>? Bindings = null);

public sealed record PackBindingResolution(
    string Name,
    PackBindingTargetKind TargetKind,
    ResourceReference Target);

public sealed record PackBindingUsage(string ResourceKind, string ResourceName, string Path);

public sealed record PackBindingPreview(
    string Name,
    PackBindingTargetKind TargetKind,
    string DisplayName,
    string? Description,
    bool Required,
    IReadOnlyList<PackBindingUsage> UsedBy,
    ResourceReference? Target,
    bool TargetAvailable)
{
    public bool IsResolved => !Required || TargetAvailable;
}

public sealed record PackRequirement
{
    public string? Capability { get; init; }
    public string? Pack { get; init; }
    public string? Version { get; init; }
}

public readonly record struct PackIdentity(string Publisher, string Name)
{
    public string ResourceName => $"{Publisher.Length}-{Publisher}-{Name}";
    public ResourceNamespace Namespace => new($"{Publisher}.{Name}");
    public override string ToString() => $"{Publisher}/{Name}";
}

[JsonConverter(typeof(JsonStringEnumConverter<InstalledPackState>))]
public enum InstalledPackState
{
    [JsonStringEnumMemberName("installing")] Installing,
    [JsonStringEnumMemberName("installed")] Installed,
    [JsonStringEnumMemberName("updating")] Updating,
    [JsonStringEnumMemberName("uninstalling")] Uninstalling,
    [JsonStringEnumMemberName("failed")] Failed,
    [JsonStringEnumMemberName("degraded")] Degraded
}

public sealed record ManagedPackResource
{
    public ResourceNamespace Namespace { get; init; } = ResourceNamespace.Default;
    public required string Kind { get; init; }
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required string VersionToken { get; init; }
}

public sealed record InstalledPackProperties
{
    public required string Publisher { get; init; }
    public required string PackName { get; init; }
    public ResourceNamespace Namespace { get; init; } = ResourceNamespace.Default;
    public required string Version { get; init; }
    public PackAudience Audience { get; init; } = PackAudience.Universal;
    public PackPurpose Purpose { get; init; } = PackPurpose.Standard;
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public required string Source { get; init; }
    public PackArtifactReference? SourceArtifact { get; init; }
    public required DateTimeOffset InstalledAt { get; init; }
    public InstalledPackState State { get; init; } = InstalledPackState.Installing;
    public IReadOnlyList<PackBindingResolution> Bindings { get; init; } = [];
    public IReadOnlyList<ManagedPackResource> ManagedResources { get; init; } = [];
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed record InstalledPackResource : Resource
{
    public InstalledPackProperties Definition { get; init; } = null!;
}

public sealed record PackConfigurationProperties
{
    public required string Publisher { get; init; }
    public required string PackName { get; init; }
    public IReadOnlyList<PackBindingResolution> Bindings { get; init; } = [];
    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record PackConfigurationResource : Resource
{
    public PackConfigurationProperties Definition { get; init; } = null!;
}

public sealed record PackResourceDocument(
    string Path,
    string ApiVersion,
    string Kind,
    string Name,
    JsonElement Manifest);

public sealed record PackArchive(
    PackManifest Manifest,
    IReadOnlyList<PackResourceDocument> Resources,
    string Source,
    ReadOnlyMemory<byte> Content = default);

[JsonConverter(typeof(JsonStringEnumConverter<PackResourceChange>))]
public enum PackResourceChange
{
    [JsonStringEnumMemberName("add")] Add,
    [JsonStringEnumMemberName("update")] Update,
    [JsonStringEnumMemberName("remove")] Remove,
    [JsonStringEnumMemberName("conflict")] Conflict
}

public sealed record PackResourcePreview(string Path, string Kind, string Name, bool AlreadyExists, PackResourceChange Change = PackResourceChange.Add);

public sealed record PackRemovalOptions(bool RemoveDashboardReferences = false, bool CloseInteractions = true);

public sealed record PackInstallationPreview(
    PackMetadata Metadata,
    IReadOnlyList<PackResourcePreview> Resources,
    bool AlreadyInstalled)
{
    public ResourceNamespace Namespace { get; init; } = ResourceNamespace.Default;
    public IReadOnlyList<PackBindingPreview> Bindings { get; init; } = [];
    public bool CanInstall => !AlreadyInstalled && Resources.All(resource => !resource.AlreadyExists);
    public bool RequiresConfiguration => Bindings.Any(binding => !binding.IsResolved);
}

public interface IPackArchiveReader
{
    Task<PackArchive> ReadAsync(Stream archive, string source, CancellationToken cancellationToken);
}

public interface IPackResourceHandler
{
    string Kind { get; }
    int InstallOrder { get; }
    Task ValidateAsync(PackResourceDocument resource, IReadOnlyList<PackResourceDocument> allResources, CancellationToken cancellationToken);
    Task<bool> ExistsAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken);
    Task<ManagedPackResource> InstallAsync(PackResourceDocument resource, PackIdentity pack, ResourceNamespace @namespace, string packVersion, CancellationToken cancellationToken);
    Task<ManagedPackResource> UpdateAsync(PackResourceDocument resource, ManagedPackResource current, PackIdentity pack, string packVersion, CancellationToken cancellationToken);
    Task<string?> GetVersionTokenAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken);
    Task DeleteAsync(ManagedPackResource resource, PackRemovalOptions options, CancellationToken cancellationToken);
}

