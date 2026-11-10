using System.Security.Cryptography;
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
    IStoredArtifactRegistrar artifacts) : IKnowledgeOutputPublisher
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
            var reference = await publications.PrepareAsync(target, projection.Name + ":" + item.ArtifactId, bytes, cancellationToken);
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
                Provenance = new Dictionary<string, string> { ["publication"] = projection.Name }
            });
        }
        // Validate and prepare the complete set before sealing any newly written output.
        try
        {
            foreach (var item in result) await publications.SealAsync(item.Content!, cancellationToken);
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
