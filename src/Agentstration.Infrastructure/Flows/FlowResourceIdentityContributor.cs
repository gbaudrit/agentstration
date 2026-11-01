using Agentstration.Flows;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.ResourceManagement;
using Agentstration.Resources;

namespace Agentstration.Infrastructure.Flows;

public sealed class FlowResourceIdentityContributor(IFlowRepository flows) : IResourceIdentityContributor
{
    public async Task<ResourceIdentitySnapshot?> GetIdentityByUidAsync(
        Guid uid,
        CancellationToken cancellationToken)
    {
        var stored = await flows.GetByUidAsync(uid, cancellationToken);
        return stored is null ? null : Identity(stored);
    }

    public async Task<IReadOnlyList<ResourceInventoryEntry>> ListExactInventoryAsync(
        ResourceScopeRef scopeRef,
        CancellationToken cancellationToken)
    {
        const int pageSize = 200;
        var result = new List<ResourceInventoryEntry>();
        var skip = 0;
        while (true)
        {
            var page = await flows.ListExactAsync(scopeRef, skip, pageSize, cancellationToken);
            result.AddRange(page.Items.Select(stored => new ResourceInventoryEntry(
                stored.Value.Uid,
                stored.Value.ScopeRef,
                stored.Value.Id.Namespace,
                FlowResourceKinds.Flow,
                stored.Value.Id.Value,
                stored.UpdatedAt)));
            if (!page.HasMore) return result;
            skip += page.Items.Count;
        }
    }

    private static ResourceIdentitySnapshot Identity(StoredFlow stored) => new(
        stored.Value.Uid,
        stored.Value.ScopeRef,
        stored.Value.Id.Namespace,
        FlowResourceKinds.Flow,
        stored.Value.Id.Value,
        0,
        stored.Value.ActiveVersion ?? stored.Value.Version,
        stored.ETag);
}
