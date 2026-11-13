using System.Text.Json;

namespace Agentstration.Storage.Contracts;

public sealed record StorageQueryProfile(string Id, string Version, string Encoding);
public sealed record StorageDocumentCapabilities(bool Get, bool Put, bool Delete, bool Query,
    bool ImmutablePublications, IReadOnlyList<StorageQueryProfile> QueryProfiles);
public sealed record StorageDocumentPolicy(bool Get = true, bool Put = false, bool Delete = false,
    bool Query = true, bool Publish = false, int MaximumPageSize = 50, int MaximumDocumentBytes = 65536,
    int MaximumResultBytes = 196608, IReadOnlyList<string>? QueryProfiles = null);
public sealed record StorageDocumentCollection
{
    public Guid Uid { get; init; }
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public bool Enabled { get; init; } = true;
    public JsonElement Schema { get; init; } = JsonSerializer.SerializeToElement(new { type = "object" });
    public StorageDocumentPolicy Policy { get; init; } = new(Query: false, QueryProfiles: []);
}
public sealed record StorageDocumentTarget(StorageRevisionReference Storage, StorageResolutionContext Consumer,
    Guid CollectionUid, StorageDocumentPolicy Policy, string? Publication = null);
public sealed record StorageQueryProfilePin(string Id, string Version, string Encoding, string ModelDigest);
public sealed record StorageDocumentValue(string Id, string Revision, JsonElement Value);
public sealed record StorageDocumentQuery(string RequestId, StorageQueryProfilePin Profile, JsonElement Query,
    int PageSize = 50, string? Cursor = null);
public sealed record StorageDocumentPage(IReadOnlyList<StorageDocumentValue> Documents, IReadOnlyList<JsonElement> Groups, string? Cursor = null);
public sealed record StorageDocumentWrite(string RequestId, DateTimeOffset NotAfter, string Id, JsonElement Value, string? ExpectedRevision = null);
public sealed record StorageDocumentDelete(string RequestId, DateTimeOffset NotAfter, string Id, string ExpectedRevision);
public sealed record StorageDocumentPublication(string Id, string Digest, int Count, string SchemaDigest);
public sealed record StorageDocumentPublicationReference(StorageDocumentTarget Target, StorageDocumentPublication Publication,
    StorageQueryProfilePin QueryProfile);
public sealed record StorageDocumentDescription(StorageDocumentCapabilities Capabilities, StorageDocumentPolicy Policy,
    IReadOnlyList<StorageQueryProfilePin> QueryProfiles, JsonElement Schema, string SchemaDigest);

/// <summary>Shared API/Flow/Tool boundary. Metadata inspection performs no physical I/O or Secret resolution.</summary>
public interface IStorageDocumentGateway
{
    Task<StorageDocumentDescription> DescribeAsync(StorageDocumentTarget target, CancellationToken ct);
    Task<StorageDocumentValue> GetAsync(StorageDocumentTarget target, string id, string? revision, CancellationToken ct);
    Task<StorageDocumentValue> PutAsync(StorageDocumentTarget target, StorageDocumentWrite request, CancellationToken ct);
    Task<StorageDocumentValue> DeleteAsync(StorageDocumentTarget target, StorageDocumentDelete request, CancellationToken ct);
    Task<StorageDocumentPage> QueryAsync(StorageDocumentTarget target, StorageDocumentQuery request, CancellationToken ct);
    Task<StorageDocumentPublicationReference> PrepareAsync(StorageDocumentTarget target, string publicationId,
        IReadOnlyList<StorageDocumentValue> documents, string requestId, DateTimeOffset notAfter, CancellationToken ct);
    Task SealAsync(StorageDocumentPublicationReference reference, CancellationToken ct);
    Task ValidatePublicationAsync(StorageDocumentPublicationReference reference, CancellationToken ct);
    Task RetireAsync(StorageDocumentPublicationReference reference, CancellationToken ct);
}
