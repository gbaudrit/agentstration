using Agentstration.DataSources.Contracts;
using Agentstration.Storage.Contracts;

namespace Agentstration.DataSources;

/// <summary>Validates document acquisition destinations and reads exact record publications.</summary>
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

    public async Task<StorageObjectContent> ReadAsync(StoragePublicationReference reference, StorageObject item,
        long offset, int maximumBytes, CancellationToken ct)
    {
        if (offset < 0 || offset > reference.Length || maximumBytes is < 1 or > StorageRecordSets.MaximumBytes
            || reference.Target.Policy.MaximumReadBytes < 1)
            throw Invalid("data_source_content_range_invalid", "The acquired content read exceeds its bounds.");
        var bytes = await publications.ReadAsync(reference, offset, Math.Min(maximumBytes, reference.Target.Policy.MaximumReadBytes), ct);
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
