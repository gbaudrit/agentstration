using System.Text.Json;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;

namespace Agentstration.Tools;

public sealed record ToolOperationalStatus(Guid ToolUid, bool Enabled, string? ETag);
public sealed record ToolProviderGroup(ResourceAccessIdentity? ToolSet, string DisplayName, IReadOnlyList<Guid> ToolUids)
{
    public string? ToolSetName { get; init; }
    public bool Unverified { get; init; }
}
public sealed record ToolProviderAdministration(IReadOnlyList<ToolProviderGroup> Groups, IReadOnlyList<ToolOperationalStatus> States);

public sealed class ToolAdministrationService(IResourceStore store,
    IResourceOperationalStateStore states, ResourceRelatedAccessService related, IResourceScopeOperations operations)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ToolOperationalStatus> GetAsync(ToolResource tool, CancellationToken cancellationToken)
    {
        var state = await states.GetAsync(ToolAccessService.Identity(tool), cancellationToken);
        return new(tool.Uid, state?.Enabled ?? tool.Definition.Enabled, state?.ETag);
    }

    public async Task<ToolProviderAdministration> GetProviderAsync(ToolProviderResource provider, CancellationToken cancellationToken)
    {
        if (store is not IResourceAccessGraphReader reader
            || await reader.GetAccessGraphAsync(provider.Uid, cancellationToken) is not { } graph)
            throw new ToolResourceValidationException("The exact provider access inventory is unavailable.");
        var membership = graph.Describe(provider.Uid);
        if (!membership.Valid || !membership.Collection) throw new ToolResourceValidationException("The provider membership is invalid.");
        var visible = new List<Guid>();
        foreach (var uid in membership.Members)
        {
            var identity = graph.Documents[uid].Identity;
            if (await store.GetExactAsync<ToolResource>(ScopedResourceAddress.Create(identity.ScopeRef, identity.Namespace,
                identity.Kind, identity.Name), cancellationToken) is not null) visible.Add(uid);
        }
        var groups = new List<ToolProviderGroup>();
        var grouped = new HashSet<Guid>();
        foreach (var set in graph.Documents.Values.Where(value => value.Identity.Kind == ToolResourceKinds.ToolSet))
        {
            if (await store.GetExactAsync<ToolSetResource>(ScopedResourceAddress.Create(set.Identity.ScopeRef, set.Identity.Namespace,
                set.Identity.Kind, set.Identity.Name), cancellationToken) is null) continue;
            var members = graph.Describe(set.Identity.Uid).Members.Intersect(visible).ToArray();
            var sameSource = set.Governance.Origin.ResourceUid is { } source && source == provider.Governance.Origin.ResourceUid
                && set.Governance.ManagedBy?.Reference == provider.Governance.ManagedBy?.Reference;
            if (members.Length == 0 && !sameSource) continue;
            var resource = set.Payload.Deserialize<ToolSetResource>(JsonOptions)!;
            groups.Add(new(ResourceAccessGraph.Identity(set.Identity), resource.Definition.DisplayName, members) { ToolSetName = set.Identity.Name, Unverified = members.Length == 0 && resource.Definition.Members.Count > 0 });
            grouped.UnionWith(members);
        }
        var ungrouped = visible.Where(uid => !grouped.Contains(uid)).ToArray();
        if (ungrouped.Length > 0) groups.Add(new(null, string.Empty, ungrouped));
        var statuses = new List<ToolOperationalStatus>();
        foreach (var uid in visible)
            statuses.Add(await GetAsync(graph.Documents[uid].Payload.Deserialize<ToolResource>(JsonOptions)!, cancellationToken));
        return new(groups, statuses);
    }

    public async Task<IReadOnlyList<ResourceRelatedAccessResult>> SetCollectionEnabledAsync(Guid parentUid,
        string membershipToken, bool enabled, IReadOnlyDictionary<Guid, string?> expectedETags,
        IReadOnlyList<Guid>? selected, CancellationToken cancellationToken)
    {
        var snapshot = await related.GetAsync(parentUid, cancellationToken);
        if (snapshot.MembershipToken != membershipToken) throw new ResourceConcurrencyException("The collection membership changed.");
        var members = snapshot.Resources.Where(value => value.Resource.Kind == ToolResourceKinds.Tool).ToArray();
        if (selected?.Any(uid => members.All(value => value.Resource.Uid != uid)) == true)
            throw new ToolResourceValidationException("Every selected Tool must belong to the exact saved collection.");
        return await operations.WriteAsync(snapshot.Parent.Kind, snapshot.Parent.OwnerScopeRef, "resources/write", async token =>
        {
            var results = new List<ResourceRelatedAccessResult>();
            foreach (var item in members.Where(value => selected is null || selected.Contains(value.Resource.Uid)))
            {
                var tool = await store.GetExactAsync<ToolResource>(ScopedResourceAddress.Create(item.Resource.OwnerScopeRef,
                    item.Resource.Namespace, item.Resource.Kind, item.Name), token);
                if (tool?.Value.Uid != item.Resource.Uid) { results.Add(new(item.Resource.Uid, false, "resource_identity_stale")); continue; }
                var previous = await states.GetAsync(item.Resource, token);
                if (!expectedETags.TryGetValue(item.Resource.Uid, out var expected) || expected != previous?.ETag)
                { results.Add(new(item.Resource.Uid, false, "operational_state_conflict")); continue; }
                try
                {
                    await states.PutAsync(item.Resource, enabled, expected, token);
                    results.Add(new(item.Resource.Uid, true, null));
                }
                catch (ResourceConcurrencyException) { results.Add(new(item.Resource.Uid, false, "operational_state_conflict")); }
            }
            return (IReadOnlyList<ResourceRelatedAccessResult>)results;
        }, cancellationToken);
    }
}
