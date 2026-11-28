using System.Security.Cryptography;
using System.Text;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;

namespace Agentstration.ResourceManagement;

public sealed class ResourceRelatedAccessService(IResourceIdentityCatalog identities,
    IResourceAccessPolicyService policies, IResourceScopeResolver scopes, IResourceScopeOperations operations)
{
    private const int MaximumMembers = 1000;
    private static readonly ResourceAccessAction[] Actions = [ResourceAccessAction.Discover, ResourceAccessAction.Use];

    public async Task<ResourceRelatedAccessSnapshot> GetAsync(Guid parentUid, CancellationToken cancellationToken)
    {
        var (graph, parent, members) = await ReadAsync(parentUid, cancellationToken);
        var targets = await operations.ListTargetsAsync(parent.Identity.Kind, "resources/write", cancellationToken);
        return Snapshot(graph, parent, members) with { CanManage = targets.Any(value => value.ScopeRef == parent.Identity.ScopeRef && value.CanWrite) };
    }

    public async Task<ResourceRelatedAccessEvaluation> EvaluateAsync(Guid parentUid, ResourceScopeRef consumer,
        CancellationToken cancellationToken)
    {
        var (graph, parent, members) = await ReadAsync(parentUid, cancellationToken);
        var resolved = await scopes.ResolveAsync(consumer, cancellationToken);
        if (resolved is null) return new(false, 0, 0, members.Count, 0, [ResourceAccessDecisionReasons.ConsumerOutsideOwnerTree]);
        var ancestors = resolved.Ancestors.Select(value => value.Ref).ToArray();
        var parentIdentity = ResourceAccessGraph.Identity(parent.Identity);
        var eligible = consumer != parent.Identity.ScopeRef
            && graph.Evaluate(parentIdentity, consumer, ancestors, ResourceAccessAction.Discover).Allowed
            && graph.Evaluate(parentIdentity, consumer, ancestors, ResourceAccessAction.Use).Allowed;
        var discover = members.Select(uid => graph.Evaluate(ResourceAccessGraph.Identity(graph.Documents[uid].Identity),
            consumer, ancestors, ResourceAccessAction.Discover)).ToArray();
        var use = members.Select(uid => graph.Evaluate(ResourceAccessGraph.Identity(graph.Documents[uid].Identity),
            consumer, ancestors, ResourceAccessAction.Use)).ToArray();
        var exceptions = members.Count(uid => graph.Documents[uid].Policy?.Grants.Any(value => value.TargetScopeRef == consumer
            && value.SourceResourceUid != parentUid) == true);
        return new(eligible, discover.Count(value => value.Allowed), use.Count(value => value.Allowed), members.Count,
            exceptions, discover.Concat(use).Where(value => !value.Allowed).Select(value => value.ReasonCode).Distinct().ToArray())
        { Unavailable = members.Count(uid => graph.Describe(uid).Available == false) };
    }

    public async Task<IReadOnlyList<ResourceRelatedAccessResult>> ApplyAsync(Guid parentUid,
        ResourceRelatedAccessRequest request, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(request.Mode)) throw Invalid("related_access_mode_invalid", "An access mode is required.");
        var (graph, parent, members) = await ReadAsync(parentUid, cancellationToken);
        if (request.MembershipToken != Token(graph, parent, members))
            throw new ResourceConcurrencyException("The related resource membership changed. Review the current inventory.");
        var targets = request.ResourceUids?.Distinct().ToArray() ?? members.ToArray();
        if (targets.Length > MaximumMembers || targets.Any(uid => !members.Contains(uid)))
            throw Invalid("related_access_members_invalid", "Every selected resource must belong to the exact saved collection.");
        var resolved = await scopes.ResolveAsync(request.ConsumerScopeRef, cancellationToken);
        if (resolved is null || request.ConsumerScopeRef == parent.Identity.ScopeRef
            || !resolved.Ancestors.Any(value => value.Ref == parent.Identity.ScopeRef))
            throw Invalid("grant_target_outside_owner_tree", "The target must be an exact descendant scope.");
        // Inherit/removal remains possible while the parent is revoked, but cannot confer new access.
        if (request.Mode == ResourceRelatedAccessMode.Allow)
        {
            foreach (var action in Actions)
                if (!(await policies.EvaluateAsync(ResourceAccessGraph.Identity(parent.Identity), request.ConsumerScopeRef,
                    action, cancellationToken)).Allowed)
                    throw Invalid("parent_access_denied", "The parent does not permit this consumer scope/action.");
        }
        return await operations.WriteAsync(parent.Identity.Kind, parent.Identity.ScopeRef, "resources/write", async token =>
        {
            var results = new List<ResourceRelatedAccessResult>();
            foreach (var uid in targets)
            {
                token.ThrowIfCancellationRequested();
                // Administer an included ToolSet once; members continue to inherit without copied grants.
                var relationships = graph.Describe(uid);
                if (relationships.InheritFrom.Any(targets.Contains)
                    || relationships.PolicySourceUid is { } source && targets.Contains(source)) continue;
                var member = graph.Documents[uid];
                var current = await policies.GetAsync(uid, token);
                if (!request.PolicyETags.TryGetValue(uid, out var expected) || expected != current?.ETag)
                {
                    results.Add(new(uid, false, "policy_conflict"));
                    continue;
                }
                var policy = current?.Policy ?? new(ResourceAccessGraph.Identity(member.Identity), []);
                var direct = policy.Grants.SingleOrDefault(value => value.TargetScopeRef == request.ConsumerScopeRef);
                if (request.Mode != ResourceRelatedAccessMode.Inherit && direct?.DeniedActions.Count > 0
                    && direct.SourceResourceUid != parentUid)
                {
                    results.Add(new(uid, false, "explicit_deny_preserved"));
                    continue;
                }
                var grants = policy.Grants.Where(value => value.TargetScopeRef != request.ConsumerScopeRef).ToList();
                if (request.Mode == ResourceRelatedAccessMode.Inherit)
                {
                    if (direct is not null && direct.SourceResourceUid != parentUid) grants.Add(direct);
                }
                else
                {
                    grants.Add(new(request.ConsumerScopeRef, request.Mode == ResourceRelatedAccessMode.Allow ? Actions : [],
                        request.ConsumerScopeRef.Kind == ResourceScopeKind.Tenant)
                    {
                        DeniedActions = request.Mode == ResourceRelatedAccessMode.Deny ? Actions : [], SourceResourceUid = parentUid
                    });
                }
                try
                {
                    await policies.PutAsync(policy with { Grants = grants.ToArray() }, expected, token);
                    results.Add(new(uid, true, null));
                }
                catch (ResourceConcurrencyException) { results.Add(new(uid, false, "policy_conflict")); }
                catch (ResourceAccessPolicyException exception) { results.Add(new(uid, false, exception.Code)); }
            }
            return (IReadOnlyList<ResourceRelatedAccessResult>)results;
        }, cancellationToken);
    }

    private async Task<(ResourceAccessGraph Graph, ResourceAccessDocument Parent, IReadOnlyList<Guid> Members)> ReadAsync(
        Guid uid, CancellationToken cancellationToken)
    {
        if (identities is not IResourceAccessGraphReader reader
            || await reader.GetAccessGraphAsync(uid, cancellationToken) is not { } graph
            || !graph.Documents.TryGetValue(uid, out var parent))
            throw Invalid("resource_not_found", "The exact parent resource was not found.");
        // Collection inventories are administrative projections, not a way to enumerate hidden child resources.
        await operations.WriteAsync(parent.Identity.Kind, parent.Identity.ScopeRef, "resources/write",
            _ => Task.FromResult(true), cancellationToken);
        var relationships = graph.Describe(uid);
        if (!relationships.Valid || !relationships.Collection)
            throw Invalid("related_resources_unavailable", "This resource has no verified related collection.");
        if (relationships.Members.Count > MaximumMembers)
            throw Invalid("related_resources_limit", "The related collection exceeds its bounded limit.");
        return (graph, parent, relationships.Members);
    }

    private static ResourceRelatedAccessSnapshot Snapshot(ResourceAccessGraph graph, ResourceAccessDocument parent,
        IReadOnlyList<Guid> members) => new(ResourceAccessGraph.Identity(parent.Identity), Token(graph, parent, members),
        members.Select(uid => graph.Documents[uid]).Select(value => new ResourceRelatedAccessItem(
            ResourceAccessGraph.Identity(value.Identity), value.Identity.Name,
            value.Policy is null ? null : new(value.Policy, value.PolicyETag!, value.PolicyUpdatedAt))
                { DisplayName = DisplayName(value) }).ToArray());

    private static string? DisplayName(ResourceAccessDocument document)
    {
        var payload = document.Payload;
        if (payload.TryGetProperty("displayName", out var name) && name.ValueKind == System.Text.Json.JsonValueKind.String) return name.GetString();
        return payload.TryGetProperty("definition", out var definition) && definition.ValueKind == System.Text.Json.JsonValueKind.Object
            && definition.TryGetProperty("displayName", out name) && name.ValueKind == System.Text.Json.JsonValueKind.String ? name.GetString() : null;
    }

    private static string Token(ResourceAccessGraph graph, ResourceAccessDocument parent, IReadOnlyList<Guid> members) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(parent.Identity.Uid + ":" + string.Join(";",
            members.Order().Select(uid => $"{uid}:{graph.Documents[uid].Identity.Generation}:{graph.Documents[uid].Identity.ETag}")))));
    private static ResourceAccessPolicyException Invalid(string code, string message) => new(code, message);
}
