using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Security.Contracts;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class ResourceAccessPolicyServiceTests
{
    private static readonly ResourceScopeRef TenantA = ResourceScopeRef.Tenant(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
    private static readonly ResourceScopeRef TenantB = ResourceScopeRef.Tenant(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
    private static readonly ResourceScopeRef WorkspaceA = ResourceScopeRef.Workspace(Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111"));
    private static readonly ResourceScopeRef WorkspaceSibling = ResourceScopeRef.Workspace(Guid.Parse("aaaaaaaa-2222-2222-2222-222222222222"));
    private static readonly ResourceScopeRef WorkspaceB = ResourceScopeRef.Workspace(Guid.Parse("bbbbbbbb-1111-1111-1111-111111111111"));

    [TestMethod]
    public async Task ExactAndPropagatedActionsAreEvaluatedIndependently()
    {
        var fixture = CreateFixture(ResourceScopeRef.Instance, generation: 7);
        var identity = fixture.Identity(7);
        await fixture.Service.PutAsync(new(identity,
        [
            new(TenantA, [ResourceAccessAction.Discover], IncludeDescendants: true),
            new(WorkspaceA, [ResourceAccessAction.Use])
        ]), null, default);

        AssertDecision(await fixture.Service.EvaluateAsync(identity, ResourceScopeRef.Instance,
            ResourceAccessAction.Use, default), true, ResourceAccessDecisionReasons.SameScope);
        AssertDecision(await fixture.Service.EvaluateAsync(identity, WorkspaceA,
            ResourceAccessAction.Discover, default), true, ResourceAccessDecisionReasons.PropagatedGrant);
        AssertDecision(await fixture.Service.EvaluateAsync(identity, WorkspaceA,
            ResourceAccessAction.Use, default), true, ResourceAccessDecisionReasons.ExactGrant);
        AssertDecision(await fixture.Service.EvaluateAsync(identity, WorkspaceSibling,
            ResourceAccessAction.Use, default), false, ResourceAccessDecisionReasons.GrantMissing);
        AssertDecision(await fixture.Service.EvaluateAsync(identity, WorkspaceB,
            ResourceAccessAction.Discover, default), false, ResourceAccessDecisionReasons.GrantMissing);
        Assert.IsTrue(fixture.Audit.Entries.Any(value => value.Action == SecurityAuditActions.ResourceAccessEvaluated
            && value.Outcome == SecurityAuditOutcome.Failed));
    }

    [TestMethod]
    public async Task MissingAndStalePoliciesFailClosed()
    {
        var fixture = CreateFixture(TenantA, generation: 2);
        var current = fixture.Identity(2);
        AssertDecision(await fixture.Service.EvaluateAsync(current, WorkspaceA,
            ResourceAccessAction.Use, default), false, ResourceAccessDecisionReasons.PolicyMissing);

        await fixture.Service.PutAsync(new(current,
            [new(WorkspaceA, [ResourceAccessAction.Use])]), null, default);
        fixture.Resources.Current = fixture.Resources.Current! with { Generation = 3 };

        AssertDecision(await fixture.Service.EvaluateAsync(current, WorkspaceA,
            ResourceAccessAction.Use, default), false, ResourceAccessDecisionReasons.ResourceIdentityStale);
        AssertDecision(await fixture.Service.EvaluateAsync(fixture.Identity(3), WorkspaceA,
            ResourceAccessAction.Use, default), false, ResourceAccessDecisionReasons.PolicyIdentityStale);
    }

    [TestMethod]
    public async Task InvalidTargetsAndDuplicateGrantsAreRejectedWithStableCodes()
    {
        var fixture = CreateFixture(TenantA, generation: 1);
        foreach (var target in new[] { ResourceScopeRef.Instance, TenantA, TenantB, WorkspaceB })
        {
            var exception = await Assert.ThrowsExactlyAsync<ResourceAccessPolicyException>(() =>
                fixture.Service.PutAsync(new(fixture.Identity(1),
                    [new(target, [ResourceAccessAction.Use])]), null, default));
            Assert.AreEqual("grant_target_outside_owner_tree", exception.Code);
        }

        var duplicate = await Assert.ThrowsExactlyAsync<ResourceAccessPolicyException>(() =>
            fixture.Service.PutAsync(new(fixture.Identity(1),
            [
                new(WorkspaceA, [ResourceAccessAction.Discover]),
                new(WorkspaceA, [ResourceAccessAction.Use])
            ]), null, default));
        Assert.AreEqual("grant_target_duplicate", duplicate.Code);

        var invalidAction = await Assert.ThrowsExactlyAsync<ResourceAccessPolicyException>(() =>
            fixture.Service.PutAsync(new(fixture.Identity(1),
                [new(WorkspaceA, [(ResourceAccessAction)99])]), null, default));
        Assert.AreEqual("grant_actions_invalid", invalidAction.Code);
        AssertDecision(await fixture.Service.EvaluateAsync(fixture.Identity(1), WorkspaceA,
            (ResourceAccessAction)99, default), false, ResourceAccessDecisionReasons.RequestInvalid);
    }

    private static Fixture CreateFixture(ResourceScopeRef owner, long generation)
    {
        var uid = Guid.NewGuid();
        var resources = new ResourcesStore
        {
            Current = new(uid, owner, new("team"), "Agent", "assistant", generation, null, "\"resource\"")
        };
        var policies = new PolicyStore();
        var scopes = new Scopes();
        var audit = new Audit();
        var service = new ResourceAccessPolicyService(resources, policies, scopes, new ScopeOperations(), audit);
        return new(service, resources, audit, uid, owner);
    }

    private static void AssertDecision(ResourceAccessDecision decision, bool allowed, string reason)
    {
        Assert.AreEqual(allowed, decision.Allowed);
        Assert.AreEqual(reason, decision.ReasonCode);
    }

    private sealed record Fixture(ResourceAccessPolicyService Service, ResourcesStore Resources, Audit Audit,
        Guid Uid, ResourceScopeRef Owner)
    {
        public ResourceAccessIdentity Identity(long generation) => new(Uid, Owner, new("team"), "Agent", generation);
    }

    private sealed class ResourcesStore : IResourceStore
    {
        public ResourceIdentitySnapshot? Current { get; set; }
        public Task<ResourceIdentitySnapshot?> GetIdentityByUidAsync(Guid uid, CancellationToken cancellationToken) =>
            Task.FromResult(Current?.Uid == uid ? Current : null);
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<StoredResource<T>?> GetAsync<T>(ResourceKey key, CancellationToken cancellationToken) where T : Resource => throw new NotSupportedException();
        public Task<IReadOnlyList<StoredResource<T>>> ListAsync<T>(string kind, int skip, int take, CancellationToken cancellationToken) where T : Resource => throw new NotSupportedException();
        public Task<StoredResource<T>> PutAsync<T>(T resource, string? ifMatch, bool ifNoneMatch, CancellationToken cancellationToken) where T : Resource => throw new NotSupportedException();
        public Task<StoredResource<T>> CreateImmutableAsync<T>(T resource, CancellationToken cancellationToken) where T : Resource => throw new NotSupportedException();
        public Task DeleteAsync(ResourceKey key, string? ifMatch, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class PolicyStore : IResourceAccessPolicyStore
    {
        private StoredResourceAccessPolicy? stored;
        public Task<IReadOnlyList<StoredResourceAccessPolicy>> ListAsync(ResourceScopeRef ownerScopeRef, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<StoredResourceAccessPolicy>>(stored is null ? [] : [stored]);
        public Task<StoredResourceAccessPolicy?> GetAsync(Guid resourceUid, ResourceScopeRef ownerScopeRef, CancellationToken cancellationToken) =>
            Task.FromResult(stored?.Policy.Resource.Uid == resourceUid && stored.Policy.Resource.OwnerScopeRef == ownerScopeRef ? stored : null);
        public Task<StoredResourceAccessPolicy> PutAsync(ResourceAccessPolicy policy, string? ifMatch, CancellationToken cancellationToken)
        {
            if (ifMatch is not null && stored?.ETag != ifMatch) throw new ResourceConcurrencyException("stale");
            stored = new(policy, $"\"{Guid.NewGuid():N}\"", DateTimeOffset.UnixEpoch);
            return Task.FromResult(stored);
        }
        public Task DeleteAsync(Guid resourceUid, ResourceScopeRef ownerScopeRef, string? ifMatch, CancellationToken cancellationToken)
        {
            stored = null;
            return Task.CompletedTask;
        }
    }

    private sealed class ScopeOperations : IResourceScopeOperations
    {
        public ResourceScopeRef DefaultScopeRef(string kind) => throw new NotSupportedException();
        public ResourceScopeRef TargetScopeRef(ResourceScopeKind kind) => throw new NotSupportedException();
        public Task<IReadOnlyList<ResourceScopeTarget>> ListTargetsAsync(string kind, string permission, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<T> WriteAsync<T>(Resource resource, ResourceScopeRef scopeRef, string permission, Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken) => operation(cancellationToken);
        public Task<T> WriteAsync<T>(string kind, ResourceScopeRef scopeRef, string permission, Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken) => operation(cancellationToken);
    }

    private sealed class Audit : ISecurityAuditWriter
    {
        public List<SecurityAuditWrite> Entries { get; } = [];
        public Task WriteAsync(SecurityAuditWrite entry, CancellationToken cancellationToken)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class Scopes : IResourceScopeResolver
    {
        private readonly ResourceScope instance = new(1, ResourceScopeRef.Instance, ResourceScopeKind.Instance, "instance", null);
        private readonly ResourceScope tenantA = new(2, TenantA, ResourceScopeKind.Tenant, TenantA.TargetKey, 1);
        private readonly ResourceScope tenantB = new(3, TenantB, ResourceScopeKind.Tenant, TenantB.TargetKey, 1);
        private readonly ResourceScope workspaceA = new(4, WorkspaceA, ResourceScopeKind.Workspace, WorkspaceA.TargetKey, 2);
        private readonly ResourceScope workspaceSibling = new(5, WorkspaceSibling, ResourceScopeKind.Workspace, WorkspaceSibling.TargetKey, 2);
        private readonly ResourceScope workspaceB = new(6, WorkspaceB, ResourceScopeKind.Workspace, WorkspaceB.TargetKey, 3);

        public Task<ResolvedResourceScope?> ResolveAsync(ResourceScopeRef scopeRef, CancellationToken cancellationToken)
        {
            ResolvedResourceScope? resolved = scopeRef == ResourceScopeRef.Instance ? new(instance, [])
                : scopeRef == TenantA ? new(tenantA, [instance])
                : scopeRef == TenantB ? new(tenantB, [instance])
                : scopeRef == WorkspaceA ? new(workspaceA, [tenantA, instance])
                : scopeRef == WorkspaceSibling ? new(workspaceSibling, [tenantA, instance])
                : scopeRef == WorkspaceB ? new(workspaceB, [tenantB, instance]) : null;
            return Task.FromResult(resolved);
        }
    }
}
