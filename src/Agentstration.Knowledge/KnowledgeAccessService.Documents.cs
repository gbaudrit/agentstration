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
            KnowledgeSnapshotResource snapshot;
            try { snapshot = (await snapshots.GetAsync(new(snapshotId, source.Namespace), ct)).Snapshot; }
            catch (KnowledgeSnapshotException exception) { throw Error(exception.Code); }
            if (snapshot.KnowledgeSourceUid != source.Uid
                || snapshot.KnowledgeSourceScopeRef is { } pinnedScope && pinnedScope != source.ScopeRef)
                throw Error("knowledge_snapshot_source_mismatch");
            var publication = snapshot.Artifacts.FirstOrDefault()?.Content?.Records?.DocumentPublication;
            publication = publication
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
            ContinueContribution(token, request.Contribution);
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
            _ = SelectContribution(snapshot, request.Contribution);
            token = (await SessionAsync(source, snapshot, "documents", ct)) with { Contribution = request.Contribution };
            // A provider cursor is shorter lived than the general Knowledge read lease.
            var cursorExpiry = clock.GetUtcNow().AddMinutes(4);
            token = token with { Expires = token.Expires < cursorExpiry ? token.Expires : cursorExpiry };
            query = new(Guid.NewGuid().ToString("N"), request.Profile, request.Query.Value, request.PageSize ?? 50);
        }
        var members = snapshot.Artifacts.SelectMany(a => a.Content is { } content ? StorageRecordSets.Members(content) : []).ToArray();
        var references = members.Select(content => content.Document?.Publication).ToArray();
        if (snapshot.Artifacts.Any(a => a.Content?.Records?.DocumentPublication is null)) throw Error("knowledge_snapshot_documents_unsupported");
        if (references.Length == 0 || references.Any(r => r is null)) throw Error("knowledge_snapshot_documents_unsupported");
        var publication = references[0]!;
        if (publication.Publication.Count != members.Length
            || members.Select(content => content.Document!.DocumentId).Distinct(StringComparer.Ordinal).Count() != members.Length
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
        var selectedSnapshot = SelectContribution(snapshot, request.Contribution);
        if (request.Contribution is not null)
            target = target with
            {
                DocumentIds = selectedSnapshot.Artifacts.SelectMany(a => StorageRecordSets.Members(a.Content!))
                .Select(member => member.Document!.DocumentId).ToArray()
            };
        var page = await documents.QueryAsync(target, query with { RequestId = Guid.NewGuid().ToString("N") }, ct);
        var lineage = selectedSnapshot.Artifacts.SelectMany(artifact => StorageRecordSets.Members(artifact.Content!)
            .Select((member, index) => new KnowledgeDocumentRecord(member.Document!.DocumentId, artifact.ArtifactId, index)))
            .ToDictionary(member => member.DocumentId, StringComparer.Ordinal);
        if (page.Documents.Any(document => !lineage.ContainsKey(document.Id)))
            throw Error("knowledge_snapshot_documents_outside");
        var next = page.Cursor is null ? null : Seal(token with { DocumentQuery = query with { Cursor = page.Cursor } });
        if (next is { Length: > 16384 }) throw Error("knowledge_access_cursor_limit");
        return new()
        {
            Contribution = request.Contribution,
            DocumentArtifacts = page.Documents.ToDictionary(d => d.Id, d => lineage[d.Id].ArtifactId),
            State = "ready",
            SourceUid = source.Uid,
            SnapshotId = snapshot.Name,
            PublishedAt = snapshot.PublishedAt,
            DocumentPage = page with { Cursor = null },
            QueryProfile = query.Profile,
            Records = page.Documents.Select(document => lineage[document.Id]).ToArray(),
            NextCursor = next,
            CursorExpiresAt = next is null ? null : token.Expires,
            EndOfSelection = next is null
        };
    }
}
