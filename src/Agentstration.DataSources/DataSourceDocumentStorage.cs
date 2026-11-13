using System.Security.Cryptography;
using System.Text.Json;
using Agentstration.DataSources.Contracts;
using Agentstration.Storage.Contracts;

namespace Agentstration.DataSources;

/// <summary>Retains one exact JSON result per acquisition through governed document publications.</summary>
public sealed class DataSourceDocumentStorage(IStorageResolver storages, IStorageDocumentGateway documents,
    IStoragePublicationGateway publications)
{
    public async Task ValidateAsync(StorageRevisionReference storage, StorageResolutionContext consumer,
        DataSourceDatasetProperties dataset, CancellationToken ct)
    {
        if (!dataset.Rule.Read || !dataset.Rule.Write)
            throw Invalid("data_source_storage_capability_required", "Acquisition requires read and publication rights.");
        var target = await TargetAsync(storage, consumer, dataset.DocumentCollectionUid!.Value, ct);
        var description = await documents.DescribeAsync(target, ct);
        if (!description.Capabilities.ImmutablePublications || !description.Policy.Get || !description.Policy.Publish
            || description.QueryProfiles.Count == 0)
            throw Invalid("data_source_document_publication_required", "Acquisition requires an immutable document publication and exact reads.");
    }

    public static byte[] Encode(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw Invalid("data_source_document_object_required", "Structured acquisition requires a JSON object. Normalize other results with a transformation Flow.");
        try { return StorageDocumentEncoding.Encode(value); }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or FormatException)
        { throw Invalid("data_source_document_invalid", "The acquired JSON exceeds the document encoding profile."); }
    }

    public async Task<StoragePublicationReference> PrepareAsync(DataSourceAcquisitionResource acquisition,
        StorageContentRequest contentTarget, JsonElement value, byte[] bytes, CancellationToken ct)
    {
        if (acquisition.Uid == Guid.Empty || acquisition.DataSourceUid == Guid.Empty || acquisition.WriteNotAfter is null)
            throw Invalid("data_source_invocation_invalid", "Structured publication requires an accepted acquisition identity and deadline.");
        var target = await TargetAsync(contentTarget.Storage, contentTarget.Consumer, contentTarget.DocumentCollectionUid!.Value, ct);
        if (bytes.Length > target.Policy.MaximumDocumentBytes || bytes.Length > contentTarget.Policy.MaximumWriteBytes)
            throw Invalid("data_source_output_too_large", "The acquired JSON exceeds the collection or dataset limit.");
        var id = "dataset-" + acquisition.DataSourceUid.ToString("N") + "-" + acquisition.Uid.ToString("N");
        var documentId = acquisition.Uid.ToString("N");
        var publication = await documents.PrepareAsync(target, id, [new(documentId, string.Empty, value)],
            "dataset-" + acquisition.Uid.ToString("N"), acquisition.WriteNotAfter!.Value, ct);
        var resolved = await storages.ResolveAsync(contentTarget.Storage, contentTarget.Consumer, ct);
        var reference = new StoragePublicationReference(contentTarget, documentId, publication.Publication.Digest,
            bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)))
        {
            Binding = new(resolved.Type, resolved.ConnectedService, resolved.ConnectedServiceType,
                StorageDigests.Schema(JsonSerializer.SerializeToElement(resolved.Bindings)),
                StorageDigests.Schema(JsonSerializer.SerializeToElement(resolved.Documents)), "asp.document-publication/1"),
            Document = new(publication, documentId, publication.Publication.Digest)
        };
        await publications.SealAsync(reference, ct);
        return reference;
    }

    public async Task<StorageObjectContent> ReadAsync(StoragePublicationReference reference, StorageObject item,
        long offset, int maximumBytes, CancellationToken ct)
    {
        if (offset < 0 || offset > reference.Length || maximumBytes < 1 || maximumBytes > reference.Target.Policy.MaximumReadBytes)
            throw Invalid("data_source_content_range_invalid", "The acquired content read exceeds its bounds.");
        var bytes = await publications.ReadAsync(reference, offset, maximumBytes, ct);
        return new(item, offset, Convert.ToBase64String(bytes.Span), offset + bytes.Length == reference.Length);
    }

    private async Task<StorageDocumentTarget> TargetAsync(StorageRevisionReference storage, StorageResolutionContext consumer,
        Guid collectionUid, CancellationToken ct)
    {
        var resolved = await storages.ResolveAsync(storage, consumer, ct);
        var collection = resolved.Collections.SingleOrDefault(c => c.Uid == collectionUid && c.Enabled)
            ?? throw Invalid("data_source_document_collection_unavailable", "The selected acquisition collection is unavailable.");
        return new(storage, consumer, collectionUid, collection.Policy with { Put = false, Delete = false });
    }

    private static DataSourceValidationException Invalid(string code, string message) => new(code, message);
}
