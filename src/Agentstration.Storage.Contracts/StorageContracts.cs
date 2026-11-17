using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;

namespace Agentstration.Storage.Contracts;

public static class StorageResourceKinds
{
    public const string StorageType = "StorageType";
    public const string Storage = "Storage";
}

public sealed record StorageRequirementDescriptor(
    string Id, bool Required, ConnectedServiceValueType Type,
    ConnectedServiceValueProtection Protection, string? Format = null);

public sealed record StorageCapabilities
{
    public bool List { get; init; }
    public bool Stat { get; init; }
    public bool Read { get; init; }
    public bool Write { get; init; }
    public bool Delete { get; init; }
    public bool StableVersions { get; init; }
    public bool DurableRetention { get; init; }
}

public sealed record StorageLimits(int MaximumPageSize, int MaximumReadBytes, int MaximumWriteBytes);

/// <summary>Evidence supplied only by the authenticated contributing adapter.</summary>
public sealed record StorageContributionEvidence(
    ScopedResourceAddress Provider, Guid ProviderUid, long ProviderGeneration,
    string ContributionId, string Version, string Digest);

public sealed record StorageTypeVersionDescriptor
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StorageDocumentCapabilities? Documents { get; init; }
    public required string Version { get; init; }
    public required string SchemaDigest { get; init; }
    public required JsonElement ConfigurationSchema { get; init; }
    public required string EnvelopeSchemaDigest { get; init; }
    public required JsonElement EnvelopeSchema { get; init; }
    public bool RequiresConnectedService { get; init; }
    public IReadOnlyList<StorageRequirementDescriptor> Requirements { get; init; } = [];
    public StorageCapabilities Capabilities { get; init; } = new();
    public StorageLimits Limits { get; init; } = new(100, 1024 * 1024, 1024 * 1024);
    public StorageContributionEvidence? Contribution { get; init; }
    public StorageAspBindingDescriptor? Asp { get; init; }
    public bool Deprecated { get; init; }
    public string ContractDigest => StorageDigests.Version(this);
}

public sealed record StorageTypeProperties
{
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public bool Enabled { get; init; } = true;
    public required string PreferredVersion { get; init; }
    public IReadOnlyList<StorageTypeVersionDescriptor> Versions { get; init; } = [];
    public DescendantUsePolicy UsePolicy { get; init; } = new();
}

public sealed record StorageTypeResource : Resource
{
    public StorageTypeProperties Definition { get; init; } = null!;
    public IReadOnlyList<StorageTypeVersionAvailability> ObservedVersions { get; init; } = [];
    public StorageTypeAccessObservation? ObservedUse { get; init; }
}

/// <summary>Request-scoped generic grant decision; not persisted and not a promise of functional usability.</summary>
public sealed record StorageTypeAccessObservation(ResourceScopeRef ConsumerScopeRef, ResourceAccessDecision Use);

public sealed record StorageTypeVersionAvailability(string Version, StorageAvailability Availability);

public sealed record StorageTypeReference
{
    public required ResourceReference Type { get; init; }
    public required Guid TypeUid { get; init; }
    public required string Version { get; init; }
    public required string SchemaDigest { get; init; }
    /// <summary>Digest of the complete immutable version, including requirements and envelope.</summary>
    public required string ContractDigest { get; init; }
}

/// <summary>Explicit consumer confirmation; the export is pinned by complete descriptor.</summary>
public sealed record StorageExportBinding(string RequirementId, ConnectedServiceExportDescriptor Export);

public sealed record StorageProperties
{
    public IReadOnlyList<StorageDocumentCollection> Collections { get; init; } = [];
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public bool Enabled { get; init; } = true;
    public required StorageTypeReference Type { get; init; }
    public ConnectedServiceRevisionReference? ConnectedService { get; init; }
    public ConnectedServiceTypeReference? ConnectedServiceType { get; init; }
    public IReadOnlyList<StorageExportBinding> Bindings { get; init; } = [];
    public required JsonElement Configuration { get; init; }
    public required JsonElement Envelope { get; init; }
    public DescendantUsePolicy UsePolicy { get; init; } = new();
}

[JsonConverter(typeof(JsonStringEnumConverter<StorageAvailability>))]
public enum StorageAvailability
{
    [JsonStringEnumMemberName("ready")] Ready,
    [JsonStringEnumMemberName("stale")] Stale,
    [JsonStringEnumMemberName("unavailable")] Unavailable,
    [JsonStringEnumMemberName("disabled")] Disabled
}

public sealed record StorageDiagnostic(string Code, string Message);
public sealed record StorageObservedState(
    StorageAvailability Availability, DateTimeOffset ObservedAt, string? BindingDigest,
    IReadOnlyList<StorageDiagnostic> Diagnostics);

public sealed record StorageResource : Resource
{
    public StorageProperties Definition { get; init; } = null!;
    public StorageObservedState? ObservedState { get; init; }
}

public sealed record StorageRevisionReference(
    ScopedResourceAddress Storage, Guid StorageUid, long Generation, string BindingDigest);

public sealed record StorageResolutionContext(ResourceScopeRef ConsumerScopeRef, ResourceAddress Consumer);

/// <summary>Only the logical reference is serializable. Adapters must protect the opaque layers.</summary>
public sealed class ProtectedStorageContext(
    StorageRevisionReference reference, StorageTypeReference type,
    ConnectedServiceRevisionReference? connectedService, ConnectedServiceTypeReference? connectedServiceType,
    JsonElement configuration, JsonElement envelope, IReadOnlyList<StorageExportBinding> bindings,
    StorageCapabilities capabilities, StorageLimits limits,
    StorageDocumentCapabilities? documents = null, IReadOnlyList<StorageDocumentCollection>? collections = null)
{
    public StorageRevisionReference Reference { get; } = reference;
    public StorageTypeReference Type { get; } = type;
    public ConnectedServiceRevisionReference? ConnectedService { get; } = connectedService;
    public ConnectedServiceTypeReference? ConnectedServiceType { get; } = connectedServiceType;
    [JsonIgnore] public JsonElement Configuration { get; } = configuration;
    [JsonIgnore] public JsonElement Envelope { get; } = envelope;
    [JsonIgnore] public IReadOnlyList<StorageExportBinding> Bindings { get; } = bindings;
    [JsonIgnore] public StorageCapabilities Capabilities { get; } = capabilities;
    [JsonIgnore] public StorageLimits Limits { get; } = limits;
    [JsonIgnore] public StorageDocumentCapabilities? Documents { get; } = documents;
    [JsonIgnore] public IReadOnlyList<StorageDocumentCollection> Collections { get; } = collections ?? [];
    public override string ToString() => "[REDACTED]";
}

public interface IStorageResolver
{
    Task<ProtectedStorageContext> ResolveAsync(StorageRevisionReference storage,
        StorageResolutionContext context, CancellationToken cancellationToken = default);
}

/// <summary>Metadata-only availability check. Must never resolve values or invoke business operations.</summary>
public interface IStorageTypeAvailabilityProvider
{
    Task<bool> IsAvailableAsync(StorageTypeResource type, StorageTypeVersionDescriptor version,
        CancellationToken cancellationToken);
}

public sealed record StorageUsage(string Kind, string Name, string DisplayName, string Url);
public interface IStorageUsageProvider
{
    Task<IReadOnlyList<StorageUsage>> GetUsagesAsync(ScopedResourceAddress storage,
        CancellationToken cancellationToken = default);
}

public sealed record CreateStorageRequest(string Name, StorageProperties Properties,
    ResourceScopeRef? ScopeRef = null, ResourceNamespace? Namespace = null);
public sealed record PutStorageRequest(StorageProperties Properties);
/// <summary>Null observes drift; properties explicitly accept a complete new binding.</summary>
public sealed record ReconcileStorageRequest(StorageProperties? Properties = null);
public sealed record CreateStorageTypeRequest(string Name, StorageTypeProperties Properties,
    ResourceScopeRef? ScopeRef = null, ResourceNamespace? Namespace = null);
public sealed record PutStorageTypeRequest(StorageTypeProperties Properties);
public sealed record PublishStorageTypeVersionRequest(StorageTypeVersionDescriptor Version, bool SetPreferred = false);
