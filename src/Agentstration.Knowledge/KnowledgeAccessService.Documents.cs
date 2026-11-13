using Agentstration.Knowledge.Contracts;
using Agentstration.Storage.Contracts;

namespace Agentstration.Knowledge;

public sealed partial class KnowledgeAccessService
{
    public async Task<StorageDocumentDescription> DescribeDocumentsAsync(KnowledgeSourceId id, string? snapshotId, CancellationToken ct)
    {
        if (documents is null) throw Error("knowledge_documents_unavailable");
        var source = await SourceAsync(id, false, ct);
        StorageDocumentTarget target;
        if (snapshotId is not null)
        {
            var snapshot = await ExactAsync(source, snapshotId, ct);
            var publication = snapshot.Artifacts.FirstOrDefault()?.Content?.Document?.Publication
                ?? throw Error("knowledge_snapshot_documents_unsupported");
            target = publication.Target;
        }
        else if (source.Definition.Output is { DocumentCollectionUid: { } collection } output)
            target = new(output.Storage, new(Consumer(), source.Address), collection, new(QueryProfiles: null));
        else throw Error("knowledge_snapshot_documents_unsupported");
        target = target with
        {
            Consumer = target.Consumer with { ConsumerScopeRef = Consumer() },
            Policy = target.Policy with { Put = false, Delete = false, Publish = false }
        };
        return await documents.DescribeAsync(target, ct);
    }

    public async Task<KnowledgeAccessResult> QueryDocumentsAsync(KnowledgeSourceId id,
        KnowledgeDocumentQueryRequest request, CancellationToken ct)
    {
        if (documents is null) throw Error("knowledge_documents_unavailable");
        var source = await SourceAsync(id, true, ct);
        AccessToken token;
        KnowledgeSnapshotResource snapshot;
        StorageDocumentQuery query;
        if (request.Cursor is { } cursor)
        {
            if (request.SnapshotId is not null || request.PreparationId is not null || request.Query is not null || request.PageSize is not null)
                throw Error("knowledge_access_cursor_parameters");
            token = Open(cursor, "documents", source);
            query = token.DocumentQuery ?? throw Error("knowledge_access_cursor_invalid");
            if (request.Profile is not null && request.Profile != query.Profile)
                throw Error("knowledge_access_cursor_parameters");
            snapshot = await ContinueAsync(source, token, ct);
        }
        else
        {
            if (request.Profile is null || request.Query is null || request.PageSize is < 1 or > 100)
                throw Error("knowledge_documents_query_invalid");
            var selection = await SelectAsync(source, request.SnapshotId, request.PreparationId, ct);
            if (selection.Pending is not null) return selection.Pending;
            snapshot = selection.Snapshot!;
            token = await SessionAsync(source, snapshot, "documents", ct);
            // A provider cursor is shorter lived than the general Knowledge read lease.
            var cursorExpiry = clock.GetUtcNow().AddMinutes(4);
            token = token with { Expires = token.Expires < cursorExpiry ? token.Expires : cursorExpiry };
            query = new(Guid.NewGuid().ToString("N"), request.Profile, request.Query.Value, request.PageSize ?? 50);
        }
        var references = snapshot.Artifacts.Select(a => a.Content?.Document?.Publication).ToArray();
        if (references.Length == 0 || references.Any(r => r is null)) throw Error("knowledge_snapshot_documents_unsupported");
        var publication = references[0]!;
        if (publication.Publication.Count != snapshot.Artifacts.Count
            || snapshot.Artifacts.Select(a => a.Content!.Document!.DocumentId).Distinct(StringComparer.Ordinal).Count() != snapshot.Artifacts.Count
            || references.Any(r => r!.QueryProfile != publication.QueryProfile || r.Publication != publication.Publication || r.Target.Storage != publication.Target.Storage
            || r.Target.CollectionUid != publication.Target.CollectionUid || r.Target.Publication != publication.Target.Publication))
            throw Error("knowledge_snapshot_documents_inconsistent");
        // This comes from the exact Snapshot evidence, never from the source's current output Storage.
        if (publication.QueryProfile != query.Profile) throw Error("knowledge_snapshot_query_profile_mismatch");
        var target = publication.Target with
        {
            Consumer = publication.Target.Consumer with { ConsumerScopeRef = Consumer() },
            Policy = publication.Target.Policy with { Put = false, Delete = false, Publish = false }
        };
        await documents.ValidatePublicationAsync(publication with { Target = target }, ct);
        var page = await documents.QueryAsync(target, query with { RequestId = Guid.NewGuid().ToString("N") }, ct);
        var next = page.Cursor is null ? null : Seal(token with { DocumentQuery = query with { Cursor = page.Cursor } });
        if (next is { Length: > 16384 }) throw Error("knowledge_access_cursor_limit");
        return new()
        {
            State = "ready",
            SourceUid = source.Uid,
            SnapshotId = snapshot.Name,
            PublishedAt = snapshot.PublishedAt,
            DocumentPage = page with { Cursor = null },
            QueryProfile = query.Profile,
            NextCursor = next,
            CursorExpiresAt = next is null ? null : token.Expires,
            EndOfSelection = next is null
        };
    }
}
