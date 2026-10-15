using System.Net;
using System.Net.Http.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.Client;
using Agentstration.Agents;
using Agentstration.DataSources.Contracts;
using Agentstration.Flows;
using Agentstration.Flows.Application;
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
            new[] { ResourceScopeKind.Instance },
            ResourceScopePolicy.AllowedScopes(ModelResourceKinds.ModelProvider).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { ResourceScopeKind.Instance },
            ResourceScopePolicy.AllowedScopes(ModelResourceKinds.Model).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { ResourceScopeKind.Instance },
            ResourceScopePolicy.AllowedScopes(RuntimeProfileResourceKinds.RuntimeProfile).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { ResourceScopeKind.Instance, ResourceScopeKind.Tenant, ResourceScopeKind.Workspace },
            ResourceScopePolicy.AllowedScopes(ModelResourceKinds.ModelProfile).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { ResourceScopeKind.Instance, ResourceScopeKind.Tenant, ResourceScopeKind.Workspace },
            ResourceScopePolicy.AllowedScopes(AgentResourceKinds.Agent).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { ResourceScopeKind.Instance, ResourceScopeKind.Tenant, ResourceScopeKind.Workspace },
            ResourceScopePolicy.AllowedScopes(AgentResourceKinds.AgentRevision).ToArray());
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
        CollectionAssert.AreEquivalent(new[] { ResourceScopeKind.Workspace },
            ResourceScopePolicy.AllowedScopes(DataSourceResourceKinds.DataSourceDiscovery).ToArray());
        CollectionAssert.AreEquivalent(new[] { ResourceScopeKind.Workspace },
            ResourceScopePolicy.AllowedScopes(DataSourceResourceKinds.DataSourceAcquisitionTarget).ToArray());
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
        Assert.IsEmpty((await valueList.Content.ReadFromJsonAsync<SecretResponse[]>())!,
            "An ancestor Secret without a discover grant must not appear in an effective list.");
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
        Assert.IsNotNull(savedSecret);
        Assert.AreEqual(workspace, savedSecret.Resource.Definition.UsePolicy.Grants.Single().ScopeRef);
        var visibleAfterGrant = await client.GetFromJsonAsync<SecretResponse[]>("/api/secrets");
        Assert.IsTrue(visibleAfterGrant!.Any(value => value.Resource.Uid == savedSecret.Resource.Uid));

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
        }
        var revokedDenial = await Assert.ThrowsExactlyAsync<SecretCapabilityException>(() =>
            capabilities.RedeemAsync(capability.RevealForTransport(), capabilityContext));
        Assert.AreEqual("access_denied", revokedDenial.Code);
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
        var instance = targets!.Single();
        Assert.IsTrue(instance.CanWrite);

        using var created = await client.PostAsJsonAsync(
            "/api/runtimeprofiles",
            new CreateRuntimeProfileRequest(
                "explicit-scope-runtime",
                new RuntimeProfileProperties { DisplayName = "Explicit scope runtime", RuntimeType = "microsoft-agent-framework" },
                ScopeRef: instance.ScopeRef));

        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
        var resource = await created.Content.ReadFromJsonAsync<RuntimeProfileResource>();
        Assert.AreEqual(instance.ScopeRef, resource?.ScopeRef);
    }

    [TestMethod]
    public async Task HierarchicalModelProfileRequiresAccessAndAgentRevisionPinsExactDependencies()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        var profiles = factory.Services.GetRequiredService<ModelProfileManagementService>();
        var agents = factory.Services.GetRequiredService<AgentManagementService>();
        var policies = factory.Services.GetRequiredService<IResourceAccessPolicyService>();
        var tenantScope = ResourceScopeRef.Tenant(context.TenantId);
        var workspaceScope = ResourceScopeRef.Workspace(context.WorkspaceId);

        var profile = await profiles.CreateAsync(new ModelProfileResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ModelResourceKinds.ModelProfile,
            Metadata = new ResourceMetadata { Name = "tenant-reasoning" },
            ScopeRef = tenantScope,
            Definition = new ModelProfileProperties
            {
                DisplayName = "Tenant reasoning",
                Provider = new ResourceReference("ollama-local", ResourceScopeRef.Instance),
                Model = new ModelSelection { Name = "test-model" }
            }
        }, default);
        var instanceProfile = await profiles.GetExactAsync(ResourceScopeRef.Instance,
            ResourceNamespace.Default, "reasoning-default", default);
        Assert.IsNotNull(instanceProfile);
        var workspaceProfile = await profiles.CreateAsync(new ModelProfileResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ModelResourceKinds.ModelProfile,
            Metadata = new ResourceMetadata { Name = profile.Value.Name },
            ScopeRef = workspaceScope,
            Definition = profile.Value.Definition with { DisplayName = "Workspace reasoning" }
        }, default);
        Assert.AreEqual(ResourceScopeRef.Instance, instanceProfile.Value.ScopeRef);
        Assert.AreEqual(tenantScope, profile.Value.ScopeRef);
        Assert.AreEqual(workspaceScope, workspaceProfile.Value.ScopeRef);
        var agent = new AgentResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = AgentResourceKinds.Agent,
            Metadata = new ResourceMetadata { Name = "pinned-model-agent" },
            ScopeRef = workspaceScope,
            Definition = new AgentProperties
            {
                DisplayName = "Pinned model Agent",
                Instructions = "Use the pinned profile.",
                ModelProfile = new ResourceReference(profile.Value.Name, tenantScope),
                RuntimeProfile = new ResourceReference("maf-builtin", ResourceScopeRef.Instance)
            }
        };

        var ambiguousAgent = agent with
        {
            Metadata = new ResourceMetadata { Name = "ambiguous-model-agent" },
            Definition = agent.Definition with { ModelProfile = new ResourceReference(profile.Value.Name) }
        };
        await Assert.ThrowsExactlyAsync<ModelProfileValidationException>(async () =>
            await agents.PutAgentAsync(ambiguousAgent, null, true, default));

        await Assert.ThrowsExactlyAsync<ModelProfileValidationException>(async () =>
            await agents.PutAgentAsync(agent, null, true, default));

        _ = await policies.PutAsync(new ResourceAccessPolicy(
            new(profile.Value.Uid, tenantScope, profile.Value.Namespace, profile.Value.Kind,
                profile.Value.Generation),
            [new(workspaceScope, [ResourceAccessAction.Use])]), null, default);
        var runtime = await factory.Services.GetRequiredService<IResourceStore>()
            .GetExactAsync<RuntimeProfileResource>(ScopedResourceAddress.Create(ResourceScopeRef.Instance,
                ResourceNamespace.Default, RuntimeProfileResourceKinds.RuntimeProfile, "maf-builtin"), default);
        Assert.IsNotNull(runtime);
        if (await policies.GetAsync(runtime.Value.Uid, default) is null)
        {
            _ = await policies.PutAsync(new ResourceAccessPolicy(
                new(runtime.Value.Uid, ResourceScopeRef.Instance, runtime.Value.Namespace,
                    RuntimeProfileResourceKinds.RuntimeProfile, runtime.Value.Generation),
                [new(workspaceScope, [ResourceAccessAction.Use])]), null, default);
        }
        var storedAgent = await agents.PutAgentAsync(agent, null, true, default);
        var revision = await agents.EnsureExecutionRevisionAsync(ResourceNamespace.Default,
            storedAgent.Value.Name, storedAgent.Value.Generation, default);

        Assert.AreEqual(profile.Value.Uid, revision.Value.Definition.ModelProfilePin?.Uid);
        Assert.AreEqual(tenantScope, revision.Value.Definition.ModelProfilePin?.ScopeRef);
        Assert.AreEqual(profile.Value.Generation, revision.Value.Definition.ModelProfilePin?.Generation);
        Assert.AreEqual(ResourceScopeRef.Instance, revision.Value.Definition.RuntimeProfilePin?.ScopeRef);
        Assert.AreEqual("maf-builtin", revision.Value.Definition.RuntimeProfilePin?.Name);

        _ = await profiles.PutAsync(profile.Value.Namespace, profile.Value.Name, tenantScope,
            profile.Value.Definition with { Description = "Generation two" }, profile.ETag, default);
        var staleAgent = agent with
        {
            Metadata = new ResourceMetadata { Name = "stale-model-agent" }
        };
        await Assert.ThrowsExactlyAsync<ModelProfileValidationException>(async () =>
            await agents.PutAgentAsync(staleAgent, null, true, default));
    }

    [TestMethod]
    public async Task SameNamedAgentsRemainExactAcrossAllOwnershipScopesAndFlowPublication()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        var store = factory.Services.GetRequiredService<IResourceStore>();
        var policies = factory.Services.GetRequiredService<IResourceAccessPolicyService>();
        var agents = factory.Services.GetRequiredService<AgentManagementService>();
        var runtimeAgents = factory.Services.GetRequiredService<IRuntimeAgentResolver>();
        using var client = factory.CreateClient();
        var tenantScope = ResourceScopeRef.Tenant(context.TenantId);
        var workspaceScope = ResourceScopeRef.Workspace(context.WorkspaceId);
        var scopes = new[] { ResourceScopeRef.Instance, tenantScope, workspaceScope };
        var profile = await store.GetExactAsync<ModelProfileResource>(ScopedResourceAddress.Create(
            ResourceScopeRef.Instance, ResourceNamespace.Default, ModelResourceKinds.ModelProfile,
            "reasoning-default"), default);
        var runtime = await store.GetExactAsync<RuntimeProfileResource>(ScopedResourceAddress.Create(
            ResourceScopeRef.Instance, ResourceNamespace.Default, RuntimeProfileResourceKinds.RuntimeProfile,
            "maf-builtin"), default);
        Assert.IsNotNull(profile);
        Assert.IsNotNull(runtime);
        await GrantDescendantsAsync(profile.Value);
        await GrantDescendantsAsync(runtime.Value);

        foreach (var scopeRef in scopes)
        {
            var stored = await agents.PutAgentAsync(new AgentResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = AgentResourceKinds.Agent,
                Metadata = new ResourceMetadata { Name = "shared-agent" },
                ScopeRef = scopeRef,
                Definition = new AgentProperties
                {
                    DisplayName = $"Agent at {scopeRef}",
                    Instructions = "Keep the exact owner graph.",
                    ModelProfile = new ResourceReference("reasoning-default", ResourceScopeRef.Instance),
                    RuntimeProfile = new ResourceReference("maf-builtin", ResourceScopeRef.Instance)
                }
            }, null, true, default);
            var revision = await agents.EnsureExecutionRevisionAsync(ResourceNamespace.Default,
                stored.Value.Name, scopeRef, stored.Value.Generation, default);
            Assert.AreEqual(scopeRef, stored.Value.ScopeRef);
            var apiAgent = await client.GetFromJsonAsync<AgentResource>(
                $"/api/agents/shared-agent?scopeRef={Uri.EscapeDataString(scopeRef.ToString())}");
            Assert.IsNotNull(apiAgent);
            Assert.AreEqual(stored.Value.Uid, apiAgent.Uid);
            Assert.AreEqual(scopeRef, apiAgent.ScopeRef);
            Assert.AreEqual(scopeRef, revision.Value.ScopeRef);
            Assert.AreEqual(stored.Value.Uid, revision.Value.AgentUid);
            Assert.AreEqual(ResourceScopeRef.Instance, revision.Value.Definition.ModelProfilePin?.ScopeRef);
            Assert.AreEqual(ResourceScopeRef.Instance, revision.Value.Definition.RuntimeProfilePin?.ScopeRef);
            var resolved = await runtimeAgents.ResolveAsync(new RuntimeAgentReference(stored.Value.Name,
                stored.Value.Generation) { ScopeRef = scopeRef }, default);
            Assert.AreEqual(stored.Value.Uid, resolved.AgentId);
            Assert.AreEqual(scopeRef, resolved.AgentScopeRef);
            Assert.AreEqual(revision.Value.Uid, resolved.RevisionUid);
        }

        var flowService = factory.Services.GetRequiredService<FlowService>();
        var flowId = new FlowId("instance-agent-flow");
        _ = await flowService.CreateExactAsync(ResourceScopeRef.Instance,
            new CreateFlowCommand(flowId.Value, null, "1.0.0", true,
                new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, "shared-agent",
                    Namespace: ResourceNamespace.Default, ScopeRef: ResourceScopeRef.Instance))),
            ResourceNamespace.Default, default);
        var published = await flowService.PublishVersionExactAsync(ResourceScopeRef.Instance, flowId,
            "1.0.0", false, default);
        var target = ((DirectFlowDefinition)published.Value.Definition).Target;
        var instanceAgent = await agents.GetAgentExactAsync(ResourceScopeRef.Instance,
            ResourceNamespace.Default, "shared-agent", default);
        var instanceRevision = await agents.EnsureExecutionRevisionAsync(ResourceNamespace.Default,
            "shared-agent", ResourceScopeRef.Instance, 1, default);
        Assert.AreEqual(ResourceScopeRef.Instance, target.ScopeRef);
        Assert.AreEqual("1", target.Version);
        Assert.AreEqual(instanceAgent!.Value.Uid, target.ResourceUid);
        Assert.AreEqual(instanceRevision.Value.Uid, target.RevisionUid);
        Assert.AreEqual(instanceRevision.Value.DefinitionHash, target.DefinitionHash);
        var staleReference = new RuntimeAgentReference("shared-agent", 1)
        {
            ScopeRef = ResourceScopeRef.Instance,
            ResourceUid = Guid.NewGuid(),
            RevisionUid = instanceRevision.Value.Uid,
            DefinitionHash = instanceRevision.Value.DefinitionHash
        };
        var stale = await Assert.ThrowsExactlyAsync<RuntimeAgentResolutionException>(async () =>
            await runtimeAgents.ResolveAsync(staleReference, default));
        Assert.AreEqual("agent_reference_stale", stale.Code);

        async Task GrantDescendantsAsync(Resource resource)
        {
            var current = await policies.GetAsync(resource.Uid, default);
            var grants = (current?.Policy.Grants ?? [])
                .Where(value => value.TargetScopeRef != tenantScope)
                .Append(new ResourceAccessGrant(tenantScope, [ResourceAccessAction.Use], IncludeDescendants: true))
                .ToArray();
            _ = await policies.PutAsync(new ResourceAccessPolicy(new(resource.Uid, ResourceScopeRef.Instance,
                resource.Namespace, resource.Kind, resource.Generation), grants), current?.ETag, default);
        }
    }
}
