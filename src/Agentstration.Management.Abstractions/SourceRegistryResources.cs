namespace Agentstration.Management.Abstractions;

public static class SourceRegistryKinds
{
    public const string SourceRegistry = "SourceRegistry";
    public const string SourceRegistryIndex = "SourceRegistryIndex";
}

public static class SourceRegistryLimits
{
    public const int MaximumIndexDocumentBytes = 1024 * 1024;
    public const int MaximumCatalogs = 128;
    public const int MaximumDocumentBytes = 8 * 1024 * 1024;
    public const int MaximumDepth = 16;
    public const int MaximumPublishers = 256;
    public const int MaximumSourcesPerPublisher = 1_000;
    public const int MaximumSources = 5_000;
    public const int MaximumVersionsPerSource = 128;
    public const int MaximumVersions = 25_000;
}

public sealed record SourceRegistryCatalog
{
    public required string Name { get; init; }
    public required SourceCompatibility Compatibility { get; init; }
    public required string RegistryUrl { get; init; }
    public required string RegistryDigest { get; init; }
}

public sealed record SourceRegistryIndexDefinition
{
    public required IReadOnlyList<SourceRegistryCatalog> Catalogs { get; init; }
}

public sealed record SourceRegistryIndexManifest
{
    public required string ApiVersion { get; init; }
    public required string Kind { get; init; }
    public required SourceRegistryMetadata Metadata { get; init; }
    public required SourceRegistryIndexDefinition Definition { get; init; }
}

public sealed record ParsedSourceRegistryIndex(
    SourceRegistryIndexManifest Manifest,
    byte[] CanonicalJson,
    string IndexDigest);

public static class SourceRegistryPublisherStatuses
{
    public const string Declared = "Declared";
    public const string Verified = "Verified";
    public const string Official = "Official";
    public const string Revoked = "Revoked";
}

public sealed record SourceRegistryMetadata
{
    public required string Name { get; init; }
    public string? DisplayName { get; init; }
}

public sealed record SourceRegistryPublisher
{
    public required string Name { get; init; }
    public string? DisplayName { get; init; }
    public string? Url { get; init; }
    public required string Status { get; init; }
}

public sealed record SourceRegistryVersion
{
    public required string Version { get; init; }
    public required string ManifestUrl { get; init; }
    public required string ManifestDigest { get; init; }
}

public sealed record SourceRegistrySource
{
    public required string Publisher { get; init; }
    public required string Name { get; init; }
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public string? Latest { get; init; }
    public required IReadOnlyList<SourceRegistryVersion> Versions { get; init; }
}

public sealed record SourceRegistryDefinition
{
    public required IReadOnlyList<SourceRegistryPublisher> Publishers { get; init; }
    public required IReadOnlyList<SourceRegistrySource> Sources { get; init; }
}

public sealed record SourceRegistryManifest
{
    public required string ApiVersion { get; init; }
    public required string Kind { get; init; }
    public required SourceRegistryMetadata Metadata { get; init; }
    public required SourceRegistryDefinition Definition { get; init; }
}

public sealed record ParsedSourceRegistry(
    SourceRegistryManifest Manifest,
    byte[] CanonicalJson,
    string RegistryDigest);
