using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.DataSources;
using Agentstration.DataSources.Contracts;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class DataSourceApiTests : ModelManagementApiTestBase
{
    [TestMethod]
    public async Task DataSourceProfilesSupportAllScopesAndDescendantVisibility()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var targets = (await client.GetFromJsonAsync<ResourceScopeTargetResponse[]>(
            $"/api/resource-scopes/targets?kind={DataSourceResourceKinds.DataSourceProfile}"))!;
        CollectionAssert.AreEquivalent(
            new[] { ResourceScopeKind.Instance, ResourceScopeKind.Tenant, ResourceScopeKind.Workspace },
            targets.Select(target => target.Kind).ToArray());
        var instance = targets.Single(target => target.Kind == ResourceScopeKind.Instance).ScopeRef;
        var workspace = targets.Single(target => target.Kind == ResourceScopeKind.Workspace).ScopeRef;
        var context = await GetBootstrapContextAsync(factory);

        using (factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem())
        {
            await CreatePublishedAcquisitionFlowAsync(factory.Services, context,
                "data-source-http-acquisition", instance);
            var profiles = factory.Services.GetRequiredService<DataSourceProfileService>();
            _ = await profiles.CreateAsync(ProfileResource("shared-http", instance, Profile()), default);
            _ = await profiles.PublishAsync(ResourceNamespace.Default, "shared-http", instance,
                new("1.0.0"), default);
            var profile = await profiles.GetAsync(ResourceNamespace.Default, "shared-http", instance, default)
                ?? throw new AssertFailedException("The shared profile must exist.");
            var denied = await Assert.ThrowsAsync<DataSourceValidationException>(() =>
                factory.Services.GetRequiredService<DataSourceManagementService>().CreateAsync(
                    SourceResource("denied", workspace, new("shared-http", instance)), default));
            Assert.AreEqual("data_source_not_ready", denied.Code);
            StringAssert.Contains(denied.Message, "policy_missing");
            _ = await factory.Services.GetRequiredService<IResourceAccessPolicyService>().PutAsync(
                new(new(profile.Value.Uid, instance, profile.Value.Namespace,
                        DataSourceResourceKinds.DataSourceProfile, profile.Value.Generation),
                    [new(workspace, [ResourceAccessAction.Use])]), null, default);
        }

        using var createdSource = await client.PostAsJsonAsync("/api/datasources",
            new CreateDataSourceRequest("docs", new()
            {
                DisplayName = "Documentation",
                Profile = new("shared-http", instance),
                Configuration = JsonSerializer.SerializeToElement(new { url = "https://docs.example.test" })
            }, ScopeRef: workspace));
        Assert.AreEqual(HttpStatusCode.Created, createdSource.StatusCode);
        var source = await createdSource.Content.ReadFromJsonAsync<DataSourceResource>();
        Assert.AreEqual(workspace, source?.ScopeRef);

        var readiness = await client.GetFromJsonAsync<DataSourceReadiness>(
            $"/api/datasources/docs/readiness?scopeRef={Uri.EscapeDataString(workspace.Value)}");
        Assert.IsNotNull(readiness);
        Assert.IsTrue(readiness.Ready);
        Assert.AreEqual(instance, readiness.Profile?.ScopeRef);
        Assert.AreEqual("1.0.0", readiness.Profile?.Version);
    }

    [TestMethod]
    public async Task ProfilePublicationPinsDependenciesFromItsOwnerScopeInsteadOfTheExecutionWorkspace()
    {
        await using var factory = Factory();
        using var system = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
        var context = await GetBootstrapContextAsync(factory);
        var tenant = ResourceScopeRef.Tenant(context.TenantId);
        var workspace = ResourceScopeRef.Workspace(context.WorkspaceId);
        await CreatePublishedAcquisitionFlowAsync(factory.Services, context, "tenant-acquire", tenant);
        var store = factory.Services.GetRequiredService<IResourceStore>();
        var provider = await PutProviderAsync(store, ResourceScopeRef.Instance, "shared-provider");
        var instanceTool = await PutToolAsync(store, ResourceScopeRef.Instance, "shared-fetch", provider.Value);
        _ = await PutProviderAsync(store, workspace, "workspace-provider");
        var workspaceProvider = (await store.GetExactAsync<ToolProviderResource>(ScopedResourceAddress.Create(
            workspace, ResourceNamespace.Default, ToolResourceKinds.ToolProvider, "workspace-provider"), default))!;
        _ = await PutToolAsync(store, workspace, "shared-fetch", workspaceProvider.Value);

        var policies = factory.Services.GetRequiredService<IResourceAccessPolicyService>();
        _ = await policies.PutAsync(new(new(instanceTool.Value.Uid, ResourceScopeRef.Instance,
                instanceTool.Value.Namespace, ToolResourceKinds.Tool, instanceTool.Value.Generation),
            [new(tenant, [ResourceAccessAction.Use])]), null, default);

        var profiles = factory.Services.GetRequiredService<DataSourceProfileService>();
        var definition = Profile() with
        {
            AcquisitionFlow = new()
            {
                Name = "tenant-acquire",
                ScopeRef = tenant,
                UseActiveVersion = true
            },
            ToolBindings = [new() { Name = "fetch", Capability = "fetch", Tool = new("shared-fetch") }]
        };
        _ = await profiles.CreateAsync(ProfileResource("tenant-profile", tenant, definition), default);
        var revision = await profiles.PublishAsync(ResourceNamespace.Default, "tenant-profile", tenant,
            new("1.0.0"), default);

        Assert.AreEqual(tenant, revision.Value.AcquisitionFlow.ScopeRef);
        Assert.IsFalse(string.IsNullOrWhiteSpace(revision.Value.AcquisitionFlow.DefinitionHash));
        Assert.IsFalse(string.IsNullOrWhiteSpace(revision.Value.DependencyHash));
        var tool = revision.Value.ToolBindings.Single();
        Assert.AreEqual(ResourceScopeRef.Instance, tool.ToolScopeRef);
        Assert.AreEqual(instanceTool.Value.Uid, tool.ToolUid);
    }

    [TestMethod]
    public async Task ProfilePublicationFailsClosedForAnAncestorFlowWithoutExactAccessAuthorization()
    {
        await using var factory = Factory();
        using var system = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
        var context = await GetBootstrapContextAsync(factory);
        var tenant = ResourceScopeRef.Tenant(context.TenantId);
        await CreatePublishedAcquisitionFlowAsync(factory.Services, context, "instance-acquire",
            ResourceScopeRef.Instance);
        var profiles = factory.Services.GetRequiredService<DataSourceProfileService>();
        _ = await profiles.CreateAsync(ProfileResource("tenant-profile", tenant, Profile() with
        {
            AcquisitionFlow = new()
            {
                Name = "instance-acquire",
                ScopeRef = ResourceScopeRef.Instance,
                UseActiveVersion = true
            }
        }), default);

        var denied = await Assert.ThrowsAsync<DataSourceAcquisitionException>(() => profiles.PublishAsync(
            ResourceNamespace.Default, "tenant-profile", tenant, new("1.0.0"), default));
        Assert.AreEqual("data_source_dependency_access_denied", denied.Code);
    }

    [TestMethod]
    public async Task AcquisitionRejectsAStalePinnedToolGeneration()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        var workspace = ResourceScopeRef.Workspace(context.WorkspaceId);
        using (factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem())
        {
            await CreatePublishedAcquisitionFlowAsync(factory.Services, context, "stale-acquire");
            var store = factory.Services.GetRequiredService<IResourceStore>();
            var provider = await PutProviderAsync(store, workspace, "stale-provider");
            var tool = await PutToolAsync(store, workspace, "stale-tool", provider.Value);
            var profiles = factory.Services.GetRequiredService<DataSourceProfileService>();
            var definition = Profile() with
            {
                AcquisitionFlow = new() { Name = "stale-acquire", ScopeRef = workspace, UseActiveVersion = true },
                ToolBindings = [new() { Name = "fetch", Capability = "fetch", Tool = new("stale-tool", workspace) }]
            };
            _ = await profiles.CreateAsync(ProfileResource("stale-profile", workspace, definition), default);
            _ = await profiles.PublishAsync(ResourceNamespace.Default, "stale-profile", workspace,
                new("1.0.0"), default);
            _ = await factory.Services.GetRequiredService<DataSourceManagementService>().CreateAsync(
                SourceResource("stale-source", workspace, new("stale-profile", workspace)), default);
            _ = await store.PutExactAsync(workspace, tool.Value with
            {
                Generation = tool.Value.Generation + 1,
                Definition = tool.Value.Definition with { DisplayName = "Changed after publication" }
            }, tool.ETag, false, default);
        }

        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync(
            $"/api/datasources/stale-source/acquisitions?scopeRef={Uri.EscapeDataString(workspace.Value)}",
            new StartDataSourceAcquisitionRequest { DataSourceScopeRef = workspace });
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "data_source_acquisition_tool_stale");
    }

    [TestMethod]
    public async Task ProfileRevisionsAreImmutableAndActivationOnlyAffectsFutureResolution()
    {
        await using var factory = Factory();
        using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
        var profiles = factory.Services.GetRequiredService<DataSourceProfileService>();
        var sources = factory.Services.GetRequiredService<DataSourceManagementService>();
        var context = await GetBootstrapContextAsync(factory);
        var workspace = ResourceScopeRef.Workspace(context.WorkspaceId);
        await CreatePublishedAcquisitionFlowAsync(factory.Services, context, "data-source-http-acquisition");
        var profile = await profiles.CreateAsync(ProfileResource("versioned", workspace, Profile()), default);
        _ = await profiles.PublishAsync(ResourceNamespace.Default, "versioned", workspace, new("1.0.0"), default);
        var source = await sources.CreateAsync(SourceResource("source-a", workspace, new("versioned", workspace)), default);
        var first = await profiles.ResolveActiveAsync(workspace, source.Value.Namespace, source.Value.Definition.Profile, default);
        Assert.AreEqual("1.0.0", first.Version);

        var draft = profile.Value.Definition with { Description = "Second revision", Version = "2.0.0" };
        var current = await profiles.GetAsync(ResourceNamespace.Default, "versioned", workspace, default);
        _ = await profiles.PutAsync(ResourceNamespace.Default, "versioned", workspace, draft, current!.ETag, default);
        _ = await profiles.PublishAsync(ResourceNamespace.Default, "versioned", workspace,
            new("2.0.0", Activate: false), default);
        var beforeActivation = await profiles.ResolveActiveAsync(workspace, ResourceNamespace.Default,
            source.Value.Definition.Profile, default);
        Assert.AreEqual("1.0.0", beforeActivation.Version);
        var profileBeforeActivation = await profiles.GetAsync(ResourceNamespace.Default, "versioned", workspace, default);
        _ = await profiles.ActivateAsync(ResourceNamespace.Default, "versioned", workspace,
            new("2.0.0"), profileBeforeActivation!.ETag, default);
        var afterActivation = await profiles.ResolveActiveAsync(workspace, ResourceNamespace.Default,
            source.Value.Definition.Profile, default);
        Assert.AreEqual("2.0.0", afterActivation.Version);

        var profileBeforeRollback = await profiles.GetAsync(ResourceNamespace.Default, "versioned", workspace, default);
        _ = await profiles.ActivateAsync(ResourceNamespace.Default, "versioned", workspace,
            new("1.0.0"), profileBeforeRollback!.ETag, default);
        var afterRollback = await profiles.ResolveActiveAsync(workspace, ResourceNamespace.Default,
            source.Value.Definition.Profile, default);
        Assert.AreEqual("1.0.0", afterRollback.Version);

        var stale = await Assert.ThrowsAsync<DataSourceValidationException>(() => profiles.ActivateAsync(
            ResourceNamespace.Default, "versioned", workspace, new("9.9.9"), null, default));
        Assert.AreEqual("data_source_profile_revision_not_found", stale.Code);

        var conflict = await Assert.ThrowsAsync<DataSourceValidationException>(() => profiles.PublishAsync(
            ResourceNamespace.Default, "versioned", workspace, new("1.0.0"), default));
        Assert.AreEqual("data_source_profile_revision_conflict", conflict.Code);
        Assert.AreEqual(first.DefinitionHash,
            (await profiles.GetRevisionAsync(ResourceNamespace.Default, "versioned", "1.0.0", workspace, default))!
            .Value.DefinitionHash);
    }

    [TestMethod]
    public async Task ConcurrentPublicationOfTheSameProfileRevisionIsIdempotent()
    {
        await using var factory = Factory();
        using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
        var context = await GetBootstrapContextAsync(factory);
        var workspace = ResourceScopeRef.Workspace(context.WorkspaceId);
        var profiles = factory.Services.GetRequiredService<DataSourceProfileService>();
        await CreatePublishedAcquisitionFlowAsync(factory.Services, context, "data-source-http-acquisition");
        _ = await profiles.CreateAsync(ProfileResource("concurrent", workspace, Profile()), default);

        var request = new PublishDataSourceProfileRequest("1.0.0", Activate: false);
        var publications = await Task.WhenAll(
            profiles.PublishAsync(ResourceNamespace.Default, "concurrent", workspace, request, default),
            profiles.PublishAsync(ResourceNamespace.Default, "concurrent", workspace, request, default));

        Assert.AreEqual(publications[0].Value.Uid, publications[1].Value.Uid);
        Assert.AreEqual(publications[0].Value.DefinitionHash, publications[1].Value.DefinitionHash);
        Assert.HasCount(1, await profiles.ListRevisionsAsync(
            ResourceNamespace.Default, "concurrent", workspace, default));
    }

    [TestMethod]
    public async Task DataSourceConfigurationMustMatchThePublishedProfileSchema()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var targets = (await client.GetFromJsonAsync<ResourceScopeTargetResponse[]>(
            $"/api/resource-scopes/targets?kind={DataSourceResourceKinds.DataSource}"))!;
        var workspace = targets.Single(target => target.Kind == ResourceScopeKind.Workspace).ScopeRef;
        var context = await GetBootstrapContextAsync(factory);
        using (factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem())
            await CreatePublishedAcquisitionFlowAsync(factory.Services, context, "data-source-http-acquisition");
        using var profile = await client.PostAsJsonAsync("/api/datasourceprofiles",
            new CreateDataSourceProfileRequest("validated", Profile(), ScopeRef: workspace));
        Assert.AreEqual(HttpStatusCode.Created, profile.StatusCode);
        using var published = await client.PostAsJsonAsync(
            $"/api/datasourceprofiles/validated/revisions?scopeRef={Uri.EscapeDataString(workspace.Value)}",
            new PublishDataSourceProfileRequest("1.0.0"));
        Assert.AreEqual(HttpStatusCode.Created, published.StatusCode);

        using var invalid = await client.PostAsJsonAsync("/api/datasources", new CreateDataSourceRequest(
            "invalid", new()
            {
                DisplayName = "Invalid",
                Profile = new("validated", workspace),
                Configuration = JsonSerializer.SerializeToElement(new { })
            }, ScopeRef: workspace));
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
    }

    [TestMethod]
    public async Task DataSourceProfileCannotBeDeletedWhileReferenced()
    {
        await using var factory = Factory();
        using var system = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
        var context = await GetBootstrapContextAsync(factory);
        var workspace = ResourceScopeRef.Workspace(context.WorkspaceId);
        await CreatePublishedAcquisitionFlowAsync(factory.Services, context, "data-source-http-acquisition");
        var profiles = factory.Services.GetRequiredService<DataSourceProfileService>();
        var sources = factory.Services.GetRequiredService<DataSourceManagementService>();
        _ = await profiles.CreateAsync(ProfileResource("used", workspace, Profile()), default);
        _ = await profiles.PublishAsync(ResourceNamespace.Default, "used", workspace, new("1.0.0"), default);
        _ = await sources.CreateAsync(SourceResource("consumer", workspace, new("used", workspace)), default);
        var error = await Assert.ThrowsAsync<DataSourceValidationException>(() => profiles.DeleteAsync(
            ResourceNamespace.Default, "used", workspace, null, default));
        Assert.AreEqual("data_source_profile_in_use", error.Code);
    }

    [TestMethod]
    public async Task DataSourceAcquisitionRunsWithoutAKnowledgeSourceAndPinsItsComposition()
    {
        await using var factory = Factory();
        await using var worker = await AwpTestWorker.StartAsync(factory);
        using var system = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
        var context = await GetBootstrapContextAsync(factory);
        var workspace = ResourceScopeRef.Workspace(context.WorkspaceId);
        await CreatePublishedAcquisitionFlowAsync(factory.Services, context, "generic-acquire");
        var profiles = factory.Services.GetRequiredService<DataSourceProfileService>();
        var sources = factory.Services.GetRequiredService<DataSourceManagementService>();
        var definition = Profile() with { AcquisitionFlow = new() { Name = "generic-acquire", UseActiveVersion = true } };
        _ = await profiles.CreateAsync(ProfileResource("generic", workspace, definition), default);
        _ = await profiles.PublishAsync(ResourceNamespace.Default, "generic", workspace, new("1.0.0"), default);
        var source = await sources.CreateAsync(SourceResource("origin", workspace, new("generic", workspace)), default);

        using var client = factory.CreateClient();
        var readiness = await client.GetFromJsonAsync<DataSourceReadiness>(
            $"/api/datasources/origin/readiness?scopeRef={Uri.EscapeDataString(workspace.Value)}");
        Assert.AreEqual("generic-acquire", readiness?.Acquisition?.Name);
        Assert.AreEqual("1.0.0", readiness?.Acquisition?.Version);
        Assert.AreEqual("string", readiness?.Acquisition?.InputSchema?.GetProperty("properties")
            .GetProperty("parameters").GetProperty("properties").GetProperty("query").GetProperty("type").GetString());

        using var startedResponse = await client.PostAsJsonAsync(
            $"/api/datasources/origin/acquisitions?scopeRef={Uri.EscapeDataString(workspace.Value)}",
            new StartDataSourceAcquisitionRequest { DataSourceScopeRef = workspace });
        Assert.AreEqual(HttpStatusCode.Accepted, startedResponse.StatusCode);
        var started = await startedResponse.Content.ReadFromJsonAsync<DataSourceAcquisitionResource>()
            ?? throw new AssertFailedException("The acquisition response must contain a resource.");
        Assert.AreEqual(source.Value.Uid, started.DataSourceUid);
        Assert.AreEqual(workspace, started.DataSourceScopeRef);
        Assert.AreEqual("1.0.0", started.Composition.Profile.Version);
        Assert.AreEqual("1.0.0", started.Composition.Flow.Version);
        Assert.AreEqual(DataSourceFlowContracts.Acquisition, started.Composition.Flow.Contract);

        var current = started;
        for (var attempt = 0; attempt < 50
            && current.State is DataSourceAcquisitionState.Pending
                or DataSourceAcquisitionState.Running
                or DataSourceAcquisitionState.WaitingForChild;
            attempt++)
        {
            await Task.Delay(50);
            current = await client.GetFromJsonAsync<DataSourceAcquisitionResource>(
                $"/api/datasourceacquisitions/{Uri.EscapeDataString(started.Name)}")
                ?? throw new AssertFailedException("The acquisition status response must contain a resource.");
        }
        Assert.AreEqual(DataSourceAcquisitionState.Succeeded, current.State);
        Assert.AreEqual(0, current.Manifest?.Artifacts.Count);
    }

    [TestMethod]
    public async Task LateralScopesAndBuiltInProfileMutationAreRejected()
    {
        await using var factory = Factory().WithWebHostBuilder(builder =>
            builder.UseSetting("Agentstration:Authentication:DevelopmentGrantPlatformAdministrator", "false"));
        var context = await GetBootstrapContextAsync(factory);
        var workspace = ResourceScopeRef.Workspace(context.WorkspaceId);
        var foreignWorkspaceId = Guid.NewGuid();
        var foreignWorkspace = ResourceScopeRef.Workspace(foreignWorkspaceId);
        var identityStore = factory.Services.GetRequiredService<IIdentityStore>();
        var otherWorkspace = new Workspace(foreignWorkspaceId, context.TenantId,
            $"data-source-{foreignWorkspaceId:N}", "Data Source isolation", WorkspaceStatus.Initializing,
            DateTimeOffset.UtcNow);
        await identityStore.AddWorkspaceAsync(otherWorkspace, default);
        await factory.Services.GetRequiredService<IWorkspaceProvisioner>().ProvisionAsync(otherWorkspace, default);
        using var client = factory.CreateClient();

        using var lateral = await client.PostAsJsonAsync("/api/datasourceprofiles",
            new CreateDataSourceProfileRequest("foreign", Profile(), ScopeRef: foreignWorkspace));
        Assert.AreEqual(HttpStatusCode.Forbidden, lateral.StatusCode);

        using (factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem())
        {
            var profiles = factory.Services.GetRequiredService<DataSourceProfileService>();
            var builtIn = ProfileResource("core-http", workspace, Profile()) with
            {
                Metadata = new ResourceMetadata
                {
                    Name = "core-http",
                    Annotations = new Dictionary<string, string>
                    {
                        [ResourceProvenanceAnnotations.BuiltIn] = "true"
                    }
                }
            };
            _ = await profiles.CreateAsync(builtIn, default);
        }

        using var mutation = await client.PutAsJsonAsync(
            $"/api/datasourceprofiles/core-http?scopeRef={Uri.EscapeDataString(workspace.Value)}",
            new PutDataSourceProfileRequest(Profile() with { Description = "Changed" }));
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, mutation.StatusCode);
        StringAssert.Contains(await mutation.Content.ReadAsStringAsync(), "data_source_profile_builtin_protected");
    }

    [TestMethod]
    public async Task ProfilePublicationRejectsMissingToolsAndUnavailableProviders()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        var workspace = ResourceScopeRef.Workspace(context.WorkspaceId);
        using (factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem())
        {
            await CreatePublishedAcquisitionFlowAsync(factory.Services, context, "bound-acquire");
            var profiles = factory.Services.GetRequiredService<DataSourceProfileService>();
            var sources = factory.Services.GetRequiredService<DataSourceManagementService>();
            var missingTool = Profile() with
            {
                AcquisitionFlow = new() { Name = "bound-acquire", UseActiveVersion = true },
                ToolBindings = [new() { Name = "fetch", Capability = "fetch", Tool = new("missing-tool") }]
            };
            _ = await profiles.CreateAsync(ProfileResource("missing-tool-profile", workspace, missingTool), default);
            var missing = await Assert.ThrowsAsync<DataSourceAcquisitionException>(() => profiles.PublishAsync(
                ResourceNamespace.Default, "missing-tool-profile", workspace, new("1.0.0"), default));
            Assert.AreEqual("data_source_acquisition_tool_missing", missing.Code);

            var store = factory.Services.GetRequiredService<IResourceStore>();
            _ = await store.PutExactAsync(workspace, new ToolProviderResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = ToolResourceKinds.ToolProvider,
                Metadata = new ResourceMetadata { Name = "disabled-provider" },
                ScopeRef = workspace,
                Generation = 1,
                Definition = new ToolProviderProperties
                {
                    DisplayName = "Disabled provider",
                    ProviderType = ToolProviderType.Mcp,
                    Enabled = false,
                    Mcp = new McpToolProviderConfiguration { Internal = true }
                }
            }, null, true, default);
            var now = DateTimeOffset.UtcNow;
            _ = await store.PutExactAsync(workspace, new ToolResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = ToolResourceKinds.Tool,
                Metadata = new ResourceMetadata { Name = "disabled-tool" },
                ScopeRef = workspace,
                Generation = 1,
                Definition = new ToolResourceProperties
                {
                    DisplayName = "Disabled tool",
                    Provider = new("disabled-provider"),
                    ExternalId = "disabled.fetch",
                    Discovery = new() { Available = true, FirstSeenAt = now, LastSeenAt = now }
                }
            }, null, true, default);
            var unavailable = missingTool with
            {
                ToolBindings = [new() { Name = "fetch", Capability = "fetch", Tool = new("disabled-tool") }]
            };
            _ = await profiles.CreateAsync(ProfileResource("unavailable-profile", workspace, unavailable), default);
            var disabled = await Assert.ThrowsAsync<DataSourceAcquisitionException>(() => profiles.PublishAsync(
                ResourceNamespace.Default, "unavailable-profile", workspace, new("1.0.0"), default));
            Assert.AreEqual("data_source_acquisition_provider_unavailable", disabled.Code);
        }
    }

    private static DataSourceProfileProperties Profile() => new()
    {
        DisplayName = "HTTP",
        Version = "1.0.0",
        ConfigurationSchema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { url = new { type = "string" } },
            required = new[] { "url" },
            additionalProperties = false
        }),
        AcquisitionFlow = new() { Name = "data-source-http-acquisition", UseActiveVersion = true }
    };

    private static DataSourceProfileResource ProfileResource(
        string name,
        ResourceScopeRef scope,
        DataSourceProfileProperties definition) => new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = DataSourceResourceKinds.DataSourceProfile,
            Metadata = new ResourceMetadata { Name = name },
            ScopeRef = scope,
            Definition = definition
        };

    private static DataSourceResource SourceResource(string name, ResourceScopeRef scope, ResourceReference profile) => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = DataSourceResourceKinds.DataSource,
        Metadata = new ResourceMetadata { Name = name },
        ScopeRef = scope,
        Definition = new()
        {
            DisplayName = name,
            Profile = profile,
            Configuration = JsonSerializer.SerializeToElement(new { url = "https://docs.example.test" })
        }
    };

    private static Task<StoredResource<ToolProviderResource>> PutProviderAsync(
        IResourceStore store,
        ResourceScopeRef scope,
        string name) => store.PutExactAsync(scope, new ToolProviderResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ToolResourceKinds.ToolProvider,
            Metadata = new ResourceMetadata { Name = name },
            ScopeRef = scope,
            Generation = 1,
            Definition = new ToolProviderProperties
            {
                DisplayName = name,
                ProviderType = ToolProviderType.Mcp,
                Enabled = true,
                Mcp = new McpToolProviderConfiguration { Internal = true }
            }
        }, null, true, default);

    private static Task<StoredResource<ToolResource>> PutToolAsync(
        IResourceStore store,
        ResourceScopeRef scope,
        string name,
        ToolProviderResource provider) => store.PutExactAsync(scope, new ToolResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ToolResourceKinds.Tool,
            Metadata = new ResourceMetadata { Name = name },
            ScopeRef = scope,
            Generation = 1,
            Definition = new ToolResourceProperties
            {
                DisplayName = name,
                Provider = new(provider.Name, provider.ScopeRef, provider.Namespace),
                ExternalId = name,
                Discovery = new()
                {
                    Available = true,
                    FirstSeenAt = DateTimeOffset.UtcNow,
                    LastSeenAt = DateTimeOffset.UtcNow
                }
            }
        }, null, true, default);

    private static async Task CreatePublishedAcquisitionFlowAsync(
        IServiceProvider services,
        RequestContext context,
        string name,
        ResourceScopeRef? ownerScope = null)
    {
        var inputSchema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                dataSourceId = new { type = "string" },
                dataSourceUid = new { type = "string" },
                dataSourceGeneration = new { type = "integer" },
                profile = new { type = "object" },
                sourceConfiguration = new { type = "object" },
                parameters = new
                {
                    type = "object",
                    properties = new { query = new { type = "string" } }
                },
                caller = new { type = "object" },
                correlationId = new { type = "string" },
                acquisitionId = new { type = "string" }
            },
            required = new[] { "dataSourceId", "dataSourceUid", "dataSourceGeneration", "profile",
                "sourceConfiguration", "parameters", "caller", "correlationId", "acquisitionId" }
        });
        var outputSchema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { artifacts = new { type = "array" } },
            required = new[] { "artifacts" }
        });
        var output = JsonSerializer.SerializeToElement(new { artifacts = Array.Empty<object>() });
        var flows = services.GetRequiredService<FlowService>();
        var scope = ownerScope ?? ResourceScopeRef.Workspace(context.WorkspaceId);
        await flows.CreateExactAsync(scope, new CreateFlowCommand(
            name, null, "1.0.0", true,
            GraphOnlyDefinition(),
            new Dictionary<string, string> { [FlowMetadataKeys.Contract] = DataSourceFlowContracts.Acquisition },
            new FlowGraphDefinition
            {
                EntryStep = "input",
                InputSchema = inputSchema,
                OutputSchema = outputSchema,
                Steps =
                [
                    new InputFlowStepDefinition { Name = "input", Schema = inputSchema },
                    new OutputFlowStepDefinition { Name = "output", OutputMapping = output }
                ],
                Transitions = [new("input-output", "input", "completed", "output")]
            }), ResourceNamespace.Default, default);
        await flows.PublishVersionExactAsync(scope, new(name), "1.0.0", true, default);
    }

    private static WorkflowFlowDefinition GraphOnlyDefinition() => new(
        "input",
        [new FlowNode("input", FlowNodeKind.Input)],
        []);
}
