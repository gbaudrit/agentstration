using System.Text.Json.Serialization;

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
    public required PackProjectOrigin Origin { get; init; }
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
