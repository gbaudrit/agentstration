using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.ResourceManagement;
using Agentstration.Resources;

namespace Agentstration.Infrastructure.Flows;

public sealed class FlowResourceIdentityContributor(IFlowRepository flows) : IResourceIdentityContributor, IResourceAccessGraphContributor
{
    public async Task<ResourceAccessDocument?> GetAccessDocumentAsync(Guid uid, CancellationToken cancellationToken)
    {
        var stored = await flows.GetByUidAsync(uid, cancellationToken);
        return stored is null ? null : AccessDocument(stored);
    }

    public async Task<IReadOnlyList<ResourceAccessDocument>> ListAccessDocumentsAsync(ResourceScopeRef scope, CancellationToken cancellationToken)
    {
        var result = new List<ResourceAccessDocument>();
        while (true)
        {
            var page = await flows.ListExactAsync(scope, result.Count, 200, cancellationToken);
            result.AddRange(page.Items.Select(AccessDocument));
            if (result.Count > 1000) throw new ResourceAccessPolicyException("access_inventory_limit", "The Flow access inventory exceeds its bounded limit.");
            if (!page.HasMore) return result;
        }
    }

    private static ResourceAccessDocument AccessDocument(StoredFlow stored) => new(Identity(stored),
        stored.Value.Governance, JsonSerializer.SerializeToElement(stored.Value), null) { Available = stored.Value.Enabled };

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
                stored.UpdatedAt)
            { IsManaged = stored.Value.Governance.IsManaged }));
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
