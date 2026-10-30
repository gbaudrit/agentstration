using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Agents;
using Agentstration.Agents.Contracts;
using Agentstration.Api.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ModelProviders;
using Agentstration.Models;
using Agentstration.Models.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Secrets.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class ModelProfileApiTests : ModelManagementApiTestBase
{
    [TestMethod]
    public async Task InstanceProfileResolutionRemainsAvailableFromAnAuthorizedWorkspace()
    {
        await using var factory = DiagnosticFactory();
        var context = await GetBootstrapContextAsync(factory);
        var scopes = factory.Services.GetRequiredService<IRequestContextScopeFactory>();
        var policies = factory.Services.GetRequiredService<IResourceAccessPolicyService>();
        var profiles = factory.Services.GetRequiredService<ModelProfileManagementService>();
        using var client = factory.CreateClient();
        SelectConsoleContext(client, ResourceScopeRef.Instance);

        using var providerResponse = await client.PostAsJsonAsync("/api/modelproviders", new CreateModelProviderRequest(
            "workspace-visible-provider",
            new ModelProviderProperties
            {
                DisplayName = "Workspace-visible provider",
                Extension = new ResourceReference("ollama-extension"),
                ContributionId = "diagnostic"
            }));
        Assert.AreEqual(HttpStatusCode.Created, providerResponse.StatusCode,
            await providerResponse.Content.ReadAsStringAsync());
        using var refreshResponse = await client.PostAsync(
            "/api/modelproviders/workspace-visible-provider/models/refresh", null);
        Assert.AreEqual(HttpStatusCode.OK, refreshResponse.StatusCode,
            await refreshResponse.Content.ReadAsStringAsync());
        using var profileResponse = await client.PostAsJsonAsync("/api/modelprofiles", new CreateModelProfileRequest(
            "workspace-visible-profile",
            new ModelProfileProperties
            {
                DisplayName = "Workspace-visible profile",
                Provider = new ResourceReference("workspace-visible-provider", ResourceScopeRef.Instance),
                Model = new ModelSelection { Name = "local-model" }
            }, ScopeRef: ResourceScopeRef.Instance));
        Assert.AreEqual(HttpStatusCode.Created, profileResponse.StatusCode,
            await profileResponse.Content.ReadAsStringAsync());

        using (scopes.PushInstance(context))
        {
            var profile = await profiles.GetExactAsync(ResourceScopeRef.Instance,
                ResourceNamespace.Default, "workspace-visible-profile", default);
            Assert.IsNotNull(profile);
            _ = await policies.PutAsync(new ResourceAccessPolicy(
                new(profile.Value.Uid, ResourceScopeRef.Instance, profile.Value.Namespace,
                    ModelResourceKinds.ModelProfile, profile.Value.Generation),
                [new(ResourceScopeRef.Tenant(context.TenantId),
                    [ResourceAccessAction.Discover, ResourceAccessAction.Use], IncludeDescendants: true)]),
                null, default);
        }

        SelectConsoleContext(client, ResourceScopeRef.Workspace(context.WorkspaceId));
        var resolution = await client.GetFromJsonAsync<ModelProfileResolutionResponse>(
            "/api/modelprofiles/workspace-visible-profile/resolution?scopeRef=%2Finstance");

        Assert.IsNotNull(resolution);
        Assert.AreEqual("available", resolution.Status);
        Assert.AreEqual("local-model", resolution.Model?.Name);

        using (scopes.Push(context))
        {
            var deployment = await ((IModelDeploymentStore)profiles).GetRequiredAsync(
                ResourceScopeRef.Instance, ResourceNamespace.Default, "workspace-visible-profile", default);
            Assert.IsNotNull(deployment.EffectiveSpecification);
        }
    }

    [TestMethod]
    public async Task ProfileSecretBindingsPersistPerConsumerAndRejectUnknownRequirements()
    {
        await using var factory = Factory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IExtensionInspector>();
            services.AddSingleton<IExtensionInspector>(new ConfiguredEndpointInspector(
            [
                new AepValueRequirement(
                    AepContributionKinds.ModelProvider,
                    "ollama",
                    "credential",
                    true,
                    Protection: AepValueProtection.Secured)
            ]));
        }));
        var context = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        var profiles = factory.Services.GetRequiredService<ModelProfileManagementService>();
        var scope = ResourceScopeRef.Instance;

        async Task<ModelProfileResource> CreateAsync(string name, string secretName)
        {
            var stored = await profiles.CreateAsync(new ModelProfileResource
            {
                Metadata = new ResourceMetadata { Name = name },
                Kind = ModelResourceKinds.ModelProfile,
                ApiVersion = ResourceApiVersions.CoreV1,
                Definition = new ModelProfileProperties
                {
                    DisplayName = name,
                    Provider = new ResourceReference("ollama-local", ResourceScopeRef.Instance),
                    Model = new ModelSelection { Name = "test-model" },
                    SecretBindings = [new("credential", new(new(ResourceNamespace.Default, "Secret", secretName), scope))]
                }
            }, default);
            return stored.Value;
        }

        await CreateAsync("bound-profile-a", "company-a");
        await CreateAsync("bound-profile-b", "company-b");
        using var client = factory.CreateClient();
        var first = await client.GetFromJsonAsync<ModelProfileResource>("/api/modelprofiles/bound-profile-a");
        var second = await client.GetFromJsonAsync<ModelProfileResource>("/api/modelprofiles/bound-profile-b");
        Assert.IsNotNull(first);
        Assert.IsNotNull(second);
        Assert.AreEqual("company-a", first.Definition.SecretBindings.Single().Secret.Address.Name);
        Assert.AreEqual("company-b", second.Definition.SecretBindings.Single().Secret.Address.Name);
        Assert.AreEqual(scope, first.Definition.SecretBindings.Single().Secret.ScopeRef);

        var invalid = first with
        {
            Metadata = new ResourceMetadata { Name = "bound-profile-invalid" },
            Definition = first.Definition with
            {
                SecretBindings = [new SecretBinding("unknown", new(new(ResourceNamespace.Default, "Secret", "company-a"), scope))]
            }
        };
        await Assert.ThrowsExactlyAsync<ModelProfileValidationException>(async () => await profiles.CreateAsync(invalid, default));
    }

    [TestMethod]
    public async Task ManagedHostCompositionUsesPersistedModelProfileResolver()
    {
        await using var factory = new ApiOnlyWebApplicationFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("AI:Provider", "Managed");
            builder.UseSetting("ConnectionStrings:ollama-extension", "Endpoint=http://127.0.0.1:1");
            builder.UseSetting("Logging:LogLevel:Default", "Warning");
        });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsInstanceOfType<ChatClientResolver>(factory.Services.GetRequiredService<IChatClientResolver>());
    }

    [TestMethod]
    public async Task ModelProfileOptionMigrationPreviewsWithoutWritingAndAppliesWithETag()
    {
        await using var factory = Factory().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IExtensionInspector>();
                services.RemoveAll<IExtensionOptionsMigrator>();
                services.AddSingleton<IExtensionInspector, MigrationExtensionAdapter>();
                services.AddSingleton<IExtensionOptionsMigrator, MigrationExtensionAdapter>();
            }));
        var requestContext = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(requestContext);
        var profiles = factory.Services.GetRequiredService<ModelProfileManagementService>();
        var sourceVersion = MigrationExtensionAdapter.OptionSet.Versions.Single(value => value.Version == "1.0.0");
        var stored = await profiles.CreateAsync(new ModelProfileResource
        {
            Metadata = new ResourceMetadata { Name = "migration-profile" },
            Kind = ModelResourceKinds.ModelProfile,
            ApiVersion = ResourceApiVersions.CoreV1,
            Definition = new ModelProfileProperties
            {
                DisplayName = "Migration profile",
                Provider = new ResourceReference(ModelProviderManagementService.ModelProviderId("ollama-local"), ResourceScopeRef.Instance),
                Model = new ModelSelection { Name = "test-model" },
                ProviderOptions = new Dictionary<string, VersionedExtensionOptions>
                {
                    ["ollama"] = new()
                    {
                        OptionSet = MigrationExtensionAdapter.OptionSet.Id,
                        Version = sourceVersion.Version,
                        SchemaDigest = sourceVersion.SchemaDigest,
                        Values = JsonSerializer.SerializeToElement(new { legacyName = "kept" })
                    }
                }
            }
        }, default);
        using var client = factory.CreateClient();

        using var previewResponse = await client.PostAsJsonAsync(
            "/api/modelprofiles/migration-profile/option-migrations/preview",
            new PreviewModelProfileOptionMigrationRequest("2.0.0"));

        Assert.AreEqual(HttpStatusCode.OK, previewResponse.StatusCode);
        Assert.AreEqual(stored.ETag, previewResponse.Headers.ETag?.ToString());
        var preview = await previewResponse.Content.ReadFromJsonAsync<ModelProfileOptionMigrationPreviewResponse>();
        Assert.IsNotNull(preview);
        Assert.AreEqual("1.0.0", preview.Source.Version);
        Assert.AreEqual("2.0.0", preview.Target.Version);
        Assert.AreEqual("kept", preview.Target.Values.GetProperty("name").GetString());
        var unchanged = await profiles.GetAsync("migration-profile", default);
        Assert.AreEqual("1.0.0", unchanged!.Value.Definition.ProviderOptions["ollama"].Version);

        using var invalidPreview = await client.PostAsJsonAsync(
            "/api/modelprofiles/migration-profile/option-migrations/preview",
            new PreviewModelProfileOptionMigrationRequest("9.0.0"));
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, invalidPreview.StatusCode);
        unchanged = await profiles.GetAsync("migration-profile", default);
        Assert.AreEqual(stored.ETag, unchanged!.ETag);

        using var missingPrecondition = await client.PostAsJsonAsync(
            "/api/modelprofiles/migration-profile/option-migrations/apply",
            new PreviewModelProfileOptionMigrationRequest("2.0.0"));
        Assert.AreEqual(HttpStatusCode.Conflict, missingPrecondition.StatusCode);
        unchanged = await profiles.GetAsync("migration-profile", default);
        Assert.AreEqual(stored.ETag, unchanged!.ETag);

        using var apply = new HttpRequestMessage(HttpMethod.Post, "/api/modelprofiles/migration-profile/option-migrations/apply")
        {
            Content = JsonContent.Create(new PreviewModelProfileOptionMigrationRequest("2.0.0"))
        };
        apply.Headers.IfMatch.Add(previewResponse.Headers.ETag!);
        using var appliedResponse = await client.SendAsync(apply);
        Assert.AreEqual(HttpStatusCode.OK, appliedResponse.StatusCode);
        Assert.AreNotEqual(stored.ETag, appliedResponse.Headers.ETag?.ToString());
        var applied = await appliedResponse.Content.ReadFromJsonAsync<ModelProfileResource>();
        Assert.IsNotNull(applied);
        Assert.AreEqual("2.0.0", applied.Definition.ProviderOptions["ollama"].Version);
        Assert.AreEqual("kept", applied.Definition.ProviderOptions["ollama"].Values.GetProperty("name").GetString());
    }

    [TestMethod]
    public async Task ProfileCrudUsesETagAndAllowsTemporarilyUnavailableModel()
    {
        await using var factory = Factory();
        var requestContext = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(requestContext);
        using var client = factory.CreateClient();
        var create = Request("api-test-profile", "model-not-downloaded");

        using var createdResponse = await client.PostAsJsonAsync("/api/modelprofiles", create);
        Assert.AreEqual(HttpStatusCode.Created, createdResponse.StatusCode);
        Assert.IsNotNull(createdResponse.Headers.ETag);
        var created = await createdResponse.Content.ReadFromJsonAsync<ModelProfileResource>();
        Assert.IsNotNull(created);
        var profileStore = factory.Services.GetRequiredService<IModelProfileStore>();
        var deploymentStore = factory.Services.GetRequiredService<IModelDeploymentStore>();
        var runtimeProfile = await profileStore.GetRequiredAsync(created.Metadata.Name);
        var runtimeDeployment = await deploymentStore.GetRequiredAsync(runtimeProfile.DeploymentName);
        Assert.AreEqual(ModelProviderManagementService.ModelProviderId("ollama-local"), runtimeDeployment.ProviderName);
        Assert.AreEqual("model-not-downloaded", runtimeDeployment.ModelName);

        var updatedProperties = created.Definition with { Description = "Updated profile" };
        using var update = new HttpRequestMessage(HttpMethod.Put, "/api/modelprofiles/api-test-profile")
        {
            Content = JsonContent.Create(new PutModelProfileRequest(updatedProperties))
        };
        update.Headers.IfMatch.Add(createdResponse.Headers.ETag);
        using var updatedResponse = await client.SendAsync(update);
        Assert.AreEqual(HttpStatusCode.OK, updatedResponse.StatusCode);
        Assert.AreNotEqual(createdResponse.Headers.ETag, updatedResponse.Headers.ETag);

        var filtered = await client.GetFromJsonAsync<ValueResponse<ModelProfileSummaryResponse>>(
            "/api/modelprofiles?provider=ollama-local&model=model-not-downloaded&status=providerUnavailable&search=api-test");
        Assert.IsTrue(filtered!.Value.Any(profile => profile.Name == "api-test-profile"));

        using var staleUpdate = new HttpRequestMessage(HttpMethod.Put, "/api/modelprofiles/api-test-profile")
        {
            Content = JsonContent.Create(new PutModelProfileRequest(updatedProperties))
        };
        staleUpdate.Headers.IfMatch.Add(createdResponse.Headers.ETag);
        using var staleResponse = await client.SendAsync(staleUpdate);
        Assert.AreEqual(HttpStatusCode.Conflict, staleResponse.StatusCode);

        using var delete = new HttpRequestMessage(HttpMethod.Delete, "/api/modelprofiles/api-test-profile");
        delete.Headers.IfMatch.Add(updatedResponse.Headers.ETag!);
        using var deleted = await client.SendAsync(delete);
        Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    [TestMethod]
    public async Task UsedProfileCannotBeDeletedAndExposesUsagesResolutionAndAgentModel()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var usages = await client.GetFromJsonAsync<ModelProfileUsagesResponse>("/api/modelprofiles/reasoning-default/usages");
        var resolution = await client.GetFromJsonAsync<ModelProfileResolutionResponse>("/api/modelprofiles/reasoning-default/resolution");
        var agentModel = await client.GetFromJsonAsync<AgentModelResponse>("/api/agents/sql-expert/model");
        using var deleted = await client.DeleteAsync("/api/modelprofiles/reasoning-default");

        Assert.IsTrue(usages!.Count >= 1);
        Assert.IsTrue(usages.Value.Any(usage => usage.Name == "sql-expert"));
        Assert.AreEqual("unavailable", resolution!.Status);
        Assert.AreEqual("reasoning-default", agentModel!.Declared.ModelProfile.Name);
        Assert.AreEqual(HttpStatusCode.Conflict, deleted.StatusCode);
        Assert.AreEqual("application/problem+json", deleted.Content.Headers.ContentType?.MediaType);
    }

    [TestMethod]
    public async Task InstanceProfileUsagesIncludeAuthorizedDescendantAgentsAndExactScopeIdentity()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        var scopes = factory.Services.GetRequiredService<IRequestContextScopeFactory>();
        var profiles = factory.Services.GetRequiredService<ModelProfileManagementService>();
        var agents = factory.Services.GetRequiredService<AgentManagementService>();
        var store = factory.Services.GetRequiredService<IResourceStore>();
        var policies = factory.Services.GetRequiredService<IResourceAccessPolicyService>();
        var workspaceScope = ResourceScopeRef.Workspace(context.WorkspaceId);
        ModelProfileResource instanceProfile;
        StoredResource<AgentResource> instanceAgent;
        StoredResource<AgentResource> workspaceAgent;
        StoredResource<ModelProfileResource> homonym;

        using (scopes.PushInstance(context))
        {
            instanceProfile = (await profiles.CreateAsync(Profile("cross-scope-profile", ResourceScopeRef.Instance), default)).Value;
            _ = await policies.PutAsync(new ResourceAccessPolicy(
                new(instanceProfile.Uid, ResourceScopeRef.Instance, instanceProfile.Namespace,
                    ModelResourceKinds.ModelProfile, instanceProfile.Generation),
                [new(ResourceScopeRef.Tenant(context.TenantId),
                    [ResourceAccessAction.Discover, ResourceAccessAction.Use], IncludeDescendants: true)]),
                null,
                default);
            instanceAgent = await agents.PutAgentAsync(
                Agent("instance-profile-consumer", ResourceScopeRef.Instance,
                    new ResourceReference(instanceProfile.Name, ResourceScopeRef.Instance)),
                null,
                true,
                default);
        }

        using (scopes.Push(context))
        {
            workspaceAgent = await agents.PutAgentAsync(
                Agent("workspace-profile-consumer", workspaceScope,
                    new ResourceReference(instanceProfile.Name, ResourceScopeRef.Instance)),
                null,
                true,
                default);
            homonym = await profiles.CreateAsync(Profile(instanceProfile.Name, workspaceScope), default);
            _ = await agents.PutAgentAsync(
                Agent("homonymous-profile-consumer", workspaceScope,
                    new ResourceReference(homonym.Value.Name, workspaceScope)),
                null,
                true,
                default);
        }

        IReadOnlyList<ModelProfileUsage> usages;
        using (scopes.PushInstance(context))
            usages = await profiles.GetUsagesAsync(instanceProfile, default);

        Assert.HasCount(2, usages);
        Assert.IsTrue(usages.Any(value => value.Name == "instance-profile-consumer"
            && value.ScopeRef == ResourceScopeRef.Instance));
        Assert.IsTrue(usages.Any(value => value.Name == "workspace-profile-consumer"
            && value.ScopeRef == workspaceScope));
        Assert.IsFalse(usages.Any(value => value.Name == "homonymous-profile-consumer"));
        Assert.IsTrue(usages.All(value => value.Namespace == ResourceNamespace.Default));

        using (scopes.Push(context))
            workspaceAgent = await store.PutExactAsync(workspaceScope, workspaceAgent.Value with
            {
                Generation = checked(workspaceAgent.Value.Generation + 1),
                Definition = workspaceAgent.Value.Definition with
                {
                    ModelProfile = new ResourceReference(homonym.Value.Name, workspaceScope)
                }
            }, workspaceAgent.ETag, false, default);
        using (scopes.PushInstance(context))
        {
            usages = await profiles.GetUsagesAsync(instanceProfile, default);
            Assert.HasCount(1, usages);
            Assert.AreEqual("instance-profile-consumer", usages.Single().Name);
            await store.DeleteExactAsync(ScopedResourceAddress.Create(ResourceScopeRef.Instance,
                instanceAgent.Value.Namespace, instanceAgent.Value.Kind, instanceAgent.Value.Name),
                instanceAgent.ETag, default);
            usages = await profiles.GetUsagesAsync(instanceProfile, default);
        }
        Assert.HasCount(0, usages);
    }

    [TestMethod]
    public async Task TenantProfileUsagesStayInsideTheTenantHierarchy()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        var scopes = factory.Services.GetRequiredService<IRequestContextScopeFactory>();
        var identities = factory.Services.GetRequiredService<IIdentityStore>();
        var store = factory.Services.GetRequiredService<IResourceStore>();
        var profiles = factory.Services.GetRequiredService<ModelProfileManagementService>();
        var policies = factory.Services.GetRequiredService<IResourceAccessPolicyService>();
        var now = factory.Services.GetRequiredService<TimeProvider>().GetUtcNow();
        var tenantScope = ResourceScopeRef.Tenant(context.TenantId);
        var workspaceScope = ResourceScopeRef.Workspace(context.WorkspaceId);
        var siblingWorkspace = new Workspace(Guid.NewGuid(), context.TenantId, "sibling", "Sibling workspace",
            WorkspaceStatus.Active, now);
        var foreignTenant = new Tenant(Guid.NewGuid(), "foreign", "Foreign tenant", TenantStatus.Active, now);
        var foreignWorkspace = new Workspace(Guid.NewGuid(), foreignTenant.Id, "foreign-workspace",
            "Foreign workspace", WorkspaceStatus.Active, now);
        await identities.AddWorkspaceAsync(siblingWorkspace, default);
        await identities.AddTenantAsync(foreignTenant, default);
        await identities.AddWorkspaceAsync(foreignWorkspace, default);

        StoredResource<ModelProfileResource> tenantProfile;
        using (scopes.PushTenantAdministration(context))
        {
            tenantProfile = await store.PutExactAsync(tenantScope, Profile("tenant-impact", tenantScope),
                null, true, default);
            _ = await policies.PutAsync(new ResourceAccessPolicy(
                new(tenantProfile.Value.Uid, tenantScope, tenantProfile.Value.Namespace,
                    tenantProfile.Value.Kind, tenantProfile.Value.Generation),
                [
                    new(workspaceScope, [ResourceAccessAction.Discover, ResourceAccessAction.Use]),
                    new(ResourceScopeRef.Workspace(siblingWorkspace.Id),
                        [ResourceAccessAction.Discover, ResourceAccessAction.Use])
                ]), null, default);
            _ = await PutAgentAsync(store, tenantScope, "tenant-consumer", tenantProfile.Value);
        }
        using (scopes.Push(context))
            _ = await PutAgentAsync(store, workspaceScope, "workspace-consumer", tenantProfile.Value);
        using (scopes.Push(new RequestContext(context.PrincipalId, context.TenantId, siblingWorkspace.Id)))
            _ = await PutAgentAsync(store, ResourceScopeRef.Workspace(siblingWorkspace.Id),
                "sibling-workspace-consumer", tenantProfile.Value);

        using (scopes.PushTenant(context.PrincipalId, foreignTenant.Id))
        {
            var foreignScope = ResourceScopeRef.Tenant(foreignTenant.Id);
            var foreignProfile = await store.PutExactAsync(foreignScope, Profile(tenantProfile.Value.Name, foreignScope),
                null, true, default);
            _ = await PutAgentAsync(store, foreignScope, "foreign-consumer", foreignProfile.Value);
        }

        IReadOnlyList<ModelProfileUsage> usages;
        using (scopes.PushTenantAdministration(context))
            usages = await profiles.GetUsagesAsync(tenantProfile.Value, default);

        Assert.HasCount(3, usages);
        CollectionAssert.AreEquivalent(
            new[] { "tenant-consumer", "workspace-consumer", "sibling-workspace-consumer" },
            usages.Select(value => value.Name).ToArray());
        Assert.IsFalse(usages.Any(value => value.Name == "foreign-consumer"));
    }

    [TestMethod]
    public async Task WorkspaceProfileUsagesExcludeHomonymsInSiblingWorkspaces()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        var scopes = factory.Services.GetRequiredService<IRequestContextScopeFactory>();
        var identities = factory.Services.GetRequiredService<IIdentityStore>();
        var store = factory.Services.GetRequiredService<IResourceStore>();
        var profiles = factory.Services.GetRequiredService<ModelProfileManagementService>();
        var now = factory.Services.GetRequiredService<TimeProvider>().GetUtcNow();
        var workspaceScope = ResourceScopeRef.Workspace(context.WorkspaceId);
        var siblingWorkspace = new Workspace(Guid.NewGuid(), context.TenantId, "usage-sibling",
            "Usage sibling", WorkspaceStatus.Active, now);
        await identities.AddWorkspaceAsync(siblingWorkspace, default);

        StoredResource<ModelProfileResource> profile;
        using (scopes.Push(context))
        {
            profile = await store.PutExactAsync(workspaceScope, Profile("workspace-impact", workspaceScope),
                null, true, default);
            _ = await PutAgentAsync(store, workspaceScope, "workspace-owner-consumer", profile.Value);
        }
        using (scopes.Push(new RequestContext(context.PrincipalId, context.TenantId, siblingWorkspace.Id)))
        {
            var siblingScope = ResourceScopeRef.Workspace(siblingWorkspace.Id);
            var homonym = await store.PutExactAsync(siblingScope, Profile(profile.Value.Name, siblingScope),
                null, true, default);
            _ = await PutAgentAsync(store, siblingScope, "sibling-homonym-consumer", homonym.Value);
        }

        IReadOnlyList<ModelProfileUsage> usages;
        using (scopes.Push(context))
            usages = await profiles.GetUsagesAsync(profile.Value, default);

        Assert.HasCount(1, usages);
        Assert.AreEqual("workspace-owner-consumer", usages.Single().Name);
    }

    [TestMethod]
    public async Task UsageQueryDoesNotDiscloseScopesTheCallerCannotInspect()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        var scopes = factory.Services.GetRequiredService<IRequestContextScopeFactory>();
        var identities = factory.Services.GetRequiredService<IIdentityStore>();
        var store = factory.Services.GetRequiredService<IResourceStore>();
        var profiles = factory.Services.GetRequiredService<ModelProfileManagementService>();
        var policies = factory.Services.GetRequiredService<IResourceAccessPolicyService>();
        var now = factory.Services.GetRequiredService<TimeProvider>().GetUtcNow();
        var workspaceScope = ResourceScopeRef.Workspace(context.WorkspaceId);
        var siblingWorkspace = new Workspace(Guid.NewGuid(), context.TenantId, "hidden-sibling",
            "Hidden sibling", WorkspaceStatus.Active, now);
        await identities.AddWorkspaceAsync(siblingWorkspace, default);

        StoredResource<ModelProfileResource> profile;
        using (scopes.PushInstance(context))
        {
            profile = await store.PutExactAsync(ResourceScopeRef.Instance,
                Profile("restricted-impact", ResourceScopeRef.Instance), null, true, default);
            _ = await policies.PutAsync(new ResourceAccessPolicy(
                new(profile.Value.Uid, ResourceScopeRef.Instance, profile.Value.Namespace,
                    profile.Value.Kind, profile.Value.Generation),
                [new(ResourceScopeRef.Tenant(context.TenantId),
                    [ResourceAccessAction.Discover, ResourceAccessAction.Use], IncludeDescendants: true)]),
                null, default);
            _ = await PutAgentAsync(store, ResourceScopeRef.Instance, "hidden-instance-consumer", profile.Value);
        }
        using (scopes.Push(context))
            _ = await PutAgentAsync(store, workspaceScope, "visible-workspace-consumer", profile.Value);
        using (scopes.Push(new RequestContext(context.PrincipalId, context.TenantId, siblingWorkspace.Id)))
            _ = await PutAgentAsync(store, ResourceScopeRef.Workspace(siblingWorkspace.Id),
                "hidden-sibling-consumer", profile.Value);

        var role = new RoleDefinition(Guid.NewGuid(), "usage-reader", "Usage reader",
            [AuthorizationPermissions.WorkspacesRead, AuthorizationPermissions.ResourcesRead], false);
        var reader = new Principal(Guid.NewGuid(), PrincipalKind.Human, "Usage reader", null,
            PrincipalStatus.Active, now);
        await identities.AddRoleDefinitionAsync(role, default);
        await identities.AddPrincipalAsync(reader, default);
        await identities.AddMembershipAsync(new TenantMembership(Guid.NewGuid(), context.TenantId, reader.Id,
            MembershipStatus.Active, now), default);
        await identities.AddWorkspaceMembershipAsync(new WorkspaceMembership(Guid.NewGuid(), context.WorkspaceId,
            reader.Id, MembershipStatus.Active, now), default);
        await identities.AddRoleAssignmentAsync(new RoleAssignment(Guid.NewGuid(), context.TenantId, reader.Id,
            PrincipalType.User, role.Id, AuthorizationScopes.Workspace(context.WorkspaceId)), default);

        IReadOnlyList<ModelProfileUsage> usages;
        using (scopes.Push(new RequestContext(reader.Id, context.TenantId, context.WorkspaceId)))
            usages = await profiles.GetUsagesAsync(profile.Value, default);

        Assert.HasCount(1, usages);
        Assert.AreEqual("visible-workspace-consumer", usages.Single().Name);
        Assert.IsFalse(usages.Any(value => value.Name.Contains("hidden", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task NamespacedAgentModelResolutionUsesItsExplicitProfileNamespace()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var profileNamespace = new ResourceNamespace("shared.models");
        var agentNamespace = new ResourceNamespace("agentstration.daily-life-assistant");
        var profileRequest = Request("shared-reasoning", "qwen3:4b") with
        {
            Namespace = profileNamespace.Value,
            Properties = Request("shared-reasoning", "qwen3:4b").Properties with
            {
                Provider = new ResourceReference("ollama-local", ResourceScopeRef.Instance, ResourceNamespace.Default)
            }
        };
        using var createdProfile = await client.PostAsJsonAsync("/api/modelprofiles", profileRequest);
        Assert.AreEqual(HttpStatusCode.Created, createdProfile.StatusCode);
        var agentRequest = new AgentResourceRequest
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = AgentResourceKinds.Agent,
            Metadata = new ResourceMetadata { Name = "concierge", Namespace = agentNamespace },
            Definition = new AgentProperties
            {
                DisplayName = "Concierge",
                Instructions = "Help the user.",
                ModelProfile = new ResourceReference("shared-reasoning", @namespace: profileNamespace)
            }
        };
        using var createdAgent = await client.PutAsJsonAsync(
            $"/api/namespaces/{agentNamespace.Value}/agents/concierge",
            agentRequest);
        Assert.AreEqual(HttpStatusCode.OK, createdAgent.StatusCode);

        var resolution = await client.GetFromJsonAsync<AgentModelResponse>(
            $"/api/namespaces/{agentNamespace.Value}/agents/concierge/model");

        Assert.IsNotNull(resolution);
        Assert.AreEqual("shared-reasoning", resolution.Declared.ModelProfile.Name);
        Assert.AreEqual(profileNamespace.Value, resolution.Declared.ModelProfile.Namespace);
    }

    private static ModelProfileResource Profile(string name, ResourceScopeRef scopeRef) => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = ModelResourceKinds.ModelProfile,
        Metadata = new ResourceMetadata { Name = name },
        ScopeRef = scopeRef,
        Definition = new ModelProfileProperties
        {
            DisplayName = name,
            Provider = new ResourceReference("ollama-local", ResourceScopeRef.Instance),
            Model = new ModelSelection { Name = "test-model" }
        }
    };

    private static AgentResource Agent(
        string name,
        ResourceScopeRef scopeRef,
        ResourceReference modelProfile) => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = AgentResourceKinds.Agent,
        Metadata = new ResourceMetadata { Name = name },
        ScopeRef = scopeRef,
        Definition = new AgentProperties
        {
            DisplayName = name,
            Instructions = "Use the selected model profile.",
            ModelProfile = modelProfile,
            RuntimeProfile = new ResourceReference("maf-builtin", ResourceScopeRef.Instance)
        }
    };

    private static Task<StoredResource<AgentResource>> PutAgentAsync(
        IResourceStore store,
        ResourceScopeRef scopeRef,
        string name,
        ModelProfileResource profile,
        CancellationToken cancellationToken = default) =>
        store.PutExactAsync(scopeRef, Agent(name, scopeRef,
            new ResourceReference(profile.Name, profile.ScopeRef, profile.Namespace)),
            null, true, cancellationToken);

    [TestMethod]
    public async Task AgentUpdateRejectsUnknownModelProfileWithUnprocessableEntity()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        const string path = "/api/agents/sql-expert";
        var agent = await client.GetFromJsonAsync<AgentResource>(path);
        Assert.IsNotNull(agent);
        var request = new AgentResourceRequest
        {
            ApiVersion = agent.ApiVersion,
            Kind = agent.Kind,
            Metadata = agent.Metadata,
            Definition = agent.Definition with
            {
                ModelProfile = new ResourceReference(ModelProfileManagementService.ProfileId("missing-profile"))
            }
        };

        using var response = await client.PutAsJsonAsync(path, request);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [TestMethod]
    public async Task AgentUpdateRejectsResourceReferenceOutsideItsVisibleScopes()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        const string path = "/api/agents/sql-expert";
        var agent = await client.GetFromJsonAsync<AgentResource>(path);
        Assert.IsNotNull(agent);
        var request = new AgentResourceRequest
        {
            ApiVersion = agent.ApiVersion,
            Kind = agent.Kind,
            Metadata = agent.Metadata,
            Definition = agent.Definition with
            {
                ModelProfile = new ResourceReference(agent.Definition.ModelProfile.Name, ResourceScopeRef.Workspace(Guid.NewGuid()))
            }
        };

        using var response = await client.PutAsJsonAsync(path, request);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [TestMethod]
    public async Task AgentModelExpansionUsesTheNewProfileImmediatelyAfterUpdate()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        const string agentPath = "/api/agents/sql-expert";
        const string profileName = "deep-reasoning";
        var profileRequest = Request(profileName, "qwen3.6:latest") with
        {
            Properties = Request(profileName, "qwen3.6:latest").Properties with { DisplayName = "Deep reasoning" }
        };
        using var createdProfile = await client.PostAsJsonAsync("/api/modelprofiles", profileRequest);
        Assert.AreEqual(HttpStatusCode.Created, createdProfile.StatusCode);

        using var currentResponse = await client.GetAsync(agentPath);
        Assert.AreEqual(HttpStatusCode.OK, currentResponse.StatusCode);
        Assert.IsNotNull(currentResponse.Headers.ETag);
        var current = await currentResponse.Content.ReadFromJsonAsync<AgentResource>();
        Assert.IsNotNull(current);
        var updateRequest = new AgentResourceRequest
        {
            ApiVersion = current.ApiVersion,
            Kind = current.Kind,
            Metadata = current.Metadata,
            Definition = current.Definition with
            {
                ModelProfile = new ResourceReference(ModelProfileManagementService.ProfileId(profileName))
            }
        };
        using var update = new HttpRequestMessage(HttpMethod.Put, agentPath) { Content = JsonContent.Create(updateRequest) };
        update.Headers.IfMatch.Add(currentResponse.Headers.ETag);
        using var updatedResponse = await client.SendAsync(update);
        Assert.AreEqual(HttpStatusCode.OK, updatedResponse.StatusCode);

        var expanded = await client.GetFromJsonAsync<AgentModelResponse>("/api/agents/sql-expert/model");
        Assert.IsNotNull(expanded);
        Assert.AreEqual(profileName, expanded.Declared.ModelProfile.Name);
        Assert.AreEqual("Deep reasoning", expanded.Declared.ModelProfile.DisplayName);
        Assert.AreEqual("qwen3.6:latest", expanded.Resolved.Model.Name);
    }

    [TestMethod]
    public async Task ProfileCreationRejectsUnknownProviderWithProblemDetails()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var request = Request("invalid-provider-profile", "qwen3:1.7b") with
        {
            Properties = Request("ignored", "qwen3:1.7b").Properties with
            {
                Provider = new ResourceReference(ModelProviderManagementService.ModelProviderId("missing-provider"), ResourceScopeRef.Instance)
            }
        };

        using var response = await client.PostAsJsonAsync("/api/modelprofiles", request);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [TestMethod]
    public async Task CanonicalProfileCategoriesFlowIntoRuntimeResolution()
    {
        await using var factory = Factory();
        var requestContext = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(requestContext);
        var service = factory.Services.GetRequiredService<ModelProfileManagementService>();
        var profile = new ModelProfileResource
        {
            Metadata = new ResourceMetadata { Name = "canonical-profile" },
            Kind = ModelResourceKinds.ModelProfile,
            ApiVersion = ResourceApiVersions.CoreV1,
            Definition = new ModelProfileProperties
            {
                DisplayName = "Canonical profile",
                Provider = new ResourceReference(ModelProviderManagementService.ModelProviderId("ollama-local"), ResourceScopeRef.Instance),
                Model = new ModelSelection { Name = "qwen3:8b" },
                Generation = new ModelGenerationOptions { Temperature = 0.2, TopP = 0.8, TopK = 20, MaxOutputTokens = 4096 },
                Reasoning = new ModelReasoningOptions { Mode = ReasoningMode.Enabled, Effort = ReasoningEffort.Medium },
                Output = new ModelOutputOptions { Format = ModelOutputFormat.JsonObject },
                ProviderOptions = new Dictionary<string, VersionedExtensionOptions>
                {
                    ["ollama"] = new()
                    {
                        OptionSet = "io.agentstration.ollama/model-profile",
                        Version = "1.0.0",
                        SchemaDigest = $"sha256:{new string('0', 64)}",
                        Values = JsonSerializer.SerializeToElement(new { think = "medium", contextSize = 8192 })
                    }
                }
            }
        };

        var stored = await service.CreateAsync(profile, default);
        var resolved = await ((IModelProfileStore)service).GetRequiredAsync(stored.Value.Metadata.Name);
        var deployment = await ((IModelDeploymentStore)service).GetRequiredAsync(resolved.DeploymentName);

        Assert.AreEqual(0.2, resolved.Generation.Temperature);
        Assert.AreEqual(0.8, resolved.Generation.TopP);
        Assert.AreEqual(ReasoningEffort.Medium, resolved.Reasoning.Effort);
        Assert.IsTrue(deployment.ProviderOptions.ContainsKey("ollama"));
    }

    [TestMethod]
    public async Task ProfileResolutionExposesEffectiveCapabilitiesAndConfigurationIssues()
    {
        await using var factory = DiagnosticFactory();
        using var client = factory.CreateClient();
        SelectConsoleContext(client, ResourceScopeRef.Instance);
        var providerProperties = new ModelProviderProperties
        {
            DisplayName = "Diagnostic provider",
            Extension = new ResourceReference("ollama-extension"),
            ContributionId = "diagnostic"
        };
        using var providerResponse = await client.PostAsJsonAsync("/api/modelproviders", new CreateModelProviderRequest(
            "diagnostic-local",
            providerProperties));
        Assert.AreEqual(HttpStatusCode.Created, providerResponse.StatusCode);
        using var refreshResponse = await client.PostAsync("/api/modelproviders/diagnostic-local/models/refresh", null);
        Assert.AreEqual(HttpStatusCode.OK, refreshResponse.StatusCode);
        using var profileResponse = await client.PostAsJsonAsync("/api/modelprofiles", new CreateModelProfileRequest(
            "incompatible-profile",
            new ModelProfileProperties
            {
                DisplayName = "Incompatible profile",
                Provider = new ResourceReference("diagnostic-local", ResourceScopeRef.Instance),
                Model = new ModelSelection { Name = "local-model" },
                Reasoning = new ModelReasoningOptions { Mode = ReasoningMode.Enabled },
                Output = new ModelOutputOptions { Format = ModelOutputFormat.JsonObject }
            }, ScopeRef: ResourceScopeRef.Instance));
        Assert.AreEqual(HttpStatusCode.Created, profileResponse.StatusCode);

        var resolution = await client.GetFromJsonAsync<ModelProfileResolutionResponse>("/api/modelprofiles/incompatible-profile/resolution");

        Assert.IsNotNull(resolution);
        Assert.AreEqual("incompatible", resolution.Status);
        var capabilities = resolution.Capabilities ?? [];
        var incompatibilities = resolution.Incompatibilities ?? [];
        Assert.HasCount(4, capabilities);
        var tools = capabilities.Single(capability => capability.Name == "Tools");
        Assert.AreEqual("native", tools.ProviderSupport);
        Assert.AreEqual("unsupported", tools.ModelSupport);
        Assert.AreEqual("unsupported", tools.EffectiveSupport);
        Assert.AreEqual("native", capabilities.Single(capability => capability.Name == "Structured output").EffectiveSupport);
        Assert.IsTrue(incompatibilities.Any(issue => issue.Capability == "reasoning"));
        Assert.IsFalse(incompatibilities.Any(issue => issue.Capability == "structuredOutput"));

        var providerEtag = providerResponse.Headers.ETag?.ToString()
            ?? throw new InvalidOperationException("The provider ETag was missing.");
        using var overrideRequest = new HttpRequestMessage(HttpMethod.Put, "/api/modelproviders/diagnostic-local")
        {
            Content = JsonContent.Create(new PutModelProviderRequest(providerProperties with
            {
                SpecificationOverrides = new Dictionary<string, ModelSpecificationOverride>
                {
                    ["local-model"] = new()
                    {
                        Features = new ModelFeatureOverrides
                        {
                            Tools = new() { Support = ModelFeatureSupport.Native }
                        }
                    }
                }
            }))
        };
        overrideRequest.Headers.IfMatch.ParseAdd(providerEtag);
        using var overrideResponse = await client.SendAsync(overrideRequest);
        Assert.AreEqual(HttpStatusCode.OK, overrideResponse.StatusCode);

        resolution = await client.GetFromJsonAsync<ModelProfileResolutionResponse>(
            "/api/modelprofiles/incompatible-profile/resolution");
        tools = resolution!.Capabilities!.Single(capability => capability.Name == "Tools");
        Assert.AreEqual("native", tools.ModelSupport);
        Assert.AreEqual("native", tools.EffectiveSupport);
        Assert.IsNull(resolution.Model.ObservedSpecification?.Features.Tools);
        Assert.AreEqual(ModelFeatureSupport.Native, resolution.Model.SpecificationOverride?.Features.Tools?.Support);
    }

}

