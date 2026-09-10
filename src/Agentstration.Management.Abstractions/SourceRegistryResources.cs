using System.Net;
using System.Net.Sockets;
using System.Text.Json.Serialization;
using Agentstration.Resources;

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

[JsonConverter(typeof(JsonStringEnumConverter<SourceRegistryTrustPolicy>))]
public enum SourceRegistryTrustPolicy
{
    [JsonStringEnumMemberName("untrusted")] Untrusted,
    [JsonStringEnumMemberName("trusted")] Trusted,
    [JsonStringEnumMemberName("authoritative")] Authoritative
}

[JsonConverter(typeof(JsonStringEnumConverter<SourceRegistryAuthenticationMode>))]
public enum SourceRegistryAuthenticationMode
{
    [JsonStringEnumMemberName("none")] None,
    [JsonStringEnumMemberName("staticBearer")] StaticBearer
}

public sealed record SourceRegistryEndpointPolicy
{
    public bool AllowHttp { get; init; }
    public bool AllowPrivateNetwork { get; init; }
}

public static class SourceRegistryNetworkPolicy
{
    public static bool IsAddressAllowed(IPAddress address, bool allowPrivateNetwork)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.Broadcast)
            || address.IsIPv6Multicast || address.IsIPv6LinkLocal)
            return false;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var addressBytes = address.GetAddressBytes();
            if (addressBytes[0] == 0 || addressBytes[0] >= 224
                || addressBytes[0] == 169 && addressBytes[1] == 254)
                return false;
        }
        if (allowPrivateNetwork)
            return !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any) && !address.Equals(IPAddress.Broadcast);
        if (IPAddress.IsLoopback(address)) return false;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return (address.GetAddressBytes()[0] & 0xfe) != 0xfc;
        var bytes = address.GetAddressBytes();
        return bytes[0] != 0
            && bytes[0] != 10
            && bytes[0] != 127
            && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
            && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            && !(bytes[0] == 192 && bytes[1] == 168)
            && !(bytes[0] == 192 && bytes[1] == 0)
            && !(bytes[0] == 169 && bytes[1] == 254)
            && !(bytes[0] == 198 && bytes[1] is 18 or 19)
            && !(bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100)
            && !(bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113)
            && bytes[0] < 224;
    }
}

public sealed record SourceRegistryRefreshPolicy
{
    public bool PeriodicEnabled { get; init; }
    public TimeSpan Interval { get; init; } = TimeSpan.FromHours(24);
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    public int MaximumAttempts { get; init; } = 3;
    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan MaximumBackoff { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan Jitter { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromDays(2);
}

public sealed record SourceRegistryCachePolicy
{
    public int RetainedObservations { get; init; } = 3;
}

public sealed record SourceRegistryRegistrationProperties
{
    public required string DisplayName { get; init; }
    public required Uri IndexUrl { get; init; }
    public bool Enabled { get; init; } = true;
    public SourceRegistryTrustPolicy TrustPolicy { get; init; } = SourceRegistryTrustPolicy.Untrusted;
    public SourceRegistryEndpointPolicy EndpointPolicy { get; init; } = new();
    public SourceRegistryAuthenticationMode AuthenticationMode { get; init; }
    public ResourceReference? Credential { get; init; }
    public SourceRegistryRefreshPolicy RefreshPolicy { get; init; } = new();
    public SourceRegistryCachePolicy CachePolicy { get; init; } = new();
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
    [JsonStringEnumMemberName("noCompatibleCatalog")] NoCompatibleCatalog,
    [JsonStringEnumMemberName("policyDenied")] PolicyDenied,
    [JsonStringEnumMemberName("recovered")] Recovered
}

[JsonConverter(typeof(JsonStringEnumConverter<SourceRegistryRefreshTrigger>))]
public enum SourceRegistryRefreshTrigger
{
    [JsonStringEnumMemberName("manual")] Manual,
    [JsonStringEnumMemberName("scheduled")] Scheduled
}

[JsonConverter(typeof(JsonStringEnumConverter<SourceRegistryRefreshOutcome>))]
public enum SourceRegistryRefreshOutcome
{
    [JsonStringEnumMemberName("succeeded")] Succeeded,
    [JsonStringEnumMemberName("notModified")] NotModified,
    [JsonStringEnumMemberName("unavailable")] Unavailable,
    [JsonStringEnumMemberName("invalid")] Invalid,
    [JsonStringEnumMemberName("noCompatibleCatalog")] NoCompatibleCatalog,
    [JsonStringEnumMemberName("disabled")] Disabled,
    [JsonStringEnumMemberName("policyDenied")] PolicyDenied
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
    public SourceRegistryRefreshTrigger LastTrigger { get; init; } = SourceRegistryRefreshTrigger.Manual;
    public int ConsecutiveFailures { get; init; }
    public int LastRetryCount { get; init; }
    public long LastDurationMilliseconds { get; init; }
    public DateTimeOffset? LastRecoveredAt { get; init; }
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
    public SourceRegistryRefreshTrigger Trigger { get; init; } = SourceRegistryRefreshTrigger.Manual;
    public int RetryCount { get; init; }
    public long DurationMilliseconds { get; init; }
    public string? CorrelationId { get; init; }
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

public sealed record SourceRegistryRetrievalContext(
    ResourceScopeRef ConsumerScopeRef,
    ResourceAddress Consumer,
    SourceRegistryEndpointPolicy EndpointPolicy,
    SourceRegistryAuthenticationMode AuthenticationMode,
    ResourceReference? Credential);

public interface ISourceRegistryDocumentRetriever
{
    Task<RetrievedSourceRegistryDocument> RetrieveAsync(
        Uri source,
        string? etag,
        DateTimeOffset? lastModified,
        int maximumBytes,
        SourceRegistryRetrievalContext context,
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
    Task RemoveAsync(Guid observationId, CancellationToken cancellationToken);
}
