using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Artifacts.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Knowledge;
using Agentstration.Knowledge.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Schemas;
using Agentstration.Storage;
using Agentstration.Storage.Contracts;

namespace Agentstration.Infrastructure.Knowledge;

public sealed class KnowledgeOutputPublisher(IStoragePublicationGateway publications, IResourceStore resources,
    IArtifactDurableStore durable, IAuthorizationService authorization, ICurrentRequestContext current, IRequestContextScopeFactory scopes,
    IStoredArtifactRegistrar artifacts, IStorageDocumentGateway? documents = null, IStorageResolver? storages = null,
    TimeProvider? clock = null, StorageContentOptions? options = null) : IKnowledgeOutputPublisher
{
    public async Task<IReadOnlyList<KnowledgeSnapshotArtifact>> PublishAsync(KnowledgeSourceResource source,
        KnowledgeProjectionResource projection, KnowledgeProjectionManifest manifest, CancellationToken cancellationToken)
    {
        var output = projection.Output ?? throw new KnowledgeProjectionException("knowledge_output_required", "An output Storage is required.");
        var consumer = current.AccessMode switch
        {
            ControlPlaneAccessMode.Instance => ResourceScopeRef.Instance,
            ControlPlaneAccessMode.TenantAdministration => ResourceScopeRef.Tenant(current.Current.TenantId),
            _ => ResourceScopeRef.Workspace(current.Current.WorkspaceId)
        };
        var target = new StorageContentRequest(output.Storage, new(consumer, source.Address),
            output.Rule with { Prefix = output.Rule.Prefix + "knowledge/" + source.Uid.ToString("N") + "/" });
        var structured = new List<StorageDocumentValue>();
        var recordMembers = new Dictionary<string, IReadOnlyList<StorageRecordMember>>(StringComparer.Ordinal);
        var result = new List<KnowledgeSnapshotArtifact>();
        foreach (var item in manifest.Artifacts)
        {
            var contract = item.DataContract;
            var recordContract = item.RecordContract;
            ReadOnlyMemory<byte> bytes;
            var mediaType = item.MediaType ?? "application/octet-stream";
            if (item.Kind == KnowledgeArtifactKind.Inline)
            {
                if (item.ContentBase64 is not { } encoded || encoded.Length > (((options ?? new()).MaximumObjectBytes + 2L) / 3) * 4)
                    throw new KnowledgeProjectionException("knowledge_output_invalid", "Inline content is required and must be bounded.");
                bytes = Convert.FromBase64String(encoded);
            }
            else
            {
                await authorization.EnsurePermissionAsync(current.Current, AuthorizationPermissions.ArtifactsReadContent, cancellationToken);
                FlowRunArtifactResource artifact;
                using (scopes.Push(current.Current))
                    artifact = (await resources.GetExactAsync<FlowRunArtifactResource>(ScopedResourceAddress.Create(
                    ResourceScopeRef.Workspace(current.Current.WorkspaceId), ResourceNamespace.Default,
                    ArtifactResourceKinds.FlowRunArtifact, FlowRunArtifactId.Parse(item.ArtifactId).ToString()), cancellationToken))?.Value
                    ?? throw new KnowledgeProjectionException("knowledge_output_artifact_missing", "The output Artifact is unavailable.");
                if (artifact.Receipt.Length > (options ?? new()).MaximumObjectBytes) throw new KnowledgeProjectionException("knowledge_output_too_large", "The output exceeds the publication limit.");
                bytes = await durable.ReadAsync(new(current.Current.WorkspaceId), artifact.Receipt.OpaqueReference, 0, (int)artifact.Receipt.Length, cancellationToken);
                if (bytes.Length != artifact.Receipt.Length) throw new KnowledgeProjectionException("knowledge_output_incomplete", "The output Artifact is incomplete.");
                if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes.Span)), artifact.Receipt.Sha256,
                    StringComparison.OrdinalIgnoreCase))
                    throw new KnowledgeProjectionException("knowledge_output_integrity", "The durable Artifact content differs from its receipt.");
                mediaType = artifact.Receipt.MediaType;
                contract = artifact.Receipt.DataContract ?? contract;
                recordContract = artifact.Receipt.Materialization?.RecordContract ?? recordContract;
            }
            var digest = Convert.ToHexStringLower(SHA256.HashData(bytes.Span));
            if (item.Digest is { } expected && !string.Equals(expected.Replace("sha256:", "", StringComparison.OrdinalIgnoreCase), digest, StringComparison.OrdinalIgnoreCase))
                throw new KnowledgeProjectionException("knowledge_output_integrity", "The output digest differs from the declared content.");
            var sourceDigest = digest;
            StoragePublicationReference? reference = null;
            JsonElement[]? records = null;
            if (recordContract is null && (output.DocumentCollectionUid is not null || mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)))
            {
                using var document = JsonDocument.Parse(bytes);
                JsonRecordProjection normalized;
                try { normalized = JsonRecordProjection.Normalize(document.RootElement, contract); }
                catch (ArgumentException) { throw new KnowledgeProjectionException("knowledge_output_records_required", "JSON publication requires an object or an explicit json.explode record collection."); }
                recordContract = normalized.RecordContract;
                bytes = StorageRecordSets.Encode(normalized.Value.EnumerateArray().ToArray());
            }
            if (recordContract is not null)
            {
                using var document = JsonDocument.Parse(bytes);
                var projectionRecords = new JsonRecordProjection { Value = document.RootElement.Clone(), RecordContract = recordContract, SelectionPath = "" };
                projectionRecords.Validate(cancellationToken);
                records = document.RootElement.EnumerateArray().Select(value => value.Clone()).ToArray();
                bytes = StorageRecordSets.Encode(records);
                contract = projectionRecords.CollectionContract;
                digest = StorageRecordSets.Digest(bytes.Span);
                mediaType = "application/json";
            }
            if (output.DocumentCollectionUid is not null)
            {
                var id = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(item.ArtifactId)));
                var members = new List<StorageRecordMember>();
                for (var index = 0; index < records!.Length; index++)
                {
                    var memberId = id + "-" + index.ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
                    var encoded = StorageDocumentEncoding.Encode(records[index]);
                    structured.Add(new(memberId, string.Empty, records[index]));
                    members.Add(new(memberId, encoded.Length, StorageRecordSets.Digest(encoded)));
                }
                recordMembers.Add(item.ArtifactId, members);
                mediaType = "application/json";
            }
            if (contract is not null)
            {
                var concrete = DataContracts.Declared(contract.Schema, contract.Origin, contract.MediaType)!;
                _ = DataContracts.ReadText(concrete, Encoding.UTF8.GetString(bytes.Span), "snapshot-output", projection.ProjectionFlowRunId);
                contract = concrete with { DeclaredMode = contract.DeclaredMode, Validated = true, SchemaEvidence = contract.SchemaEvidence };
            }
            if (output.DocumentCollectionUid is null) reference = records is not null
                ? await publications.PrepareRecordsAsync(target, projection.Name + ":" + item.ArtifactId, records, cancellationToken)
                : await publications.PrepareAsync(target, projection.Name + ":" + item.ArtifactId, bytes, cancellationToken);
            result.Add(new()
            {
                ArtifactId = item.ArtifactId,
                Content = reference,
                DataContract = contract,
                RecordContract = recordContract,
                Length = bytes.Length,
                Sha256 = digest,
                MediaType = mediaType,
                ProducerFlowRunId = projection.ProjectionFlowRunId,
                ProducerFlowStepId = string.Empty,
                StorageFlowRunId = string.Empty,
                Provenance = output.DocumentCollectionUid is null
                    ? new Dictionary<string, string>(item.Provenance) { ["publication"] = projection.Name }
                    : new Dictionary<string, string>(item.Provenance)
                    {
                        ["publication"] = projection.Name,
                        ["sourceArtifactId"] = item.ArtifactId,
                        ["sourceSha256"] = sourceDigest,
                        ["canonicalization"] = "invariant-json/1"
                    }
            });
        }
        if (output.DocumentCollectionUid is { } collection)
        {
            if (structured.Count is < 1 or > 100)
                throw new KnowledgeProjectionException("knowledge_output_document_limit", "A structured publication contains between one and one hundred documents.");
            if (documents is null || storages is null) throw new KnowledgeProjectionException("knowledge_documents_unavailable", "The structured publication services are unavailable.");
            var resolved = await storages.ResolveAsync(output.Storage, target.Consumer, cancellationToken);
            var declaration = resolved.Collections.SingleOrDefault(c => c.Uid == collection && c.Enabled)
                ?? throw new KnowledgeProjectionException("knowledge_output_collection_unavailable", "The exact output collection is unavailable.");
            var documentTarget = new StorageDocumentTarget(output.Storage, target.Consumer, collection, declaration.Policy);
            var publicationId = "knowledge-" + projection.Uid.ToString("N");
            var publication = await documents.PrepareAsync(documentTarget, publicationId, structured, Guid.NewGuid().ToString("N"),
                (clock ?? TimeProvider.System).GetUtcNow().AddMinutes(4), cancellationToken);
            var binding = new StoragePublicationBinding(resolved.Type, resolved.ConnectedService,
                resolved.ConnectedServiceType, StorageDigests.Schema(JsonSerializer.SerializeToElement(resolved.Bindings)),
                StorageDigests.Schema(JsonSerializer.SerializeToElement(resolved.Documents)), "asp.document-publication/1");
            for (var index = 0; index < result.Count; index++)
            {
                var members = recordMembers[result[index].ArtifactId];
                result[index] = result[index] with
                {
                    Content = new(target with { DocumentCollectionUid = collection },
                            "records-" + StorageRecordSets.Digest(Encoding.UTF8.GetBytes(result[index].ArtifactId)),
                            publication.Publication.Digest, result[index].Length, result[index].Sha256)
                    { Binding = binding, Records = new(members, publication) }
                };
            }
        }
        // Validate and prepare the complete set before sealing any newly written output.
        try
        {
            var sealedPublications = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in result)
            {
                var reference = item.Content!;
                var identity = StorageRecordSets.DocumentPublication(reference)?.Publication.Id ?? reference.Key;
                if (sealedPublications.Add(identity)) await publications.SealAsync(reference, cancellationToken);
            }
        }
        catch (Agentstration.Storage.StorageException exception) when (exception.Code == "storage_publication_unavailable")
        {
            throw new KnowledgeProjectionException("knowledge_projection_publication_pending",
                "The prepared output will resume sealing with its original publication identity.", exception);
        }
        var catalogued = new List<KnowledgeSnapshotArtifact>(result.Count);
        try
        {
            foreach (var item in result)
            {
                var reference = item.Content!;
                FlowRunArtifactResource artifact;
                using (scopes.Push(current.Current))
                    artifact = await artifacts.RegisterStoredAsync(new(ArtifactStorageReferences.PublicationIdentity(reference),
                        projection.ProjectionFlowRunId, new(source.ScopeRef!.Value, source.Namespace, source.Kind, source.Name),
                        projection.Name, item.MediaType, new(new(projection.WorkspaceId), reference.Target,
                            reference.Key, reference.Version, reference.Length, reference.Sha256, reference)
                        { RecordContract = item.RecordContract },
                        ProducerUid: source.Uid, ProducerGeneration: source.Generation, DataContract: item.DataContract,
                        Provenance: item.Provenance), cancellationToken);
                catalogued.Add(item with { ArtifactId = artifact.ArtifactId.ToString() });
            }
        }
        catch (Exception exception) when (exception is ArtifactValidationException or Agentstration.Storage.StorageException
            or ResourceConcurrencyException)
        {
            throw new KnowledgeProjectionException("knowledge_projection_publication_pending",
                "Sealed output will resume Artifact registration with its original publication identities.", exception);
        }
        return catalogued;
    }
}
