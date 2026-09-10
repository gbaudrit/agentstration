using System.Text.Json.Serialization;

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

public interface ISourceRegistryIndexReader
{
    ParsedSourceRegistryIndex Read(string rawDocument, string fileName, Uri baseUri);
}

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

public interface ISourceRegistryReader
{
    ParsedSourceRegistry Read(string rawDocument, string fileName);
}

public interface ISourceRegistryReferenceResolver
{
    string ResolveRegistryPublicationPath(Uri baseUri, string registryUrl);
}

public static class SourceRegistryWellKnown
{
    public const string OfficialName = "agentstration-official";
    public const string OfficialDisplayName = "Agentstration Registry";
    public const string OfficialIndexUrl = "https://registry.agentstration.io/v1/index.json";
}

public sealed record SourceRegistryRegistrationProperties
{
    public required string DisplayName { get; init; }
    public required Uri IndexUrl { get; init; }
    public bool Enabled { get; init; } = true;
}

public sealed record SourceRegistryRegistrationResource : Resource
{
    public required SourceRegistryRegistrationProperties Definition { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<SourceRegistryObservedStatus>))]
public enum SourceRegistryObservedStatus
{
    [JsonStringEnumMemberName("disabled")] Disabled,
    [JsonStringEnumMemberName("neverFetched")] NeverFetched,
    [JsonStringEnumMemberName("fresh")] Fresh,
    [JsonStringEnumMemberName("stale")] Stale,
    [JsonStringEnumMemberName("refreshFailed")] RefreshFailed,
    [JsonStringEnumMemberName("invalid")] Invalid,
    [JsonStringEnumMemberName("noCompatibleCatalog")] NoCompatibleCatalog
}

[JsonConverter(typeof(JsonStringEnumConverter<SourceRegistryRefreshOutcome>))]
public enum SourceRegistryRefreshOutcome
{
    [JsonStringEnumMemberName("succeeded")] Succeeded,
    [JsonStringEnumMemberName("notModified")] NotModified,
    [JsonStringEnumMemberName("unavailable")] Unavailable,
    [JsonStringEnumMemberName("invalid")] Invalid,
    [JsonStringEnumMemberName("noCompatibleCatalog")] NoCompatibleCatalog,
    [JsonStringEnumMemberName("disabled")] Disabled
}

public sealed record SourceRegistryCatalogObservation
{
    public required string Name { get; init; }
    public required SourceCompatibility Compatibility { get; init; }
    public required string RegistryUrl { get; init; }
    public required string RegistryDigest { get; init; }
    public required string CachePath { get; init; }
}

public sealed record SourceRegistryObservation
{
    public required Guid Id { get; init; }
    public required Uri RequestedIndexUrl { get; init; }
    public required Uri FinalIndexUrl { get; init; }
    public required string IndexDigest { get; init; }
    public string? ETag { get; init; }
    public DateTimeOffset? LastModified { get; init; }
    public required DateTimeOffset FetchedAt { get; init; }
    public required string AgentstrationVersion { get; init; }
    public required IReadOnlyList<SourceRegistryCatalogObservation> Catalogs { get; init; }
}

public sealed record SourceRegistryObservedStateProperties
{
    public required Guid RegistrationUid { get; init; }
    public SourceRegistryObservedStatus Status { get; init; } = SourceRegistryObservedStatus.NeverFetched;
    public DateTimeOffset? LastAttemptedAt { get; init; }
    public DateTimeOffset? LastSuccessfulRefreshAt { get; init; }
    public SourceRegistryRefreshOutcome? LastOutcome { get; init; }
    public string? LastErrorCode { get; init; }
    public string? LastErrorMessage { get; init; }
    public SourceRegistryObservation? Current { get; init; }
}

public sealed record SourceRegistryObservedStateResource : Resource
{
    public required SourceRegistryObservedStateProperties Definition { get; init; }
}

public sealed record SourceRegistryRefreshRecordProperties
{
    public required Guid RegistrationUid { get; init; }
    public required DateTimeOffset AttemptedAt { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
    public required SourceRegistryRefreshOutcome Outcome { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public Guid? ObservationId { get; init; }
    public string? IndexDigest { get; init; }
}

public sealed record SourceRegistryRefreshRecordResource : Resource
{
    public required SourceRegistryRefreshRecordProperties Definition { get; init; }
}

public sealed record SourceRegistryRegistrationView(
    SourceRegistryRegistrationResource Registration,
    SourceRegistryObservedStateResource Observed);

public sealed record RetrievedSourceRegistryDocument(
    Uri RequestedUrl,
    Uri FinalUrl,
    string FileName,
    string? ETag,
    DateTimeOffset? LastModified,
    bool NotModified,
    string? Content);

public interface ISourceRegistryDocumentRetriever
{
    Task<RetrievedSourceRegistryDocument> RetrieveAsync(
        Uri source,
        string? etag,
        DateTimeOffset? lastModified,
        int maximumBytes,
        CancellationToken cancellationToken);
}

public sealed record SourceRegistryCachedPublication(
    Guid ObservationId,
    byte[] Index,
    IReadOnlyDictionary<string, byte[]> Catalogs);

public interface ISourceRegistryCacheStore
{
    Task StoreAsync(SourceRegistryCachedPublication publication, CancellationToken cancellationToken);
    Task<SourceRegistryCachedPublication?> GetAsync(Guid observationId, CancellationToken cancellationToken);
}
