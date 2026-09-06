using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentstration.Management.Abstractions;

public static class SourceKinds
{
    public const string PublishedSourceVersion = "SourceVersion";
    public const string SourceProvider = "sourceProvider";
}

public sealed class SourceValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class SourceVersionConflictException(string message) : Exception(message);

public sealed class SourceRetrievalException(string code, string message, Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = code;
}

public sealed record SourcePublisher
{
    public required string Name { get; init; }
    public string? DisplayName { get; init; }
    public string? Url { get; init; }
}

public sealed record SourceBindingDefinition
{
    public required string Name { get; init; }
    public required string TargetKind { get; init; }
}

public sealed record SourceProviderReference
{
    public required string Binding { get; init; }
}

public sealed record SourceCompatibilityBounds
{
    public string? MinVersion { get; init; }
    public string? MaxVersionExclusive { get; init; }
}

public sealed record SourceCompatibility
{
    public SourceCompatibilityBounds? Agentstration { get; init; }
}

public sealed record SourceChannelConfiguration
{
    public required string OptionSet { get; init; }
    public required string Version { get; init; }
    public required string SchemaDigest { get; init; }
    public JsonElement Values { get; init; }
}

public sealed record SourceChannelDefinition
{
    public required string Name { get; init; }
    public SourceCompatibility? Compatibility { get; init; }
    public required SourceProviderReference Provider { get; init; }
    public required SourceChannelConfiguration Configuration { get; init; }
}

public sealed record SourceCatalogDefinition
{
    public required string Kind { get; init; }
    public required string Path { get; init; }
}

public sealed record PublishedSourceVersionDefinition
{
    public required string Version { get; init; }
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public required SourcePublisher Publisher { get; init; }
    public IReadOnlyList<SourceBindingDefinition> Bindings { get; init; } = [];
    public IReadOnlyList<SourceChannelDefinition> Channels { get; init; } = [];
    public IReadOnlyList<SourceCatalogDefinition> Catalogs { get; init; } = [];
}

public sealed record PublishedSourceVersionManifest
{
    public required string ApiVersion { get; init; }
    public required string Kind { get; init; }
    public ResourceMetadata Metadata { get; init; } = new();
    public required PublishedSourceVersionDefinition Definition { get; init; }
}

public sealed record SourceIdentityProperties
{
    public required string Publisher { get; init; }
}

public sealed record SourceResource : Resource
{
    public required SourceIdentityProperties Definition { get; init; }
}

public sealed record SourceVersionProperties
{
    public required Guid SourceUid { get; init; }
    public required string SourceName { get; init; }
    public required string Publisher { get; init; }
    public required string Version { get; init; }
    public required string ManifestDigest { get; init; }
    public required string RawManifest { get; init; }
    public required PublishedSourceVersionDefinition PublishedDefinition { get; init; }
    public required DateTimeOffset ImportedAt { get; init; }
    public SourceManifestOrigin? Origin { get; init; }
}

public sealed record SourceVersionResource : Resource
{
    public required SourceVersionProperties Definition { get; init; }
}

public sealed record SourceManifestOrigin
{
    public required string Url { get; init; }
    public string? ETag { get; init; }
    public DateTimeOffset? LastModified { get; init; }
}

public sealed record SourceConfigurationProperties
{
    public required Guid SourceUid { get; init; }
    public required string DisplayName { get; init; }
    public SourceManifestOrigin? Origin { get; init; }
}

public sealed record SourceConfigurationResource : Resource
{
    public required SourceConfigurationProperties Definition { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<SourceImportOutcome>))]
public enum SourceImportOutcome
{
    [JsonStringEnumMemberName("created")] Created,
    [JsonStringEnumMemberName("unchanged")] Unchanged,
    [JsonStringEnumMemberName("rejected")] Rejected
}

public sealed record SourceObservedProperties
{
    public required Guid SourceUid { get; init; }
    public required DateTimeOffset LastAttemptAt { get; init; }
    public required SourceImportOutcome LastOutcome { get; init; }
    public Guid? LastSuccessfulVersionUid { get; init; }
    public DateTimeOffset? LastSuccessfulAt { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed record SourceObservedResource : Resource
{
    public required SourceObservedProperties Definition { get; init; }
}

public sealed record SourceImportRecordProperties
{
    public required Guid SourceUid { get; init; }
    public required DateTimeOffset AttemptedAt { get; init; }
    public required SourceImportOutcome Outcome { get; init; }
    public string? DeclaredVersion { get; init; }
    public string? ManifestDigest { get; init; }
    public Guid? SourceVersionUid { get; init; }
    public SourceManifestOrigin? Origin { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed record SourceImportRecordResource : Resource
{
    public required SourceImportRecordProperties Definition { get; init; }
}

public sealed record ParsedSourceManifest(
    PublishedSourceVersionManifest Manifest,
    string RawManifest,
    string Digest);

public sealed record RetrievedSourceManifest(
    string Content,
    SourceManifestOrigin Origin);

public interface ISourceManifestReader
{
    ParsedSourceManifest Read(string rawManifest);
}

public interface ISourceManifestRetriever
{
    Task<RetrievedSourceManifest> RetrieveAsync(Uri source, CancellationToken cancellationToken);
}

public sealed record SourceView(
    SourceResource Source,
    SourceConfigurationResource Configuration,
    SourceObservedResource Observed,
    int VersionCount);

public sealed record SourceImportResult(
    SourceView Source,
    SourceVersionResource Version,
    SourceImportOutcome Outcome);
