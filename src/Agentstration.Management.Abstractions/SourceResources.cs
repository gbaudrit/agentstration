using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.Resources;

namespace Agentstration.Management.Abstractions;

public static class SourceKinds
{
    public const string PublishedSourceVersion = "SourceVersion";
    public const string SourceProvider = "sourceProvider";
}

public sealed class SourceValidationException : Exception
{
    public SourceValidationException(string code, string message, ParsedSourceManifest? parsedManifest = null)
        : base(message)
    {
        Code = code;
        ParsedManifest = parsedManifest;
    }

    public string Code { get; }
    public ParsedSourceManifest? ParsedManifest { get; }
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

[JsonConverter(typeof(JsonStringEnumConverter<SourceChannelCompatibilityStatus>))]
public enum SourceChannelCompatibilityStatus
{
    Compatible,
    Incompatible,
    CompatibilityUnknown
}

public sealed record SourceChannelCompatibilityView(
    SourceChannelCompatibilityStatus Status,
    string? RunningVersion,
    SourceCompatibilityBounds? Declared,
    string? ReasonCode,
    string? Reason);

public interface IAgentstrationVersionProvider
{
    string? CurrentVersion { get; }
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

public static class SourceVerificationKinds
{
    public const string VerifiedSourceIndex = "VerifiedSourceIndex";
}

[JsonConverter(typeof(JsonStringEnumConverter<SourceVerificationStatus>))]
public enum SourceVerificationStatus
{
    [JsonStringEnumMemberName("verified")] Verified,
    [JsonStringEnumMemberName("unverified")] Unverified,
    [JsonStringEnumMemberName("unavailable")] Unavailable
}

public sealed record VerifiedSourceIdentity
{
    public required string Publisher { get; init; }
    public required string Name { get; init; }
}

public sealed record SourceVerificationEvidence
{
    public required string Type { get; init; }
    public required string Authority { get; init; }
    public string? Reference { get; init; }
}

public sealed record VerifiedSourceManifestLocation
{
    public required string Url { get; init; }
    public bool Mutable { get; init; }
}

public sealed record VerifiedSourceChannelDefinition
{
    public required string Name { get; init; }
    public required string Revision { get; init; }
    public required string SnapshotDigest { get; init; }
    public required SourceVerificationEvidence Evidence { get; init; }
}

public sealed record VerifiedSourceDefinition
{
    public required VerifiedSourceIdentity Source { get; init; }
    public required string Version { get; init; }
    public required string ManifestDigest { get; init; }
    public required SourcePublisher Publisher { get; init; }
    public IReadOnlyList<VerifiedSourceManifestLocation> ManifestLocations { get; init; } = [];
    public required SourceVerificationEvidence Evidence { get; init; }
    public IReadOnlyList<VerifiedSourceChannelDefinition> Channels { get; init; } = [];
}

public sealed record VerifiedSourceIndexDefinition
{
    public IReadOnlyList<VerifiedSourceDefinition> Sources { get; init; } = [];
}

public sealed record VerifiedSourceIndexManifest
{
    public required string ApiVersion { get; init; }
    public required string Kind { get; init; }
    public ResourceMetadata Metadata { get; init; } = new();
    public required VerifiedSourceIndexDefinition Definition { get; init; }
}

public sealed record SourceDefinitionVerificationView(
    SourceVerificationStatus Status,
    string ReasonCode,
    SourcePublisher DeclaredPublisher,
    SourcePublisher? VerifiedPublisher,
    SourceVerificationEvidence? Evidence,
    IReadOnlyList<VerifiedSourceManifestLocation> ManifestLocations);

public sealed record SourceChannelSnapshotVerificationView(
    SourceVerificationStatus Status,
    string ReasonCode,
    SourceDefinitionVerificationView Definition,
    string Channel,
    string Revision,
    string SnapshotDigest,
    SourceVerificationEvidence? Evidence);

public interface ISourceVerificationIndexProvider
{
    Task<VerifiedSourceIndexManifest?> GetAsync(CancellationToken cancellationToken);
}

public interface ISourceVerificationIndexReader
{
    VerifiedSourceIndexManifest Read(string content);
}

public sealed record SourceConfigurationProperties
{
    public required Guid SourceUid { get; init; }
    public required string DisplayName { get; init; }
    public SourceManifestOrigin? Origin { get; init; }
    public IReadOnlyList<SourceBindingSelection> Bindings { get; init; } = [];
}

public sealed record SourceBindingSelection
{
    public required string Name { get; init; }
    public required string TargetKind { get; init; }
    public required ResourceReference Target { get; init; }
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

public sealed record SourceContentIntegrity(string Algorithm, string Digest);

public sealed record SourceSnapshotArtifactReference(
    string StorageKey,
    string MediaType,
    string Sha256,
    long Length,
    long ExpandedBytes,
    int EntryCount);

public sealed record SourceProviderSnapshotIdentity
{
    public required Guid Uid { get; init; }
    public required ResourceScopeRef ScopeRef { get; init; }
    public required ResourceNamespace Namespace { get; init; }
    public required string Name { get; init; }
    public required long Generation { get; init; }
    public required string ContributionId { get; init; }
}

public sealed record SourceChannelSnapshotProperties
{
    public required Guid SourceUid { get; init; }
    public required Guid SourceVersionUid { get; init; }
    public required string SourceVersion { get; init; }
    public required string Channel { get; init; }
    public required SourceChannelConfiguration RequestedConfiguration { get; init; }
    public required string ResolvedRevision { get; init; }
    public required SourceContentIntegrity ResolvedIntegrity { get; init; }
    public required SourceProviderSnapshotIdentity Provider { get; init; }
    public required SourceSnapshotArtifactReference Artifact { get; init; }
    public required DateTimeOffset MaterializedAt { get; init; }
}

public sealed record SourceChannelSnapshotResource : Resource
{
    public required SourceChannelSnapshotProperties Definition { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<SourceChannelRefreshOutcome>))]
public enum SourceChannelRefreshOutcome
{
    [JsonStringEnumMemberName("created")] Created,
    [JsonStringEnumMemberName("unchanged")] Unchanged,
    [JsonStringEnumMemberName("failed")] Failed
}

public sealed record SourceChannelObservedProperties
{
    public required Guid SourceUid { get; init; }
    public required Guid SourceVersionUid { get; init; }
    public required string Channel { get; init; }
    public required DateTimeOffset LastAttemptAt { get; init; }
    public required SourceChannelRefreshOutcome LastOutcome { get; init; }
    public Guid? CurrentSnapshotUid { get; init; }
    public string? LastResolvedRevision { get; init; }
    public Guid? LastProviderUid { get; init; }
    public long? LastProviderGeneration { get; init; }
    public DateTimeOffset? LastSuccessfulAt { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed record SourceChannelObservedResource : Resource
{
    public required SourceChannelObservedProperties Definition { get; init; }
}

public sealed record SourceChannelSnapshotReference(
    Guid SnapshotUid,
    Guid SourceVersionUid,
    string Channel,
    string Digest);

public sealed record SourceChannelRefreshResult(
    SourceChannelRefreshOutcome Outcome,
    SourceChannelSnapshotResource Snapshot,
    SourceChannelObservedResource Observed,
    SourceChannelSnapshotReference Pin);

public sealed record SourceChannelStatusView(
    string Channel,
    SourceChannelCompatibilityView Compatibility,
    SourceChannelObservedResource? Refresh);

public sealed record SourceProviderInvocation(
    Uri Endpoint,
    string ContributionId,
    SourceChannelConfiguration Configuration,
    ResourceNamespace Namespace = default,
    ResourceScopeRef? ExtensionScopeRef = null,
    string? ExtensionName = null,
    AepTransportAuthenticationMode AuthenticationMode = AepTransportAuthenticationMode.None,
    ResourceReference? Credential = null,
    string? ExpectedExtensionId = null);

public sealed record ResolvedSourceRevision(
    string Revision,
    SourceContentIntegrity Integrity);

public sealed record MaterializedSourceRevision(
    string Revision,
    string MediaType,
    ReadOnlyMemory<byte> Content,
    long ExpandedBytes,
    int EntryCount,
    SourceContentIntegrity Integrity);

public sealed record SourceMaterializationLimits(
    long MaxArchiveBytes = 16 * 1024 * 1024,
    int MaxEntries = 10_000,
    long MaxExpandedBytes = 64 * 1024 * 1024,
    int TimeoutSeconds = 60);

public interface ISourceProviderMaterializer
{
    Task<ResolvedSourceRevision> ResolveAsync(SourceProviderInvocation invocation, CancellationToken cancellationToken);
    Task<MaterializedSourceRevision> MaterializeAsync(SourceProviderInvocation invocation, string revision, SourceMaterializationLimits limits, CancellationToken cancellationToken);
}

public interface ISourceSnapshotArtifactStore
{
    Task<SourceSnapshotArtifactReference> SaveAsync(MaterializedSourceRevision content, CancellationToken cancellationToken);
    Task<Stream> OpenReadAsync(SourceSnapshotArtifactReference reference, CancellationToken cancellationToken);
}

public static class SourceCatalogKinds
{
    public const string Bootstrap = "BootstrapCatalog";
    public const string Pack = "PackCatalog";
}

public static class SourceCatalogLimits
{
    public const int MaximumManifestBytes = 1024 * 1024;
}

public sealed record BootstrapCatalogVariant
{
    public required string Locale { get; init; }
    public required string Path { get; init; }
}

public sealed record BootstrapCatalogEntry
{
    public required string Name { get; init; }
    public required string DefaultLocale { get; init; }
    public IReadOnlyList<BootstrapCatalogVariant> Variants { get; init; } = [];
}

public sealed record BootstrapCatalogProperties
{
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<BootstrapCatalogEntry> Entries { get; init; } = [];
}

public sealed record BootstrapCatalogManifest
{
    public required string ApiVersion { get; init; }
    public required string Kind { get; init; }
    public ResourceMetadata Metadata { get; init; } = new();
    public required BootstrapCatalogProperties Definition { get; init; }
}

public sealed record PackCatalogEntry
{
    public required string Name { get; init; }
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public required string Path { get; init; }
}

public sealed record PackCatalogProperties
{
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<PackCatalogEntry> Entries { get; init; } = [];
}

public sealed record PackCatalogManifest
{
    public required string ApiVersion { get; init; }
    public required string Kind { get; init; }
    public ResourceMetadata Metadata { get; init; } = new();
    public required PackCatalogProperties Definition { get; init; }
}

public sealed record ParsedSourceCatalog(
    string Kind,
    string Name,
    BootstrapCatalogManifest? Bootstrap,
    PackCatalogManifest? Pack);

public sealed record SourceBootstrapBindingContract(string Name, BootstrapBindingTargetKind TargetKind, bool Required);
public sealed record SourceBootstrapProfileContract(
    string Name,
    BootstrapProfileScope TargetScope,
    IReadOnlyList<SourceBootstrapBindingContract> Bindings);

public interface ISourceCatalogManifestReader
{
    ParsedSourceCatalog ReadCatalog(string content, string declaredKind);
    SourceBootstrapProfileContract ReadBootstrapProfile(string content);
}

public interface ISourceSnapshotContent : IAsyncDisposable
{
    IReadOnlyCollection<string> Paths { get; }
    Task<byte[]> ReadBytesAsync(string normalizedPath, int maximumBytes, CancellationToken cancellationToken);
    Task<string> ReadTextAsync(string normalizedPath, int maximumBytes, CancellationToken cancellationToken);
}

public interface ISourceSnapshotContentReader
{
    Task<ISourceSnapshotContent> OpenAsync(SourceSnapshotArtifactReference reference, CancellationToken cancellationToken);
}

public sealed record SourceCatalogProvenance(
    Guid SourceUid,
    Guid SourceVersionUid,
    string SourceVersion,
    string Channel,
    Guid SnapshotUid,
    string SnapshotDigest,
    string CatalogKind,
    string CatalogName,
    string CatalogPath);

public sealed record SourceBootstrapVariantView(string Locale, string Path);
public sealed record SourceBootstrapEntryView(
    string Name,
    string DefaultLocale,
    IReadOnlyList<SourceBootstrapVariantView> Variants,
    string ResolvedLocale,
    string ResolvedPath);
public sealed record SourcePackEntryView(string Name, string? DisplayName, string? Description, string Path);

public sealed record SourcePackSelection(
    ResourceScopeRef SourceScope,
    string Publisher,
    string SourceName,
    Guid SourceVersionUid,
    string Channel,
    Guid SnapshotUid,
    string CatalogName,
    string EntryName,
    string Path);

public sealed record SourcePackInstallRequest(
    SourcePackSelection Selection,
    bool ReplaceExisting = false,
    bool RemoveDashboardReferences = false,
    IReadOnlyList<PackBindingSelection>? Bindings = null);

public sealed record SourceCatalogView(
    SourceCatalogProvenance Provenance,
    string DisplayName,
    string? Description,
    IReadOnlyList<SourceBootstrapEntryView> BootstrapEntries,
    IReadOnlyList<SourcePackEntryView> PackEntries);

public static class SourceDescendantPath
{
    public static string Normalize(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
            throw new SourceValidationException("source_path_invalid", $"{label} must be a non-empty relative descendant path.");
        if (value.StartsWith('/') || value.StartsWith('\\') || value.Contains('\\') || value.Contains('\0'))
            throw new SourceValidationException("source_path_invalid", $"{label} must use a relative forward-slash path.");
        var segments = value.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".." || segment.Contains(':') || segment.Any(char.IsControl)))
            throw new SourceValidationException("source_path_invalid", $"{label} contains an empty, absolute, current, or parent segment.");
        return string.Join('/', segments);
    }

    public static string Combine(string? parent, string descendant, string label)
    {
        var child = Normalize(descendant, label);
        return string.IsNullOrEmpty(parent) ? child : $"{Normalize(parent, label)}/{child}";
    }

    public static string? Parent(string normalizedPath)
    {
        var index = normalizedPath.LastIndexOf('/');
        return index < 0 ? null : normalizedPath[..index];
    }
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
    SourceImportOutcome Outcome,
    SourceDefinitionVerificationView Verification);
