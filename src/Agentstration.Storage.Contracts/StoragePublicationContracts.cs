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
}

/// <summary>Provider-owned publication semantics. Generic read/write capability does not imply support.</summary>
public interface IStoragePublicationGateway
{
    Task ValidateAsync(StorageContentRequest target, CancellationToken cancellationToken);
    Task<StoragePublicationReference> PrepareAsync(StorageContentRequest target, string publicationId,
        ReadOnlyMemory<byte> content, CancellationToken cancellationToken);
    Task SealAsync(StoragePublicationReference reference, CancellationToken cancellationToken);
    Task<ReadOnlyMemory<byte>> ReadAsync(StoragePublicationReference reference, long offset, int length,
        CancellationToken cancellationToken);
}
