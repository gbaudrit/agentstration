using System.Net;
using System.Net.Http.Json;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Management.Core;
using Agentstration.Resources;

namespace Agentstration.Management.Tests;

public sealed partial class ModelManagementApiTests
{
    [TestMethod]
    public void ResourceScopePolicyMatchesTheInitialOwnershipModel()
    {
        CollectionAssert.AreEquivalent(
            new[] { ResourceScopeKind.Tenant },
            ResourceScopePolicy.AllowedScopes(ResourceKinds.ModelProvider).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { ResourceScopeKind.Workspace },
            ResourceScopePolicy.AllowedScopes(ResourceKinds.Agent).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { ResourceScopeKind.Instance, ResourceScopeKind.Tenant, ResourceScopeKind.Workspace },
            ResourceScopePolicy.AllowedScopes(ResourceKinds.Secret).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { ResourceScopeKind.Instance, ResourceScopeKind.Tenant, ResourceScopeKind.Workspace },
            ResourceScopePolicy.AllowedScopes(ResourceKinds.SourceProvider).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { ResourceScopeKind.Workspace },
            ResourceScopePolicy.AllowedScopes(ResourceKinds.Trigger).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { ResourceScopeKind.Instance },
            ResourceScopePolicy.AllowedScopes(ResourceKinds.SourceRegistryRegistration).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { ResourceScopeKind.Instance },
            ResourceScopePolicy.AllowedScopes(ResourceKinds.SourceRegistryObservedState).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { ResourceScopeKind.Instance },
            ResourceScopePolicy.AllowedScopes(ResourceKinds.SourceRegistryRefreshRecord).ToArray());
    }

    [TestMethod]
    public async Task SecretAndVaultCreationExposeTargetsAndRequireTheExactSameScope()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var targets = await client.GetFromJsonAsync<ResourceScopeTargetResponse[]>(
            $"/api/resource-scopes/targets?kind={ResourceKinds.Secret}");
        Assert.IsNotNull(targets);
        var tenant = targets.Single(value => value.Kind == ResourceScopeKind.Tenant);
        var workspace = targets.Single(value => value.Kind == ResourceScopeKind.Workspace);
        Assert.IsTrue(tenant.CanWrite);
        Assert.IsTrue(workspace.CanWrite);

        using var tenantVaultResponse = await client.PostAsJsonAsync(
            "/api/vaults",
            new CreateVaultRequest("tenant-vault", new VaultProperties { DisplayName = "Tenant Vault", ProviderType = "local" }, tenant.ScopeRef));
        Assert.AreEqual(HttpStatusCode.Created, tenantVaultResponse.StatusCode);

        using var rejected = await client.PostAsJsonAsync(
            "/api/secrets",
            new CreateSecretRequest("invalid-workspace-secret", new SecretProperties
            {
                DisplayName = "Invalid workspace Secret",
                Vault = new ResourceReference("tenant-vault", tenant.ScopeRef),
                Key = "invalid-workspace-secret"
            }, workspace.ScopeRef));
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);

        using var workspaceVaultResponse = await client.PostAsJsonAsync(
            "/api/vaults",
            new CreateVaultRequest("workspace-vault", new VaultProperties { DisplayName = "Workspace Vault", ProviderType = "local" }, workspace.ScopeRef));
        Assert.AreEqual(HttpStatusCode.Created, workspaceVaultResponse.StatusCode);

        using var created = await client.PostAsJsonAsync(
            "/api/secrets",
            new CreateSecretRequest("workspace-secret", new SecretProperties
            {
                DisplayName = "Workspace Secret",
                Vault = new ResourceReference("workspace-vault", workspace.ScopeRef),
                Key = "workspace-secret"
            }, workspace.ScopeRef));
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
        var resource = await created.Content.ReadFromJsonAsync<SecretResource>();
        Assert.AreEqual(workspace.ScopeRef, resource?.ScopeRef);
    }

    [TestMethod]
    public async Task ResourceScopeInventoryExposesTheAccessibleHierarchyAndExactOwnership()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var targets = await client.GetFromJsonAsync<ResourceScopeTargetResponse[]>(
            $"/api/resource-scopes/targets?kind={ResourceKinds.Vault}");
        Assert.IsNotNull(targets);
        var tenant = targets.Single(value => value.Kind == ResourceScopeKind.Tenant);
        var workspace = targets.Single(value => value.Kind == ResourceScopeKind.Workspace);

        using var created = await client.PostAsJsonAsync(
            "/api/vaults",
            new CreateVaultRequest("inventory-vault", new VaultProperties
            {
                DisplayName = "Inventory Vault",
                ProviderType = "local"
            }, tenant.ScopeRef));
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);

        var inventory = await client.GetFromJsonAsync<ResourceScopeInventoryResponse>("/api/resource-scopes");
        Assert.IsNotNull(inventory);
        var instanceNode = inventory.Scopes.Single(value => value.Kind == ResourceScopeKind.Instance);
        var tenantNode = inventory.Scopes.Single(value => value.ScopeRef == tenant.ScopeRef);
        var workspaceNode = inventory.Scopes.Single(value => value.ScopeRef == workspace.ScopeRef);
        Assert.IsNull(instanceNode.ParentScopeRef);
        Assert.AreEqual(instanceNode.ScopeRef, tenantNode.ParentScopeRef);
        Assert.AreEqual(tenantNode.ScopeRef, workspaceNode.ParentScopeRef);
        Assert.IsFalse(tenantNode.IsCurrent);
        Assert.IsTrue(workspaceNode.IsCurrent);
        Assert.IsTrue(tenantNode.Resources.Any(value =>
            value.Kind == ResourceKinds.Vault && value.Name == "inventory-vault"));
        Assert.IsFalse(workspaceNode.Resources.Any(value => value.Name == "inventory-vault"));
    }

    [TestMethod]
    public async Task ResourceCreationHonorsTheScopeSelectedByTheConsole()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var targets = await client.GetFromJsonAsync<ResourceScopeTargetResponse[]>(
            $"/api/resource-scopes/targets?kind={ResourceKinds.RuntimeProfile}");
        var tenant = targets!.Single();

        using var created = await client.PostAsJsonAsync(
            "/api/runtimeprofiles",
            new CreateRuntimeProfileRequest(
                "explicit-scope-runtime",
                new RuntimeProfileProperties { DisplayName = "Explicit scope runtime", RuntimeType = "microsoft-agent-framework" },
                ScopeRef: tenant.ScopeRef));

        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
        var resource = await created.Content.ReadFromJsonAsync<RuntimeProfileResource>();
        Assert.AreEqual(tenant.ScopeRef, resource?.ScopeRef);
    }
}
