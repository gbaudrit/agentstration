using System.Runtime.CompilerServices;
using Agentstration.Resources;
using Agentstration.SystemGraph.Contracts;

namespace Agentstration.ResourceManagement;

/// <summary>Metadata helpers shared by typed family contributors; no manifest reflection.</summary>
public static class ResourceSystemGraph
{
    public static SystemGraphNode Publication(Resource resource, Guid ownerUid, string ownerKind, string ownerName,
        string version, string? hash = null, string? contractHash = null, ResourceScopeRef? ownerScope = null, bool hasNativeUid = true,
        long? generation = null, DateTimeOffset? publishedAt = null) => new(
        SystemGraphIds.Publication(ownerUid, version),
        new(ownerUid, ownerScope ?? resource.ScopeRef!.Value, resource.Namespace, ownerKind, ownerName),
        "publication", new(Generation: generation, Publication: version, Hash: hash, OriginKind: resource.Governance.Origin.Kind.ToString())
        { PublicationUid = hasNativeUid ? resource.Uid : null, ContractHash = contractHash, PublishedAt = publishedAt });

    public static SystemGraphNode Node(Resource resource, string? id = null, string state = "definition", string? publication = null, string? hash = null) => new(
        id ?? SystemGraphIds.Definition(resource.Uid),
        new(resource.Uid, resource.ScopeRef ?? throw new InvalidOperationException("Resource owner scope is missing."), resource.Namespace, resource.Kind, resource.Name),
        state, new(resource.Generation, resource.ETag, publication, hash, resource.Governance.Origin.Kind.ToString()));

    public static async IAsyncEnumerable<StoredResource<T>> ReadAsync<T>(IResourceStore store, ResourceScopeRef scope,
        string kind, [EnumeratorCancellation] CancellationToken ct) where T : Resource
    {
        const int pageSize = 200;
        for (var offset = 0; ; offset += pageSize)
        {
            ct.ThrowIfCancellationRequested();
            var page = await store.ListExactAsync<T>(scope, kind, offset, pageSize, ct);
            foreach (var item in page) yield return item;
            if (page.Count < pageSize) yield break;
        }
    }
}
