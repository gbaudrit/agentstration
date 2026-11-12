using System.Security.Cryptography;
using System.Text.Json;
using Agentstration.Artifacts.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Knowledge;
using Agentstration.Knowledge.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Storage.Contracts;

namespace Agentstration.Infrastructure.Knowledge;

public sealed class KnowledgeOutputPublisher(IStoragePublicationGateway publications, IResourceStore resources,
    IArtifactDurableStore durable, IAuthorizationService authorization, ICurrentRequestContext current, IRequestContextScopeFactory scopes,
    IStoredArtifactRegistrar artifacts, IStorageDocumentGateway? documents = null, IStorageResolver? storages = null, TimeProvider? clock = null) : IKnowledgeOutputPublisher
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
        var canonical = new Dictionary<string, byte[]>();
        var result = new List<KnowledgeSnapshotArtifact>();
        foreach (var item in manifest.Artifacts)
        {
            ReadOnlyMemory<byte> bytes;
            var mediaType = item.MediaType ?? "application/octet-stream";
            if (item.Kind == KnowledgeArtifactKind.Inline)
            {
                if (item.ContentBase64 is not { Length: <= 87384 } encoded)
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
                if (artifact.Receipt.Length > 65536) throw new KnowledgeProjectionException("knowledge_output_too_large", "The output exceeds the publication limit.");
                bytes = await durable.ReadAsync(new(current.Current.WorkspaceId), artifact.Receipt.OpaqueReference, 0, (int)artifact.Receipt.Length, cancellationToken);
                if (bytes.Length != artifact.Receipt.Length) throw new KnowledgeProjectionException("knowledge_output_incomplete", "The output Artifact is incomplete.");
                if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes.Span)), artifact.Receipt.Sha256,
                    StringComparison.OrdinalIgnoreCase))
                    throw new KnowledgeProjectionException("knowledge_output_integrity", "The durable Artifact content differs from its receipt.");
                mediaType = artifact.Receipt.MediaType;
            }
            var digest = Convert.ToHexStringLower(SHA256.HashData(bytes.Span));
            if (item.Digest is { } expected && !string.Equals(expected.Replace("sha256:", "", StringComparison.OrdinalIgnoreCase), digest, StringComparison.OrdinalIgnoreCase))
                throw new KnowledgeProjectionException("knowledge_output_integrity", "The output digest differs from the declared content.");
            var sourceDigest = digest;
            StoragePublicationReference? reference = null;
            if (output.DocumentCollectionUid is not null)
            {
                using var json = JsonDocument.Parse(bytes);
                if (json.RootElement.ValueKind != JsonValueKind.Object)
                    throw new KnowledgeProjectionException("knowledge_output_document_invalid", "Structured output requires JSON objects.");
                var id = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(item.ArtifactId)));
                var encoded = StorageDocumentEncoding.Encode(json.RootElement);
                canonical.Add(id, encoded);
                structured.Add(new(id, string.Empty, json.RootElement.Clone()));
                bytes = encoded;
                digest = Convert.ToHexStringLower(SHA256.HashData(encoded));
                mediaType = "application/json";
            }
            else reference = await publications.PrepareAsync(target, projection.Name + ":" + item.ArtifactId, bytes, cancellationToken);
            result.Add(new()
            {
                ArtifactId = item.ArtifactId,
                Content = reference,
                Length = bytes.Length,
                Sha256 = digest,
                MediaType = mediaType,
                ProducerFlowRunId = projection.ProjectionFlowRunId,
                ProducerFlowStepId = string.Empty,
                StorageFlowRunId = string.Empty,
                Provenance = output.DocumentCollectionUid is null
                    ? new Dictionary<string, string> { ["publication"] = projection.Name }
                    : new Dictionary<string, string>
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
                var document = structured[index];
                var bytes = canonical[document.Id];
                result[index] = result[index] with
                {
                    Content = new StoragePublicationReference(target with { DocumentCollectionUid = collection }, document.Id,
                        publication.Publication.Digest, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)))
                    {
                        Binding = binding,
                        Document = new(publication, document.Id, publication.Publication.Digest)
                    }
                };
            }
        }
        // Validate and prepare the complete set before sealing any newly written output.
        try
        {
            var sealedPublications = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in result)
            {
                var identity = item.Content!.Document?.Publication.Publication.Id ?? item.Content.Key;
                if (sealedPublications.Add(identity)) await publications.SealAsync(item.Content, cancellationToken);
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
                            reference.Key, reference.Version, reference.Length, reference.Sha256, reference),
                        ProducerUid: source.Uid, ProducerGeneration: source.Generation), cancellationToken);
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
