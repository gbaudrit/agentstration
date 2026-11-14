using System.Security.Cryptography;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Storage;
using Agentstration.Storage.Contracts;

namespace Agentstration.Infrastructure.Storage;

/// <summary>Retains the established Artifact publication boundary while selecting the actual representation explicitly.</summary>
public sealed class StoragePublicationGateway(LocalStoragePublicationGateway local, IStorageDocumentGateway documents,
    IStorageResolver storages, ICurrentRequestContext current) : IStoragePublicationGateway
{
    public async Task ValidateAsync(StorageContentRequest target, CancellationToken ct)
    {
        if (target.DocumentCollectionUid is not { } collection) { await local.ValidateAsync(target, ct); return; }
        var resolved = await storages.ResolveAsync(target.Storage, target.Consumer, ct);
        var declaration = resolved.Collections.SingleOrDefault(c => c.Uid == collection && c.Enabled)
            ?? throw new StorageException("storage_collection_unavailable", "The structured output collection is unavailable.");
        var description = await documents.DescribeAsync(new(target.Storage, target.Consumer, collection, declaration.Policy), ct);
        if (!description.Capabilities.ImmutablePublications || !description.Policy.Publish || !description.Policy.Get
            || !description.Policy.Query || description.QueryProfiles.Count == 0)
            throw new StorageException("storage_document_publication_unsupported", "Structured output requires retained immutable publication, read and a compatible query profile.");
    }
    public Task<StoragePublicationReference> PrepareAsync(StorageContentRequest target, string id, ReadOnlyMemory<byte> content, CancellationToken ct)
    {
        if (target.DocumentCollectionUid is not null) throw new StorageException("storage_document_publication_required", "Structured output uses the document publication pipeline.");
        return local.PrepareAsync(target, id, content, ct);
    }
    public Task SealAsync(StoragePublicationReference reference, CancellationToken ct) => reference.Document is { } document
        ? documents.SealAsync(document.Publication, ct) : local.SealAsync(reference, ct);
    public async Task<ReadOnlyMemory<byte>> ReadAsync(StoragePublicationReference reference, long offset, int length, CancellationToken ct)
    {
        if (reference.Document is not { } document) return await local.ReadAsync(reference, offset, length, ct);
        if (reference.Target.Storage != document.Publication.Target.Storage || reference.Target.DocumentCollectionUid != document.Publication.Target.CollectionUid
            || reference.Key != document.DocumentId || reference.Version != document.Revision || reference.Length is < 0 or > 65536
            || offset < 0 || offset > reference.Length || length < 1)
            throw new StorageException("storage_document_reference_invalid", "The exact document materialization evidence is inconsistent.");
        var target = ReadTarget(document.Publication.Target);
        await documents.ValidatePublicationAsync(document.Publication with { Target = target }, ct);
        var result = await documents.GetAsync(target, document.DocumentId, document.Revision, ct);
        var bytes = StorageDocumentEncoding.Encode(result.Value);
        if (bytes.Length != reference.Length || Convert.ToHexStringLower(SHA256.HashData(bytes)) != reference.Sha256)
            throw new StorageException("storage_document_integrity", "The structured document differs from its Artifact evidence.");
        return bytes.AsMemory((int)offset, (int)Math.Min(length, bytes.Length - offset));
    }
    public async Task<bool> RetireAsync(StoragePublicationReference reference, CancellationToken ct)
    {
        if (reference.Document is not { } document) return await local.RetireAsync(reference, ct);
        await documents.RetireAsync(document.Publication, ct); return true;
    }
    private StorageDocumentTarget ReadTarget(StorageDocumentTarget target)
    {
        var scope = current.AccessMode switch
        {
            ControlPlaneAccessMode.Instance => ResourceScopeRef.Instance,
            ControlPlaneAccessMode.TenantAdministration => ResourceScopeRef.Tenant(current.Current.TenantId),
            _ => ResourceScopeRef.Workspace(current.Current.WorkspaceId)
        };
        return target with
        {
            Consumer = target.Consumer with { ConsumerScopeRef = scope },
            Policy = target.Policy with { Put = false, Delete = false, Publish = false }
        };
    }
}
