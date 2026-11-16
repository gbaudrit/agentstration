using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Storage;
using Agentstration.Storage.Contracts;

namespace Agentstration.Infrastructure.Storage;

/// <summary>Retains the established Artifact publication boundary while selecting the actual representation explicitly.</summary>
public sealed class StoragePublicationGateway(LocalStoragePublicationGateway local, IStorageDocumentGateway documents,
    IStorageResolver storages, ICurrentRequestContext current, TimeProvider? clock = null) : IStoragePublicationGateway
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
    public async Task<StoragePublicationReference> PrepareRecordsAsync(StorageContentRequest target, string id,
        IReadOnlyList<JsonElement> records, CancellationToken ct, DateTimeOffset? notAfter = null)
    {
        var aggregate = StorageRecordSets.Encode(records);
        if (aggregate.Length > target.Policy.MaximumWriteBytes)
            throw new StorageException("storage_publication_limit", "The record set exceeds the publication limit.");
        if (target.DocumentCollectionUid is not { } collection) return await local.PrepareRecordsAsync(target, id, records, ct, notAfter);
        await ValidateAsync(target, ct);
        var resolved = await storages.ResolveAsync(target.Storage, target.Consumer, ct);
        var declaration = resolved.Collections.Single(c => c.Uid == collection && c.Enabled);
        var identity = StorageRecordSets.Digest(Encoding.UTF8.GetBytes(id));
        var values = records.Select((record, index) => new StorageDocumentValue(identity + "-" + index.ToString("D3", System.Globalization.CultureInfo.InvariantCulture), string.Empty, record)).ToArray();
        var publication = await documents.PrepareAsync(new(target.Storage, target.Consumer, collection, declaration.Policy),
            "records-" + identity, values, "records-" + identity, notAfter ?? (clock ?? TimeProvider.System).GetUtcNow().AddMinutes(4), ct);
        return new(target, "records-" + identity, publication.Publication.Digest, aggregate.Length, StorageRecordSets.Digest(aggregate))
        {
            Binding = new(resolved.Type, resolved.ConnectedService, resolved.ConnectedServiceType,
                StorageDigests.Schema(JsonSerializer.SerializeToElement(resolved.Bindings)),
                StorageDigests.Schema(JsonSerializer.SerializeToElement(resolved.Documents)), "asp.document-publication/1"),
            Records = new(values.Select(value =>
            {
                var bytes = StorageDocumentEncoding.Encode(value.Value);
                return new StorageRecordMember(value.Id, bytes.Length, StorageRecordSets.Digest(bytes));
            }).ToArray(), publication)
        };
    }
    public async Task SealAsync(StoragePublicationReference reference, CancellationToken ct)
    {
        if (reference.Records is not null)
        {
            _ = StorageRecordSets.Members(reference);
            if (reference.Records.DocumentPublication is { } batch) await documents.SealAsync(batch, ct);
            else foreach (var member in StorageRecordSets.Members(reference)) await local.SealAsync(member, ct);
        }
        else if (reference.Document is { } document) await documents.SealAsync(document.Publication, ct);
        else await local.SealAsync(reference, ct);
    }
    public async Task<ReadOnlyMemory<byte>> ReadAsync(StoragePublicationReference reference, long offset, int length, CancellationToken ct)
    {
        if (reference.Records is not null)
        {
            if (offset < 0 || offset > reference.Length || length < 1 || length > reference.Target.Policy.MaximumReadBytes)
                throw new StorageException("storage_publication_range_invalid", "The record read exceeds its bounds.");
            var members = StorageRecordSets.Members(reference);
            using var aggregate = new MemoryStream();
            aggregate.WriteByte((byte)'[');
            foreach (var member in members)
            {
                if (aggregate.Length > 1) aggregate.WriteByte((byte)',');
                long position = 0;
                do
                {
                    var chunk = await ReadAsync(member, position, (int)Math.Min(member.Length - position, Math.Min(65536, member.Target.Policy.MaximumReadBytes)), ct);
                    if (chunk.Length == 0 || chunk.Length > member.Length - position)
                        throw new StorageException("storage_record_integrity", "The record member is incomplete.");
                    await aggregate.WriteAsync(chunk, ct);
                    position += chunk.Length;
                } while (position < member.Length);
            }
            aggregate.WriteByte((byte)']');
            var aggregateBytes = aggregate.ToArray();
            if (aggregateBytes.Length != reference.Length || StorageRecordSets.Digest(aggregateBytes) != reference.Sha256)
                throw new StorageException("storage_record_integrity", "The logical record collection differs from its evidence.");
            return aggregateBytes.AsMemory((int)offset, (int)Math.Min(length, aggregateBytes.Length - offset));
        }
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
        if (reference.Records is not null)
        {
            var members = StorageRecordSets.Members(reference);
            if (reference.Records.DocumentPublication is { } batch) { await documents.RetireAsync(batch, ct); return true; }
            var success = true;
            foreach (var member in members) success &= await local.RetireAsync(member, ct);
            return success;
        }
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
