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
            new[] { ResourceScopeKind.Workspace },
            ResourceScopePolicy.AllowedScopes(ResourceKinds.Trigger).ToArray());
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
}
