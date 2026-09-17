using System.Text.Json;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Secrets;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class DescendantResourceUseTests
{
    private static readonly ResourceScopeRef TenantA = ResourceScopeRef.Tenant(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
    private static readonly ResourceScopeRef TenantB = ResourceScopeRef.Tenant(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
    private static readonly ResourceScopeRef WorkspaceA = ResourceScopeRef.Workspace(Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111"));
    private static readonly ResourceScopeRef WorkspaceB = ResourceScopeRef.Workspace(Guid.Parse("bbbbbbbb-1111-1111-1111-111111111111"));
    private static readonly ResourceScopeRef WorkspaceSibling = ResourceScopeRef.Workspace(Guid.Parse("aaaaaaaa-2222-2222-2222-222222222222"));

    [TestMethod]
    public async Task DefaultDenyAndExactGrantsDoNotSpillToSiblings()
    {
        var authorizer = new DescendantResourceUseAuthorizer(new Scopes());
        var deny = new DescendantUsePolicy();
        Assert.IsTrue(await authorizer.CanUseAsync(TenantA, TenantA, deny, default));
        Assert.IsFalse(await authorizer.CanUseAsync(TenantA, WorkspaceA, deny, default));

        var grant = new DescendantUsePolicy { Grants = [new(WorkspaceA)] };
        await authorizer.ValidateAsync(TenantA, grant, default);
        Assert.IsTrue(await authorizer.CanUseAsync(TenantA, WorkspaceA, grant, default));
        Assert.IsFalse(await authorizer.CanUseAsync(TenantA, WorkspaceSibling, grant, default));
        Assert.IsFalse(await authorizer.CanUseAsync(TenantA, WorkspaceB, grant, default));
        Assert.IsFalse(await authorizer.CanUseAsync(WorkspaceA, TenantA, grant, default));
        Assert.IsFalse(await authorizer.CanUseAsync(TenantA, WorkspaceA,
            new() { Grants = [new(ResourceScopeRef.Instance, IncludeDescendants: true)] }, default));
        Assert.IsFalse(await authorizer.IsSameOrDescendantAsync(TenantA, WorkspaceB, default));
        Assert.IsFalse(await authorizer.IsSameOrDescendantAsync(WorkspaceA, TenantA, default));
    }

    [TestMethod]
    public async Task TenantGrantMayIncludeOnlyItsOwnWorkspaces()
    {
        var authorizer = new DescendantResourceUseAuthorizer(new Scopes());
        var grant = new DescendantUsePolicy { Grants = [new(TenantA, IncludeDescendants: true)] };
        await authorizer.ValidateAsync(ResourceScopeRef.Instance, grant, default);
        Assert.IsTrue(await authorizer.CanUseAsync(ResourceScopeRef.Instance, WorkspaceA, grant, default));
        Assert.IsFalse(await authorizer.CanUseAsync(ResourceScopeRef.Instance, WorkspaceB, grant, default));
    }

    [TestMethod]
    public async Task DefinitionsWithoutExplicitUsePolicyDenyDescendants()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var vault = JsonSerializer.Deserialize<VaultProperties>("""{"displayName":"Local","providerType":"local"}""", options);
        var secret = JsonSerializer.Deserialize<SecretProperties>("""{"displayName":"Default policy","vault":{"name":"local"},"key":"default-policy"}""", options);

        Assert.IsNotNull(vault);
        Assert.IsNotNull(secret);
        var authorizer = new DescendantResourceUseAuthorizer(new Scopes());
        Assert.IsTrue(await authorizer.CanUseAsync(TenantA, TenantA, vault.UsePolicy, default));
        Assert.IsFalse(await authorizer.CanUseAsync(TenantA, WorkspaceA, vault.UsePolicy, default));
        Assert.IsTrue(await authorizer.CanUseAsync(TenantA, TenantA, secret.UsePolicy, default));
        Assert.IsFalse(await authorizer.CanUseAsync(TenantA, WorkspaceA, secret.UsePolicy, default));
    }

    [TestMethod]
    public async Task InvalidTargetsAreRejectedBeforePersistence()
    {
        var authorizer = new DescendantResourceUseAuthorizer(new Scopes());
        foreach (var target in new[] { TenantA, TenantB, ResourceScopeRef.Instance, WorkspaceB })
        {
            await Assert.ThrowsAsync<InvalidDescendantUseGrantException>(async () =>
                await authorizer.ValidateAsync(TenantA, new() { Grants = [new(target)] }, default));
        }
        await Assert.ThrowsAsync<InvalidDescendantUseGrantException>(async () =>
            await authorizer.ValidateAsync(TenantA, new() { Grants = [new(default)] }, default));
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
            ResolvedResourceScope? resolved = scopeRef.Kind switch
            {
                ResourceScopeKind.Instance when scopeRef == ResourceScopeRef.Instance => new(instance, []),
                ResourceScopeKind.Tenant when scopeRef == TenantA => new(tenantA, [instance]),
                ResourceScopeKind.Tenant when scopeRef == TenantB => new(tenantB, [instance]),
                ResourceScopeKind.Workspace when scopeRef == WorkspaceA => new(workspaceA, [tenantA, instance]),
                ResourceScopeKind.Workspace when scopeRef == WorkspaceSibling => new(workspaceSibling, [tenantA, instance]),
                ResourceScopeKind.Workspace when scopeRef == WorkspaceB => new(workspaceB, [tenantB, instance]),
                _ => null
            };
            return Task.FromResult(resolved);
        }
    }
}
