using System.Net;
using System.Net.Http.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.Client;
using Agentstration.Agents;
using Agentstration.Identity.Contracts;
using Agentstration.Models;
using Agentstration.Parameters;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Contracts;
using Agentstration.Secrets;
using Agentstration.Secrets.Abstractions;
using Agentstration.Secrets.Contracts;
using Agentstration.Sources.Contracts;
using Agentstration.Triggers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class ResourceScopeApiTests : ModelManagementApiTestBase
{
    [TestMethod]
    public void ResourceScopePolicyMatchesTheInitialOwnershipModel()
    {
        CollectionAssert.AreEquivalent(
            new[] { ResourceScopeKind.Tenant },
            ResourceScopePolicy.AllowedScopes(ModelResourceKinds.ModelProvider).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { ResourceScopeKind.Workspace },
            ResourceScopePolicy.AllowedScopes(AgentResourceKinds.Agent).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { ResourceScopeKind.Instance, ResourceScopeKind.Tenant, ResourceScopeKind.Workspace },
            ResourceScopePolicy.AllowedScopes(SecretResourceKinds.Secret).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { ResourceScopeKind.Instance, ResourceScopeKind.Tenant, ResourceScopeKind.Workspace },
            ResourceScopePolicy.AllowedScopes(ParameterResourceKinds.Parameter).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { ResourceScopeKind.Instance, ResourceScopeKind.Tenant, ResourceScopeKind.Workspace },
            ResourceScopePolicy.AllowedScopes(SourceResourceKinds.SourceProvider).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { ResourceScopeKind.Workspace },
            ResourceScopePolicy.AllowedScopes(TriggerResourceKinds.Trigger).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { ResourceScopeKind.Instance },
            ResourceScopePolicy.AllowedScopes(SourceRegistryKinds.SourceRegistryRegistration).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { ResourceScopeKind.Instance },
            ResourceScopePolicy.AllowedScopes(SourceRegistryKinds.SourceRegistryObservedState).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { ResourceScopeKind.Instance },
            ResourceScopePolicy.AllowedScopes(SourceRegistryKinds.SourceRegistryRefreshRecord).ToArray());
    }

    [TestMethod]
    public async Task SecretAndVaultCreationRequireAnExplicitAncestorVaultGrant()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var targets = await client.GetFromJsonAsync<ResourceScopeTargetResponse[]>(
            $"/api/resource-scopes/targets?kind={SecretResourceKinds.Secret}");
        Assert.IsNotNull(targets);
        var tenant = targets.Single(value => value.Kind == ResourceScopeKind.Tenant);
        var workspace = targets.Single(value => value.Kind == ResourceScopeKind.Workspace);
        Assert.IsTrue(tenant.CanWrite);
        Assert.IsTrue(workspace.CanWrite);

        using var invalidGrant = await client.PostAsJsonAsync("/api/vaults", new CreateVaultRequest(
            "invalid-grant-vault", new VaultProperties
            {
                DisplayName = "Invalid grant Vault",
                ProviderType = "local",
                UsePolicy = new DescendantUsePolicy { Grants = [new(ResourceScopeRef.Instance)] }
            }, tenant.ScopeRef));
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, invalidGrant.StatusCode);

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

        using var tenantVaultRead = await client.GetAsync($"/api/vaults/tenant-vault?scopeRef={Uri.EscapeDataString(tenant.ScopeRef.Value)}");
        Assert.AreEqual(HttpStatusCode.OK, tenantVaultRead.StatusCode);
        using var grantRequest = new HttpRequestMessage(HttpMethod.Put,
            $"/api/vaults/tenant-vault?scopeRef={Uri.EscapeDataString(tenant.ScopeRef.Value)}")
        {
            Content = JsonContent.Create(new PutVaultRequest(new VaultProperties
            {
                DisplayName = "Tenant Vault",
                ProviderType = "local",
                UsePolicy = new DescendantUsePolicy { Grants = [new(workspace.ScopeRef)] }
            }))
        };
        grantRequest.Headers.IfMatch.ParseAdd(tenantVaultRead.Headers.ETag!.ToString());
        using var grantResponse = await client.SendAsync(grantRequest);
        Assert.AreEqual(HttpStatusCode.OK, grantResponse.StatusCode);
        using var savedVaultResponse = await client.GetAsync($"/api/vaults/tenant-vault?scopeRef={Uri.EscapeDataString(tenant.ScopeRef.Value)}");
        var savedVault = await savedVaultResponse.Content.ReadFromJsonAsync<VaultResponse>();
        Assert.AreEqual(workspace.ScopeRef, savedVault?.Resource.Definition.UsePolicy.Grants.Single().ScopeRef);

        using var allowed = await client.PostAsJsonAsync("/api/secrets",
            new CreateSecretRequest("granted-workspace-secret", new SecretProperties
            {
                DisplayName = "Granted workspace Secret",
                Vault = new ResourceReference("tenant-vault", tenant.ScopeRef),
                Key = "granted-workspace-secret"
            }, workspace.ScopeRef));
        Assert.AreEqual(HttpStatusCode.Created, allowed.StatusCode);

        using var deleteVault = new HttpRequestMessage(HttpMethod.Delete,
            $"/api/vaults/tenant-vault?scopeRef={Uri.EscapeDataString(tenant.ScopeRef.Value)}");
        deleteVault.Headers.IfMatch.ParseAdd(grantResponse.Headers.ETag!.ToString());
        using var inUse = await client.SendAsync(deleteVault);
        Assert.AreEqual(HttpStatusCode.Conflict, inUse.StatusCode);

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
    public async Task SecretResolutionUsesExactScopeAndRequiresItsOwnGrant()
    {
        var vaultProvider = new TestVaultProvider();
        await using var factory = Factory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddSingleton<ISecretVaultProvider>(vaultProvider)));
        using var client = factory.CreateClient();
        var targets = (await client.GetFromJsonAsync<ResourceScopeTargetResponse[]>(
            $"/api/resource-scopes/targets?kind={SecretResourceKinds.Secret}"))!;
        var tenant = targets.Single(value => value.Kind == ResourceScopeKind.Tenant).ScopeRef;
        var workspace = targets.Single(value => value.Kind == ResourceScopeKind.Workspace).ScopeRef;
        using var vaultResponse = await client.PostAsJsonAsync("/api/vaults", new CreateVaultRequest(
            "resolution-vault", new VaultProperties { DisplayName = "Resolution Vault", ProviderType = "test" }, tenant));
        Assert.AreEqual(HttpStatusCode.Created, vaultResponse.StatusCode);
        using var secretResponse = await client.PostAsJsonAsync("/api/secrets", new CreateSecretRequest(
            "resolution-secret", new SecretProperties
            {
                DisplayName = "Resolution Secret",
                Vault = new ResourceReference("resolution-vault", tenant),
                Key = "resolution-secret"
            }, tenant));
        Assert.AreEqual(HttpStatusCode.Created, secretResponse.StatusCode);

        const string secretValue = "private-value-must-not-appear-in-management";
        using var setValue = await client.PutAsJsonAsync(
            $"/api/secrets/resolution-secret/value?scopeRef={Uri.EscapeDataString(tenant.Value)}",
            new SetSecretValueRequest(secretValue));
        Assert.AreEqual(HttpStatusCode.NoContent, setValue.StatusCode);
        using var valueRead = await client.GetAsync($"/api/secrets/resolution-secret?scopeRef={Uri.EscapeDataString(tenant.Value)}");
        using var valueList = await client.GetAsync("/api/secrets");
        using var valueUsages = await client.GetAsync($"/api/secrets/resolution-secret/usages?scopeRef={Uri.EscapeDataString(tenant.Value)}");
        Assert.AreEqual(HttpStatusCode.OK, valueRead.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, valueList.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, valueUsages.StatusCode);
        foreach (var response in new[] { valueRead, valueList, valueUsages })
            Assert.IsFalse((await response.Content.ReadAsStringAsync()).Contains(secretValue, StringComparison.Ordinal));

        var resolver = factory.Services.GetRequiredService<ISecretResolver>();
        var authorizer = factory.Services.GetRequiredService<ISecretAccessAuthorizer>();
        var context = new SecretResolutionContext(workspace,
            ResourceAddress.Create(ResourceNamespace.Default, "ModelProvider", "consumer"));
        var address = ResourceAddress.Create(ResourceNamespace.Default, SecretResourceKinds.Secret, "resolution-secret");
        using (factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem())
        {
            await Assert.ThrowsAsync<SecretAccessDeniedException>(async () =>
                await authorizer.GetAuthorizedStatusAsync(new SecretReference(address, tenant), context));
            Assert.AreEqual(SecretValueStatus.Missing, await authorizer.GetAuthorizedStatusAsync(new SecretReference(address), context));
            await Assert.ThrowsAsync<SecretAccessDeniedException>(async () =>
                await resolver.ResolveAsync(new SecretReference(address, tenant), context));
            Assert.IsNull(await resolver.ResolveAsync(new SecretReference(address), context));
        }

        using var secretRead = await client.GetAsync($"/api/secrets/resolution-secret?scopeRef={Uri.EscapeDataString(tenant.Value)}");
        using var grantRequest = new HttpRequestMessage(HttpMethod.Put,
            $"/api/secrets/resolution-secret?scopeRef={Uri.EscapeDataString(tenant.Value)}")
        {
            Content = JsonContent.Create(new PutSecretRequest(new SecretProperties
            {
                DisplayName = "Resolution Secret",
                Vault = new ResourceReference("resolution-vault", tenant),
                Key = "resolution-secret",
                UsePolicy = new DescendantUsePolicy { Grants = [new(workspace)] }
            }))
        };
        grantRequest.Headers.IfMatch.ParseAdd(secretRead.Headers.ETag!.ToString());
        using var granted = await client.SendAsync(grantRequest);
        Assert.AreEqual(HttpStatusCode.OK, granted.StatusCode);
        using var savedSecretResponse = await client.GetAsync($"/api/secrets/resolution-secret?scopeRef={Uri.EscapeDataString(tenant.Value)}");
        var savedSecret = await savedSecretResponse.Content.ReadFromJsonAsync<SecretResponse>();
        Assert.AreEqual(workspace, savedSecret?.Resource.Definition.UsePolicy.Grants.Single().ScopeRef);

        var capabilities = factory.Services.GetRequiredService<ISecretCapabilityService>();
        var capabilityContext = new SecretCapabilityContext(
            ScopedResourceAddress.Create(ResourceScopeRef.Instance, ResourceNamespace.Default, "ExtensionRegistration", "test-extension"),
            "test.extension",
            ScopedResourceAddress.Create(workspace, ResourceNamespace.Default, "ModelProvider", "consumer"),
            "credential",
            Guid.NewGuid().ToString("N"));
        SecretCapabilityHandle capability;
        using (factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem())
        {
            var readsBeforeIssuance = vaultProvider.GetCalls;
            capability = await capabilities.IssueAsync(
                capabilityContext,
                new SecretBinding("credential", new SecretReference(address, tenant)),
                ["credential"],
                CancellationToken.None);
            Assert.AreEqual(readsBeforeIssuance, vaultProvider.GetCalls);
        }

        using (factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem())
        using (var resolved = await resolver.ResolveAsync(new SecretReference(address, tenant), context))
        {
            Assert.AreEqual(SecretValueStatus.Configured, await authorizer.GetAuthorizedStatusAsync(new SecretReference(address, tenant), context));
            Assert.IsNotNull(resolved);
            Assert.AreEqual("[REDACTED]", resolved.ToString());
        }

        var identities = factory.Services.GetRequiredService<IIdentityStore>();
        var now = factory.Services.GetRequiredService<TimeProvider>().GetUtcNow();
        var siblingId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var foreignWorkspaceId = Guid.NewGuid();
        await identities.AddWorkspaceAsync(new Workspace(siblingId, tenant.TargetId!.Value,
            "sibling", "Sibling", WorkspaceStatus.Active, now), default);
        await identities.AddTenantAsync(new Tenant(otherTenantId, "other-tenant", "Other tenant", TenantStatus.Active, now), default);
        await identities.AddWorkspaceAsync(new Workspace(foreignWorkspaceId, otherTenantId,
            "foreign", "Foreign", WorkspaceStatus.Active, now), default);
        using (factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem())
        {
            foreach (var scope in new[] { ResourceScopeRef.Workspace(siblingId), ResourceScopeRef.Workspace(foreignWorkspaceId) })
            {
                var deniedContext = capabilityContext with
                {
                    Consumer = ScopedResourceAddress.Create(scope, ResourceNamespace.Default, "ModelProvider", "consumer")
                };
                var denied = await Assert.ThrowsExactlyAsync<SecretCapabilityException>(() => capabilities.IssueAsync(
                    deniedContext, new SecretBinding("credential", new SecretReference(address, tenant)),
                    ["credential"], CancellationToken.None));
                Assert.AreEqual("access_denied", denied.Code);
            }
        }

        SecretCapabilityHandle apiHandle;
        using (factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem())
            apiHandle = await capabilities.IssueAsync(capabilityContext,
                new SecretBinding("credential", new SecretReference(address, tenant)),
                ["credential"], CancellationToken.None);
        var grant = new AepSecretAccessGrant(AepProtocol.SecretAccessVersion,
            new Uri(client.BaseAddress!, AepProtocol.SecretAccessPath), capabilityContext.ExtensionId,
            capabilityContext.RequirementId, capabilityContext.ExecutionId, apiHandle.RevealForTransport());
        var bytes = await new AepSecretAccessClient(client).RedeemAsync(grant);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, bytes);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);

        using var revokeRequest = new HttpRequestMessage(HttpMethod.Put,
            $"/api/secrets/resolution-secret?scopeRef={Uri.EscapeDataString(tenant.Value)}")
        {
            Content = JsonContent.Create(new PutSecretRequest(new SecretProperties
            {
                DisplayName = "Resolution Secret",
                Vault = new ResourceReference("resolution-vault", tenant),
                Key = "resolution-secret"
            }))
        };
        revokeRequest.Headers.IfMatch.ParseAdd(granted.Headers.ETag!.ToString());
        using var revoked = await client.SendAsync(revokeRequest);
        Assert.AreEqual(HttpStatusCode.OK, revoked.StatusCode);
        using (factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem())
        {
            await Assert.ThrowsAsync<SecretAccessDeniedException>(async () =>
                await authorizer.GetAuthorizedStatusAsync(new SecretReference(address, tenant), context));
            await Assert.ThrowsAsync<SecretAccessDeniedException>(async () =>
                await resolver.ResolveAsync(new SecretReference(address, tenant), context));
            var denied = await Assert.ThrowsExactlyAsync<SecretCapabilityException>(() =>
                capabilities.RedeemAsync(capability.RevealForTransport(), capabilityContext));
            Assert.AreEqual("access_denied", denied.Code);
        }
    }

    private sealed class TestVaultProvider : ISecretVaultProvider
    {
        private int getCalls;
        public int GetCalls => Volatile.Read(ref getCalls);
        public string ProviderType => "test";
        public Task<SecretValueStatus> GetStatusAsync(SecretVaultContext context, string key, CancellationToken cancellationToken = default) => Task.FromResult(SecretValueStatus.Configured);
        public Task<SecretValue?> GetAsync(SecretVaultContext context, string key, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref getCalls);
            return Task.FromResult<SecretValue?>(new([1, 2, 3]));
        }
        public Task SetAsync(SecretVaultContext context, string key, SecretValue value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(SecretVaultContext context, string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [TestMethod]
    public async Task ResourceScopeInventoryExposesTheAccessibleHierarchyAndExactOwnership()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var targets = await client.GetFromJsonAsync<ResourceScopeTargetResponse[]>(
            $"/api/resource-scopes/targets?kind={SecretResourceKinds.Vault}");
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
            value.Kind == SecretResourceKinds.Vault && value.Name == "inventory-vault"));
        Assert.IsFalse(workspaceNode.Resources.Any(value => value.Name == "inventory-vault"));
    }

    [TestMethod]
    public async Task ResourceCreationHonorsTheScopeSelectedByTheConsole()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var targets = await client.GetFromJsonAsync<ResourceScopeTargetResponse[]>(
            $"/api/resource-scopes/targets?kind={RuntimeProfileResourceKinds.RuntimeProfile}");
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
