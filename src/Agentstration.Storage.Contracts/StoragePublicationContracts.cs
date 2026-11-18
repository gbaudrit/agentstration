using Agentstration.ConnectedServices.Contracts;

namespace Agentstration.Storage.Contracts;

public sealed record StoragePublicationBinding(StorageTypeReference Type,
    ConnectedServiceRevisionReference? ConnectedService, ConnectedServiceTypeReference? ConnectedServiceType,
    string ManualMappingDigest, string CapabilityDigest, string PublicationContract);

/// <summary>A retained, sealed object, independent of expiring content access tokens.</summary>
public sealed record StoragePublicationReference(StorageContentRequest Target, string Key,
    string Version, long Length, string Sha256)
{
    public StoragePublicationBinding? Binding { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public StorageDocumentArtifactReference? Document { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public StorageRecordSet? Records { get; init; }
}
public sealed record StorageRecordMember(string Id, long Length, string Sha256);
public sealed record StorageRecordSet(IReadOnlyList<StorageRecordMember> Members,
    StorageDocumentPublicationReference? DocumentPublication = null);
public sealed record StorageDocumentArtifactReference(StorageDocumentPublicationReference Publication, string DocumentId, string Revision);

/// <summary>Provider-owned publication semantics. Generic read/write capability does not imply support.</summary>
public interface IStoragePublicationGateway
{
    Task<StoragePublicationReference> PrepareRecordsAsync(StorageContentRequest target, string publicationId,
        IReadOnlyList<System.Text.Json.JsonElement> records, CancellationToken cancellationToken, DateTimeOffset? notAfter = null) =>
        throw new NotSupportedException("This publication adapter does not support JSON record sets.");
    Task<bool> RetireAsync(StoragePublicationReference reference, CancellationToken cancellationToken) => Task.FromResult(false);

    Task ValidateAsync(StorageContentRequest target, CancellationToken cancellationToken);
    Task<StoragePublicationReference> PrepareAsync(StorageContentRequest target, string publicationId,
        ReadOnlyMemory<byte> content, CancellationToken cancellationToken);
    Task SealAsync(StoragePublicationReference reference, CancellationToken cancellationToken);
    Task<ReadOnlyMemory<byte>> ReadAsync(StoragePublicationReference reference, long offset, int length,
        CancellationToken cancellationToken);
}
