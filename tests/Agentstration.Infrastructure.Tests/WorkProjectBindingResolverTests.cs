using Agentstration.Identity;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure.Work;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Work;

namespace Agentstration.Infrastructure.Tests;

[TestClass]
public sealed class WorkProjectBindingResolverTests
{
    [TestMethod]
    public async Task ExactAncestorBindingRequiresDiscoverAndUseAndReadsOnlyIdentityMetadata()
    {
        var fixture = new Fixture();
        using var context = fixture.Context.Push(fixture.Caller);
        var result = await fixture.Resolver.ResolveAsync(fixture.Workspace, fixture.Caller.PrincipalId, fixture.Binding, default);
        Assert.IsTrue(result.IsSuccess, result.ErrorCode);
        Assert.AreEqual(fixture.Catalog.Identity.ETag, result.Value!.ETag);
        CollectionAssert.AreEqual(new[] { ResourceAccessAction.Discover, ResourceAccessAction.Use }, fixture.Policy.Actions.ToArray());
        Assert.AreEqual(ControlPlaneAccessMode.Workspace, fixture.Context.AccessMode);
        fixture.Policy.Use = false;
        Assert.AreEqual("work_project_binding_denied", (await fixture.Resolver.ResolveAsync(fixture.Workspace, fixture.Caller.PrincipalId, fixture.Binding, default)).ErrorCode);
        fixture.Policy.Use = true; fixture.Policy.Discover = false;
        Assert.AreEqual("work_project_binding_unavailable", (await fixture.Resolver.ResolveAsync(fixture.Workspace, fixture.Caller.PrincipalId, fixture.Binding, default)).ErrorCode);
        fixture.Catalog.Identity = fixture.Catalog.Identity with { Generation = 4 };
        Assert.AreEqual("work_project_binding_unavailable", (await fixture.Resolver.ResolveAsync(fixture.Workspace, fixture.Caller.PrincipalId, fixture.Binding, default)).ErrorCode,
            "An undiscoverable resource must not reveal whether its pinned generation is stale.");
    }

    [TestMethod]
    public async Task ForeignCallerWorkspaceAndSiblingReferencesFailBeforeMetadataLookup()
    {
        var fixture = new Fixture();
        Assert.AreEqual("work_project_binding_denied", (await fixture.Resolver.ResolveAsync(fixture.Workspace, fixture.Caller.PrincipalId, fixture.Binding, default)).ErrorCode);
        using var context = fixture.Context.Push(fixture.Caller);
        Assert.AreEqual("work_project_binding_denied", (await fixture.Resolver.ResolveAsync(fixture.Workspace, Guid.NewGuid(), fixture.Binding, default)).ErrorCode);
        Assert.AreEqual("work_project_binding_denied", (await fixture.Resolver.ResolveAsync(WorkspaceId.New(), fixture.Caller.PrincipalId, fixture.Binding, default)).ErrorCode);
        var sibling = fixture.Binding with { Target = fixture.Binding.Target with
            { Address = fixture.Binding.Target.Address with { ScopeRef = ResourceScopeRef.Workspace(Guid.NewGuid()) } } };
        Assert.AreEqual("work_project_binding_unavailable", (await fixture.Resolver.ResolveAsync(fixture.Workspace, fixture.Caller.PrincipalId, sibling, default)).ErrorCode);
        Assert.AreEqual(0, fixture.Catalog.Reads);
        fixture.Authorization.Allowed = false;
        Assert.AreEqual("work_project_binding_denied", (await fixture.Resolver.ResolveAsync(fixture.Workspace, fixture.Caller.PrincipalId, fixture.Binding, default)).ErrorCode);
        Assert.AreEqual(0, fixture.Catalog.Reads);
    }

    [TestMethod]
    public async Task MissingChangedOrReplacedResourceNeverSilentlyRetargetsTheBinding()
    {
        var fixture = new Fixture(); using var context = fixture.Context.Push(fixture.Caller);
        var binding = fixture.Binding;
        fixture.Catalog.Identity = fixture.Catalog.Identity with { Generation = 4 };
        Assert.AreEqual("work_project_binding_stale", (await fixture.Resolver.ResolveAsync(fixture.Workspace, fixture.Caller.PrincipalId, binding, default)).ErrorCode);
        fixture.Catalog.Identity = fixture.Catalog.Identity with { Generation = 3, Name = "replacement" };
        Assert.AreEqual("work_project_binding_unavailable", (await fixture.Resolver.ResolveAsync(fixture.Workspace, fixture.Caller.PrincipalId, binding, default)).ErrorCode);
        fixture.Catalog.Identity = fixture.Catalog.Identity with { Name = "git-operations", Version = "changed" };
        Assert.AreEqual("work_project_binding_stale", (await fixture.Resolver.ResolveAsync(fixture.Workspace, fixture.Caller.PrincipalId, binding, default)).ErrorCode);
        fixture.Catalog.Missing = true;
        Assert.AreEqual("work_project_binding_unavailable", (await fixture.Resolver.ResolveAsync(fixture.Workspace, fixture.Caller.PrincipalId, binding, default)).ErrorCode);
    }

    private sealed class Fixture
    {
        public WorkspaceId Workspace { get; } = WorkspaceId.New();
        public RequestContext Caller { get; }
        public CurrentRequestContext Context { get; } = new();
        public Catalog Catalog { get; }
        public Policy Policy { get; } = new();
        public Authorization Authorization { get; } = new();
        public WorkProjectContextBinding Binding { get; }
        public WorkProjectBindingResolver Resolver { get; }
        public Fixture()
        {
            Caller = new(Guid.NewGuid(), Guid.NewGuid(), Workspace.Value);
            Catalog = new(Context, new(Guid.NewGuid(), ResourceScopeRef.Instance, ResourceNamespace.Default, "ToolProvider", "git-operations", 3, null, "\"provider-v3\""));
            Binding = new("sharing", WorkProjectBindingPurpose.Sharing, new(new(ResourceScopeRef.Instance, ResourceNamespace.Default, "ToolProvider", "git-operations"), Catalog.Identity.Uid, 3));
            Resolver = new(Catalog, Policy, new DescendantResourceUseAuthorizer(new Scopes(Workspace, Caller.TenantId)), Context, Context, Authorization);
        }
    }

    private sealed class Catalog(CurrentRequestContext context, ResourceIdentitySnapshot identity) : IResourceIdentityCatalog
    {
        public ResourceIdentitySnapshot Identity { get; set; } = identity;
        public bool Missing { get; set; }
        public int Reads { get; private set; }
        public Task<ResourceIdentitySnapshot?> GetIdentityByUidAsync(Guid uid, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Reads++;
            Assert.AreEqual(ControlPlaneAccessMode.System, context.AccessMode, "Only a checked identity metadata read may enter System mode.");
            return Task.FromResult(!Missing && uid == Identity.Uid ? Identity : null);
        }
        public Task<IReadOnlyList<ResourceInventoryEntry>> ListExactInventoryAsync(ResourceScopeRef scope, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class Scopes(WorkspaceId workspace, Guid tenantId) : IResourceScopeResolver
    {
        public Task<ResolvedResourceScope?> ResolveAsync(ResourceScopeRef scope, CancellationToken token)
        {
            var instance = new ResourceScope(1, ResourceScopeRef.Instance, ResourceScopeKind.Instance, "instance", null);
            var tenant = new ResourceScope(2, ResourceScopeRef.Tenant(tenantId), ResourceScopeKind.Tenant, tenantId.ToString("D"), 1);
            ResolvedResourceScope? result = scope == ResourceScopeRef.Instance ? new(instance, [])
                : scope == tenant.Ref ? new(tenant, [instance])
                : scope == ResourceScopeRef.Workspace(workspace.Value) ? new(new(3, scope, ResourceScopeKind.Workspace, workspace.ToString(), 2), [tenant, instance]) : null;
            return Task.FromResult(result);
        }
    }

    private sealed class Authorization : Agentstration.Identity.Contracts.IAuthorizationService
    {
        public bool Allowed { get; set; } = true;
        public Task<bool> HasPermissionAsync(RequestContext context, string permission, CancellationToken token) => Task.FromResult(Allowed);
        public Task<IReadOnlySet<string>> GetPermissionsAsync(RequestContext context, CancellationToken token) => throw new NotSupportedException();
        public Task EnsurePermissionAsync(RequestContext context, string permission, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class Policy : IResourceAccessPolicyService
    {
        public bool Discover { get; set; } = true;
        public bool Use { get; set; } = true;
        public List<ResourceAccessAction> Actions { get; } = [];
        public Task<ResourceAccessDecision> EvaluateAsync(ResourceAccessIdentity resource, ResourceScopeRef consumer, ResourceAccessAction action, CancellationToken token)
        { Actions.Add(action); return Task.FromResult(new ResourceAccessDecision(action == ResourceAccessAction.Discover ? Discover : Use, "exact_grant")); }
        public Task<IReadOnlyList<ResourceAccessPolicyResponse>> ListAsync(ResourceScopeRef owner, CancellationToken token) => throw new NotSupportedException();
        public Task<ResourceAccessPolicyResponse?> GetAsync(Guid uid, CancellationToken token) => throw new NotSupportedException();
        public Task<ResourceAccessPolicyResponse> PutAsync(ResourceAccessPolicy policy, string? ifMatch, CancellationToken token) => throw new NotSupportedException();
        public Task DeleteAsync(Guid uid, string? ifMatch, CancellationToken token) => throw new NotSupportedException();
    }
}
