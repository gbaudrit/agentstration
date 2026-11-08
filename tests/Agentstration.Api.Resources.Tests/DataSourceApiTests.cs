using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Artifacts;
using Agentstration.Artifacts.Contracts;
using Agentstration.DataSources;
using Agentstration.DataSources.Contracts;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure.Flows;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Tools;
using Agentstration.Tools.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class DataSourceApiTests : ResourceManagementApiTestBase
{
    [TestMethod]
    [DataRow(ResourceScopeKind.Workspace)]
    [DataRow(ResourceScopeKind.Instance)]
    [DataRow(ResourceScopeKind.Tenant)]
    public async Task AcquisitionHistorySynchronizesNonemptyArtifactManifestsInTheExecutionWorkspace(ResourceScopeKind kind)
    {
        await using var factory = Factory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IDataSourceAcquisitionFlowGateway>();
            services.AddSingleton<CompletedArtifactFlowGateway>();
            services.AddSingleton<IDataSourceAcquisitionFlowGateway>(provider => provider.GetRequiredService<CompletedArtifactFlowGateway>());
        }));
        var bootstrap = await GetBootstrapContextAsync(factory);
        var scopes = factory.Services.GetRequiredService<IRequestContextScopeFactory>();
        var topology = factory.Services.GetRequiredService<IOperationalTopologyProvisioner>();
        var workspace = kind switch
        {
            ResourceScopeKind.Instance => await topology.GetInstanceOperationsWorkspaceAsync(default),
            ResourceScopeKind.Tenant => await topology.GetTenantOperationsWorkspaceAsync(bootstrap.TenantId, default),
            _ => null
        };
        var context = workspace is null ? bootstrap : new RequestContext(bootstrap.PrincipalId, workspace.TenantId, workspace.Id);
        var owner = kind switch
        {
            ResourceScopeKind.Instance => ResourceScopeRef.Instance,
            ResourceScopeKind.Tenant => ResourceScopeRef.Tenant(bootstrap.TenantId),
            _ => ResourceScopeRef.Workspace(bootstrap.WorkspaceId)
        };
        string artifactId;
        using (scopes.Push(context))
        {
            var artifact = await factory.Services.GetRequiredService<ArtifactManagementService>().CreateStagedAsync(
                new("acquisition.txt", "text/plain", new ArtifactProducer { Kind = ArtifactProducerKind.FlowRun, Id = "artifact-producer" }), default);
            artifactId = artifact.Value.ArtifactId.ToString();
        }
        factory.Services.GetRequiredService<CompletedArtifactFlowGateway>().ArtifactId = artifactId;
        using (scopes.PushSystem())
        {
            await CreatePublishedAcquisitionFlowAsync(factory.Services, context, "artifact-acquire", owner);
            var profile = Profile() with { AcquisitionFlow = new() { Name = "artifact-acquire", UseActiveVersion = true } };
            var profiles = factory.Services.GetRequiredService<DataSourceProfileService>();
            _ = await profiles.CreateAsync(ProfileResource("artifact-profile", owner, profile), default);
            _ = await profiles.PublishAsync(ResourceNamespace.Default, "artifact-profile", owner, new("1.0.0"), default);
            _ = await factory.Services.GetRequiredService<DataSourceManagementService>().CreateAsync(
                SourceResource("artifact-origin", owner, new("artifact-profile", owner)), default);
        }
        using var client = factory.CreateClient();
        SelectConsoleContext(client, owner);
        var url = $"/api/datasources/artifact-origin/acquisitions?scopeRef={Uri.EscapeDataString(owner.Value)}";
        using var started = await client.PostAsJsonAsync(url, new StartDataSourceAcquisitionRequest { DataSourceScopeRef = owner });
        Assert.AreEqual(HttpStatusCode.Accepted, started.StatusCode);
        using var historyResponse = await client.GetAsync(url);
        Assert.AreEqual(HttpStatusCode.OK, historyResponse.StatusCode, await historyResponse.Content.ReadAsStringAsync());
        var history = await historyResponse.Content.ReadFromJsonAsync<DataSourceAcquisitionResource[]>();
        var acquisition = history!.Single();
        Assert.AreEqual(DataSourceAcquisitionState.Succeeded, acquisition.State, acquisition.ErrorMessage);
        Assert.AreEqual(artifactId, acquisition.Manifest!.Artifacts.Single().ArtifactId);
        if (kind != ResourceScopeKind.Workspace)
        {
            SelectConsoleContext(client, ResourceScopeRef.Workspace(bootstrap.WorkspaceId));
            using var hidden = await client.GetAsync($"/api/datasourceacquisitions/{acquisition.Name}");
            Assert.AreEqual(HttpStatusCode.NotFound, hidden.StatusCode);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AcquisitionToolReplaysTheFixedTargetAndReturnsTheNewArtifact(bool instanceContext)
    {
        await using var factory = Factory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IDataSourceAcquisitionFlowGateway>();
            // The callback below supplies verified execution material without creating a Runtime Run.
            services.RemoveAll<IToolExecutionEventSink>();
            services.AddSingleton<AcquisitionToolFlowGateway>();
            services.AddSingleton<IDataSourceAcquisitionFlowGateway>(provider =>
                provider.GetRequiredService<AcquisitionToolFlowGateway>());
        }));
        var bootstrap = await GetBootstrapContextAsync(factory);
        var scopes = factory.Services.GetRequiredService<IRequestContextScopeFactory>();
        var context = bootstrap;
        if (instanceContext)
        {
            var topology = factory.Services.GetRequiredService<IOperationalTopologyProvisioner>();
            var operations = await topology.GetInstanceOperationsWorkspaceAsync(default);
            context = new RequestContext(bootstrap.PrincipalId, operations.TenantId, operations.Id);
        }
        var workspace = ResourceScopeRef.Workspace(context.WorkspaceId);
        var owner = instanceContext ? ResourceScopeRef.Instance : workspace;
        var gateway = factory.Services.GetRequiredService<AcquisitionToolFlowGateway>();
        using (scopes.Push(context))
        {
            var artifacts = factory.Services.GetRequiredService<ArtifactManagementService>();
            for (var index = 0; index < 3; index++)
            {
                var artifact = await artifacts.CreateStagedAsync(new("catalog.json", "application/json",
                    new ArtifactProducer { Kind = ArtifactProducerKind.FlowRun, Id = $"catalog-{index}" }), default);
                await artifacts.WriteAsync(artifact.Value.ArtifactId, 0, "{}"u8.ToArray(), default);
                await artifacts.SealAsync(artifact.Value.ArtifactId, default);
                gateway.ArtifactIds.Enqueue(artifact.Value.ArtifactId.ToString());
            }
        }
        using (scopes.PushSystem())
        {
            await CreatePublishedAcquisitionFlowAsync(factory.Services, context, "tool-acquire", owner);
            await CreatePublishedDiscoveryFlowAsync(factory.Services, context, "tool-discover", owner);
            var profiles = factory.Services.GetRequiredService<DataSourceProfileService>();
            _ = await profiles.CreateAsync(ProfileResource("tool-profile", owner, Profile() with
            {
                AcquisitionFlow = new() { Name = "tool-acquire", UseActiveVersion = true },
                DiscoveryFlow = new() { Name = "tool-discover", UseActiveVersion = true }
            }), default);
            _ = await profiles.PublishAsync(ResourceNamespace.Default, "tool-profile", owner, new("1.0.0"), default);
            _ = await factory.Services.GetRequiredService<DataSourceManagementService>().CreateAsync(
                SourceResource("targeted-origin", owner, new("tool-profile", owner)), default);
        }

        using var client = factory.CreateClient();
        SelectConsoleContext(client, owner);
        var discovery = await StartAndAwaitDiscoveryAsync(client, owner);
        using var promoted = await client.PostAsJsonAsync($"/api/datasourcediscoveries/{discovery.Name}/targets",
            new PromoteDataSourceAcquisitionTargetRequest("documents", "documents"));
        Assert.AreEqual(HttpStatusCode.Created, promoted.StatusCode, await promoted.Content.ReadAsStringAsync());

        using var started = await client.PostAsJsonAsync(
            $"/api/datasources/targeted-origin/acquisitions?scopeRef={Uri.EscapeDataString(owner.Value)}",
            new StartDataSourceAcquisitionRequest { DataSourceScopeRef = owner, TargetName = "documents", Parameters = OpaquePayload });
        Assert.AreEqual(HttpStatusCode.Accepted, started.StatusCode, await started.Content.ReadAsStringAsync());
        var pending = (await started.Content.ReadFromJsonAsync<DataSourceAcquisitionResource>())!;
        var original = (await client.GetFromJsonAsync<DataSourceAcquisitionResource>($"/api/datasourceacquisitions/{pending.Name}"))!;
        Assert.AreEqual(DataSourceAcquisitionState.Succeeded, original.State);
        var originalArtifact = original.Manifest!.Artifacts.Single();

        var createUrl = $"/api/tooldefinitions/from-acquisition/{original.Name}";
        using var invalid = await client.PostAsJsonAsync(createUrl,
            new { name = "catalog", displayName = "Catalog", artifactId = Guid.NewGuid().ToString("N") });
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        using var created = await client.PostAsJsonAsync(createUrl,
            new { name = "catalog", displayName = "Catalog", artifactId = originalArtifact.ArtifactId });
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode, await created.Content.ReadAsStringAsync());
        var tool = (await created.Content.ReadFromJsonAsync<ToolDefinitionResource>())!;
        Assert.AreEqual(owner, tool.ScopeRef);
        Assert.AreEqual(original.Name, tool.Definition.Acquisition!.TemplateId);
        StringAssert.Contains(tool.Definition.Description!, "artifact reference, not the acquired data");
        StringAssert.Contains(tool.Definition.Description!, "Do not infer the acquired content");
        var listedDefinitions = await client.GetFromJsonAsync<JsonElement>("/api/tooldefinitions");
        Assert.IsTrue(listedDefinitions.GetProperty("value").EnumerateArray().Any(value =>
            value.GetProperty("metadata").GetProperty("name").GetString() == "catalog"));
        var listedTools = await client.GetFromJsonAsync<JsonElement>("/api/tools");
        Assert.IsTrue(listedTools.GetProperty("value").EnumerateArray().Any(value =>
            value.GetProperty("metadata").GetProperty("name").GetString() == "agentstration.catalog"));

        using var details = await client.GetAsync(created.Headers.Location);
        Assert.AreEqual(HttpStatusCode.OK, details.StatusCode, await details.Content.ReadAsStringAsync());
        using var projection = await client.GetAsync(
            $"/api/tools/agentstration.catalog?scopeRef={Uri.EscapeDataString(owner.Value)}");
        Assert.AreEqual(HttpStatusCode.OK, projection.StatusCode, await projection.Content.ReadAsStringAsync());
        var toolPath = $"/api/tools/agentstration.catalog/run?scopeRef={Uri.EscapeDataString(owner.Value)}";
        using var overrideResponse = await client.PostAsJsonAsync(toolPath,
            new RunToolRequest(JsonSerializer.SerializeToElement(new { limit = 100 }), ToolRunMode.Execute));
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, overrideResponse.StatusCode, await overrideResponse.Content.ReadAsStringAsync());
        var request = new RunToolRequest(JsonSerializer.SerializeToElement(new { }), ToolRunMode.Execute);
        var artifactIds = new HashSet<string>(StringComparer.Ordinal) { originalArtifact.ArtifactId };
        for (var call = 0; call < 2; call++)
        {
            using var response = await client.PostAsJsonAsync(toolPath, request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            var output = (await response.Content.ReadFromJsonAsync<RunToolResponse>())!.Output!.Value;
            Assert.IsTrue(artifactIds.Add(output.GetProperty("artifact").GetProperty("artifactId").GetString()!));
            Assert.AreNotEqual(original.Name, output.GetProperty("acquisitionId").GetString());
        }
        Assert.HasCount(3, gateway.AcquisitionInputs);
        foreach (var input in gateway.AcquisitionInputs)
        {
            Assert.IsTrue(JsonElement.DeepEquals(OpaquePayload, input.GetProperty("parameters")));
            Assert.AreEqual(original.Target!.Uid, input.GetProperty("target").GetProperty("uid").GetGuid());
            Assert.IsTrue(JsonElement.DeepEquals(OpaquePayload, input.GetProperty("target").GetProperty("selector")));
        }
        var latestDiscovery = await StartAndAwaitDiscoveryAsync(client, owner);
        using var stale = await client.PostAsJsonAsync(toolPath, request);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, stale.StatusCode);
        StringAssert.Contains(await stale.Content.ReadAsStringAsync(), "data_source_acquisition_target_stale");
        Assert.HasCount(3, gateway.AcquisitionInputs);
        var targets = await client.GetFromJsonAsync<DataSourceAcquisitionTargetView[]>(
            $"/api/datasources/targeted-origin/acquisition-targets?scopeRef={Uri.EscapeDataString(owner.Value)}");
        using var reconcile = new HttpRequestMessage(HttpMethod.Post, "/api/datasourceacquisitiontargets/documents/reconcile")
        { Content = JsonContent.Create(new ReconcileDataSourceAcquisitionTargetRequest(latestDiscovery.Name, "documents")) };
        reconcile.Headers.TryAddWithoutValidation("If-Match", targets!.Single().Target.ETag);
        using var reconciled = await client.SendAsync(reconcile);
        Assert.AreEqual(HttpStatusCode.OK, reconciled.StatusCode, await reconciled.Content.ReadAsStringAsync());
        gateway.ArtifactIds.Enqueue(originalArtifact.ArtifactId);
        using var replayed = await client.PostAsJsonAsync(toolPath, request);
        Assert.AreEqual(HttpStatusCode.OK, replayed.StatusCode, await replayed.Content.ReadAsStringAsync());

        if (instanceContext)
        {
            var projectedTool = (await projection.Content.ReadFromJsonAsync<ToolResource>())!;
            var runtimeTool = new RuntimeExecutionToolMaterial(projectedTool.Name, projectedTool.Namespace,
                projectedTool.Definition.DisplayName, null, ToolDefinitionAcquisitionTarget.InputSchema,
                ToolDefinitionAcquisitionTarget.OutputSchema, false, AgentstrationToolProvider.Name,
                projectedTool.Namespace, projectedTool.Definition.ExternalId)
            {
                ScopeRef = projectedTool.ScopeRef,
                Uid = projectedTool.Uid,
                Generation = projectedTool.Generation,
                ProviderScopeRef = projectedTool.Definition.Provider!.ScopeRef
            };
            var agent = new RuntimeExecutionAgentMaterial("material", "agent", Guid.NewGuid(), "catalog-agent", 1,
                "revision", "hash", "handler", "Catalog agent", "", "", "model", ResourceNamespace.Default, [runtimeTool]);
            var callback = new RuntimeGovernedToolRequest(agent, runtimeTool, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                null, context.TenantId, new WorkspaceId(context.WorkspaceId), context.PrincipalId, "catalog-agent-run",
                JsonSerializer.SerializeToElement(new { }), null)
            {
                ExecutionScopeRef = workspace,
                LogicalScopeRef = owner
            };
            gateway.ArtifactIds.Enqueue(originalArtifact.ArtifactId);
            var operationGateway = factory.Services.GetRequiredService<IRuntimeWorkerOperationGateway>();
            var output = await operationGateway.InvokeToolAsync(callback, default);
            Assert.AreEqual(originalArtifact.ArtifactId, output!.Value.GetProperty("artifact").GetProperty("artifactId").GetString());

            SelectConsoleContext(client, ResourceScopeRef.Workspace(bootstrap.WorkspaceId));
            using var hidden = await client.PostAsJsonAsync(createUrl,
                new { name = "catalog", displayName = "Catalog", artifactId = originalArtifact.ArtifactId });
            Assert.AreEqual(HttpStatusCode.NotFound, hidden.StatusCode);
        }
    }

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
        var tenant = ResourceScopeRef.Tenant(context.TenantId);

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
                    [new(workspace, [ResourceAccessAction.Discover, ResourceAccessAction.Use]),
                        new(tenant, [ResourceAccessAction.Discover, ResourceAccessAction.Use])]), null, default);
        }

        using var createdSource = await client.PostAsJsonAsync("/api/datasources",
            new CreateDataSourceRequest("docs", new()
            {
                DisplayName = "Documentation",
                Profile = new("shared-http", instance),
                Configuration = JsonSerializer.SerializeToElement(new { url = "https://docs.example.test" })
            }, ScopeRef: workspace));
        Assert.AreEqual(HttpStatusCode.Created, createdSource.StatusCode,
            await createdSource.Content.ReadAsStringAsync());
        var source = await createdSource.Content.ReadFromJsonAsync<DataSourceResource>();
        Assert.AreEqual(workspace, source?.ScopeRef);

        var readiness = await client.GetFromJsonAsync<DataSourceReadiness>(
            $"/api/datasources/docs/readiness?scopeRef={Uri.EscapeDataString(workspace.Value)}");
        Assert.IsNotNull(readiness);
        Assert.IsTrue(readiness.Ready);
        Assert.AreEqual(instance, readiness.Profile?.ScopeRef);
        Assert.AreEqual("1.0.0", readiness.Profile?.Version);

        SelectConsoleContext(client, tenant);
        using var tenantSource = await client.PostAsJsonAsync("/api/datasources",
            new CreateDataSourceRequest("tenant-docs", new()
            {
                DisplayName = "Tenant documentation",
                Profile = new("shared-http", instance),
                Configuration = JsonSerializer.SerializeToElement(new { url = "https://docs.example.test" })
            }, ScopeRef: tenant));
        Assert.AreEqual(HttpStatusCode.Created, tenantSource.StatusCode, await tenantSource.Content.ReadAsStringAsync());
        var tenantReadiness = await client.GetFromJsonAsync<DataSourceReadiness>(
            $"/api/datasources/tenant-docs/readiness?scopeRef={Uri.EscapeDataString(tenant.Value)}");
        Assert.IsNotNull(tenantReadiness);
        Assert.IsTrue(tenantReadiness.Ready);
        Assert.AreEqual(instance, tenantReadiness.Profile?.ScopeRef);

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
        var first = await profiles.ResolveActiveAsync(workspace, source.Value.Namespace, source.Value.Definition.Profile!, default);
        Assert.AreEqual("1.0.0", first.Version);

        var draft = profile.Value.Definition with { Description = "Second revision", Version = "2.0.0" };
        var current = await profiles.GetAsync(ResourceNamespace.Default, "versioned", workspace, default);
        _ = await profiles.PutAsync(ResourceNamespace.Default, "versioned", workspace, draft, current!.ETag, default);
        _ = await profiles.PublishAsync(ResourceNamespace.Default, "versioned", workspace,
            new("2.0.0", Activate: false), default);
        var beforeActivation = await profiles.ResolveActiveAsync(workspace, ResourceNamespace.Default,
            source.Value.Definition.Profile!, default);
        Assert.AreEqual("1.0.0", beforeActivation.Version);
        var profileBeforeActivation = await profiles.GetAsync(ResourceNamespace.Default, "versioned", workspace, default);
        _ = await profiles.ActivateAsync(ResourceNamespace.Default, "versioned", workspace,
            new("2.0.0"), profileBeforeActivation!.ETag, default);
        var afterActivation = await profiles.ResolveActiveAsync(workspace, ResourceNamespace.Default,
            source.Value.Definition.Profile!, default);
        Assert.AreEqual("2.0.0", afterActivation.Version);

        var profileBeforeRollback = await profiles.GetAsync(ResourceNamespace.Default, "versioned", workspace, default);
        _ = await profiles.ActivateAsync(ResourceNamespace.Default, "versioned", workspace,
            new("1.0.0"), profileBeforeRollback!.ETag, default);
        var afterRollback = await profiles.ResolveActiveAsync(workspace, ResourceNamespace.Default,
            source.Value.Definition.Profile!, default);
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
        Assert.AreEqual("1.0.0", started.Composition!.Profile.Version);
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
            _ = await factory.Services.GetRequiredService<IResourceStore>().PutExactAsync(foreignWorkspace,
                SourceResource("foreign-source", foreignWorkspace, new("unresolved", foreignWorkspace)), null, true, default);
        }

        using var mutation = await client.PutAsJsonAsync(
            $"/api/datasourceprofiles/core-http?scopeRef={Uri.EscapeDataString(workspace.Value)}",
            new PutDataSourceProfileRequest(Profile() with { Description = "Changed" }));
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, mutation.StatusCode);
        StringAssert.Contains(await mutation.Content.ReadAsStringAsync(), "data_source_profile_builtin_protected");

        using var crossWorkspace = await client.PostAsJsonAsync(
            $"/api/datasources/foreign-source/discoveries?scopeRef={Uri.EscapeDataString(foreignWorkspace.Value)}",
            new StartDataSourceDiscoveryRequest { DataSourceScopeRef = foreignWorkspace });
        Assert.AreEqual(HttpStatusCode.Forbidden, crossWorkspace.StatusCode);
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

    [TestMethod]
    [DataRow(ResourceScopeKind.Workspace, false)]
    [DataRow(ResourceScopeKind.Instance, false)]
    [DataRow(ResourceScopeKind.Tenant, false)]
    [DataRow(ResourceScopeKind.Workspace, true)]
    [DataRow(ResourceScopeKind.Instance, true)]
    [DataRow(ResourceScopeKind.Tenant, true)]
    public async Task DiscoveryPromotesExplicitTargetsAndRediscoveryMakesThemStale(ResourceScopeKind scopeKind, bool recoverPending)
    {
        await using var factory = Factory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IDataSourceAcquisitionFlowGateway>();
            services.AddSingleton<DataSourceAcquisitionFlowGateway>();
            services.AddSingleton<RecordingDataSourceFlowGateway>();
            services.AddSingleton<IDataSourceAcquisitionFlowGateway>(provider => provider.GetRequiredService<RecordingDataSourceFlowGateway>());
            services.RemoveAll<IToolExecutionPipeline>();
            services.AddSingleton<RecordingFlowToolPipeline>();
            services.AddSingleton<IToolExecutionPipeline>(provider => provider.GetRequiredService<RecordingFlowToolPipeline>());
        }));
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var context = await GetBootstrapContextAsync(factory);
        var workspace = scopeKind switch
        {
            ResourceScopeKind.Instance => ResourceScopeRef.Instance,
            ResourceScopeKind.Tenant => ResourceScopeRef.Tenant(context.TenantId),
            _ => ResourceScopeRef.Workspace(context.WorkspaceId)
        };
        using (factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem())
        {
            await CreatePublishedAcquisitionFlowAsync(factory.Services, context, "target-acquire", workspace, routed: true);
            await CreatePublishedDiscoveryFlowAsync(factory.Services, context, "target-discover", workspace);
            var profiles = factory.Services.GetRequiredService<DataSourceProfileService>();
            var sources = factory.Services.GetRequiredService<DataSourceManagementService>();
            var definition = Profile() with
            {
                AcquisitionFlow = new() { Name = "target-acquire", UseActiveVersion = true },
                DiscoveryFlow = new() { Name = "target-discover", UseActiveVersion = true },
                ConfigurationSchema = JsonSerializer.SerializeToElement(new
                {
                    type = "object",
                    properties = new { url = new { type = "string" }, ExtensionData = new { type = "object" } },
                    required = new[] { "url" },
                    additionalProperties = false,
                    ExtensionHints = OpaquePayload
                })
            };
            _ = await profiles.CreateAsync(ProfileResource("targeted", workspace, definition), default);
            _ = await profiles.PublishAsync(ResourceNamespace.Default, "targeted", workspace, new("1.0.0"), default);
            var source = SourceResource("targeted-origin", workspace, new("targeted", workspace));
            _ = await sources.CreateAsync(source with
            {
                Definition = source.Definition with
                {
                    Configuration = JsonSerializer.SerializeToElement(new { url = "https://docs.example.test", ExtensionData = OpaquePayload })
                }
            }, default);
        }

        using var client = factory.CreateClient();
        SelectConsoleContext(client, workspace);
        var topology = factory.Services.GetRequiredService<IOperationalTopologyProvisioner>();
        var executionWorkspace = scopeKind switch
        {
            ResourceScopeKind.Instance => (await topology.GetInstanceOperationsWorkspaceAsync(default)).Id,
            ResourceScopeKind.Tenant => (await topology.GetTenantOperationsWorkspaceAsync(context.TenantId, default)).Id,
            _ => context.WorkspaceId
        };
        var history = await client.GetFromJsonAsync<DataSourceAcquisitionResource[]>($"/api/datasources/targeted-origin/acquisitions?scopeRef={Uri.EscapeDataString(workspace.Value)}");
        Assert.IsEmpty(history!);
        using var missingTarget = await client.PostAsJsonAsync(
            $"/api/datasources/targeted-origin/acquisitions?scopeRef={Uri.EscapeDataString(workspace.Value)}",
            new StartDataSourceAcquisitionRequest { DataSourceScopeRef = workspace });
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, missingTarget.StatusCode);
        StringAssert.Contains(await missingTarget.Content.ReadAsStringAsync(), "data_source_acquisition_target_required");

        var gateway = factory.Services.GetRequiredService<RecordingDataSourceFlowGateway>();
        gateway.DeferStarts = recoverPending;
        var discovery = await StartAndAwaitDiscoveryAsync(client, workspace, "discover-once", async () =>
        {
            if (!recoverPending) return;
            gateway.DeferStarts = false;
            using var system = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
            await factory.Services.GetRequiredService<DataSourceDiscoveryService>().RecoverPendingAsync(default);
        });
        Assert.AreEqual(DataSourceDiscoveryState.Succeeded, discovery.State);
        Assert.AreEqual("documents", discovery.Manifest?.Choices.Single().Id);
        var repeatedDiscovery = await StartAndAwaitDiscoveryAsync(client, workspace, "discover-once");
        Assert.AreEqual(discovery.Uid, repeatedDiscovery.Uid);
        using var promotedResponse = await client.PostAsJsonAsync(
            $"/api/datasourcediscoveries/{discovery.Name}/targets",
            new PromoteDataSourceAcquisitionTargetRequest("documents", "documents"));
        Assert.AreEqual(HttpStatusCode.Created, promotedResponse.StatusCode);
        var promoted = await promotedResponse.Content.ReadFromJsonAsync<DataSourceAcquisitionTargetResource>();
        Assert.AreEqual(discovery.Uid, promoted?.DiscoveryUid);

        gateway.DeferStarts = recoverPending;
        using var started = await client.PostAsJsonAsync(
            $"/api/datasources/targeted-origin/acquisitions?scopeRef={Uri.EscapeDataString(workspace.Value)}",
            new StartDataSourceAcquisitionRequest { DataSourceScopeRef = workspace, TargetName = "documents", Parameters = OpaquePayload });
        Assert.AreEqual(HttpStatusCode.Accepted, started.StatusCode);
        var accepted = await started.Content.ReadFromJsonAsync<DataSourceAcquisitionResource>();
        Assert.AreEqual(promoted?.Uid, accepted?.Target?.Uid);
        Assert.IsNotNull(accepted);
        Assert.AreEqual(ResourceScopeRef.Workspace(executionWorkspace), accepted.ScopeRef);
        Assert.AreEqual(workspace, accepted.LogicalScopeRef);
        Assert.AreEqual(workspace, discovery.LogicalScopeRef);
        Assert.AreEqual(executionWorkspace, discovery.WorkspaceId);
        if (recoverPending)
        {
            gateway.DeferStarts = false;
            using var system = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
            await factory.Services.GetRequiredService<DataSourceAcquisitionService>().RecoverPendingAsync(default);
        }
        var flow = await client.GetFromJsonAsync<FlowRun>($"/api/flowRuns/{accepted.FlowRunId}");
        Assert.IsNotNull(flow);
        Assert.AreEqual(workspace, flow.Scope.LogicalScopeRef);
        for (var attempt = 0; attempt < 200 && accepted.State is DataSourceAcquisitionState.Pending
                 or DataSourceAcquisitionState.Running or DataSourceAcquisitionState.WaitingForChild; attempt++)
        {
            await Task.Delay(50);
            accepted = await client.GetFromJsonAsync<DataSourceAcquisitionResource>($"/api/datasourceacquisitions/{accepted.Name}")
                ?? throw new AssertFailedException("The acquisition response must exist.");
        }
        Assert.AreEqual(DataSourceAcquisitionState.Succeeded, accepted.State, $"{accepted.ErrorCode}: {accepted.ErrorMessage}");
        var pipeline = factory.Services.GetRequiredService<RecordingFlowToolPipeline>();
        var invocation = pipeline.Invocations.Single();
        Assert.IsTrue(JsonElement.DeepEquals(OpaquePayload, invocation.Context.Arguments!.Value.GetProperty("selector")));
        Assert.AreEqual("targeted", invocation.Context.Arguments.Value.GetProperty("profileName").GetString());
        AssertFlowInputContract(gateway, discovery.FlowRunId, recoverPending, acquisition: false);
        AssertFlowInputContract(gateway, accepted.FlowRunId, recoverPending, acquisition: true);
        Assert.AreEqual(new WorkspaceId(executionWorkspace), invocation.Context.WorkspaceId);
        Assert.AreEqual(context.PrincipalId, invocation.Context.PrincipalId);
        Assert.AreEqual(workspace, invocation.Context.ToolScopeRef);
        Assert.AreEqual(executionWorkspace, invocation.Request.WorkspaceId);
        Assert.AreEqual(scopeKind switch
        {
            ResourceScopeKind.Instance => ControlPlaneAccessMode.Instance,
            ResourceScopeKind.Tenant => ControlPlaneAccessMode.TenantAdministration,
            _ => ControlPlaneAccessMode.Workspace
        }, invocation.Mode);


        if (scopeKind != ResourceScopeKind.Workspace)
        {
            SelectConsoleContext(client, ResourceScopeRef.Workspace(context.WorkspaceId));
            using var hiddenAcquisition = await client.GetAsync($"/api/datasourceacquisitions/{accepted.Name}");
            Assert.AreEqual(HttpStatusCode.NotFound, hiddenAcquisition.StatusCode);
            using var hiddenDiscovery = await client.GetAsync($"/api/datasourcediscoveries/{discovery.Name}");
            Assert.AreEqual(HttpStatusCode.NotFound, hiddenDiscovery.StatusCode);
            SelectConsoleContext(client, workspace);
        }

        var rediscovery = await StartAndAwaitDiscoveryAsync(client, workspace);
        var targets = await client.GetFromJsonAsync<DataSourceAcquisitionTargetView[]>(
            $"/api/datasources/targeted-origin/acquisition-targets?scopeRef={Uri.EscapeDataString(workspace.Value)}");
        Assert.IsTrue(targets?.Single().Stale);
        using var stale = await client.PostAsJsonAsync(
            $"/api/datasources/targeted-origin/acquisitions?scopeRef={Uri.EscapeDataString(workspace.Value)}",
            new StartDataSourceAcquisitionRequest { DataSourceScopeRef = workspace, TargetName = "documents" });
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, stale.StatusCode);
        StringAssert.Contains(await stale.Content.ReadAsStringAsync(), "data_source_acquisition_target_stale");

        var observedTarget = targets!.Single().Target;
        Assert.IsNotNull(observedTarget.ETag, "The listed target must expose its observed concurrency token.");
        using var conflictingRequest = new HttpRequestMessage(HttpMethod.Post,
            "/api/datasourceacquisitiontargets/documents/reconcile")
        { Content = JsonContent.Create(new ReconcileDataSourceAcquisitionTargetRequest(rediscovery.Name, "documents")) };
        conflictingRequest.Headers.TryAddWithoutValidation("If-Match", "\"outdated-target\"");
        using var conflictingResponse = await client.SendAsync(conflictingRequest);
        Assert.AreEqual(HttpStatusCode.Conflict, conflictingResponse.StatusCode);

        using var reconcileRequest = new HttpRequestMessage(HttpMethod.Post,
            "/api/datasourceacquisitiontargets/documents/reconcile")
        { Content = JsonContent.Create(new ReconcileDataSourceAcquisitionTargetRequest(rediscovery.Name, "documents")) };
        reconcileRequest.Headers.TryAddWithoutValidation("If-Match", observedTarget.ETag);
        using var reconciledResponse = await client.SendAsync(reconcileRequest);
        Assert.AreEqual(HttpStatusCode.OK, reconciledResponse.StatusCode);
        var currentTargets = await client.GetFromJsonAsync<DataSourceAcquisitionTargetView[]>(
            $"/api/datasources/targeted-origin/acquisition-targets?scopeRef={Uri.EscapeDataString(workspace.Value)}");
        Assert.IsFalse(currentTargets?.Single().Stale);
        var reconciledTarget = currentTargets!.Single().Target;
        Assert.AreEqual(observedTarget.Uid, reconciledTarget.Uid);
        Assert.AreEqual(observedTarget.Name, reconciledTarget.Name);
        Assert.AreEqual(rediscovery.Uid, reconciledTarget.DiscoveryUid);
        Assert.IsGreaterThan(observedTarget.Generation, reconciledTarget.Generation);
        var historicalAcquisition = await client.GetFromJsonAsync<DataSourceAcquisitionResource>(
            $"/api/datasourceacquisitions/{accepted.Name}");
        Assert.AreEqual(discovery.Uid, historicalAcquisition!.Target!.DiscoveryUid);
    }

    private static async Task<DataSourceDiscoveryResource> StartAndAwaitDiscoveryAsync(HttpClient client, ResourceScopeRef workspace,
        string? idempotencyKey = null, Func<Task>? afterStart = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/datasources/targeted-origin/discoveries?scopeRef={Uri.EscapeDataString(workspace.Value)}")
        { Content = JsonContent.Create(new StartDataSourceDiscoveryRequest { DataSourceScopeRef = workspace, Parameters = OpaquePayload }) };
        if (idempotencyKey is not null) request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
        var current = await response.Content.ReadFromJsonAsync<DataSourceDiscoveryResource>()
            ?? throw new AssertFailedException("Discovery response missing.");
        if (afterStart is not null) await afterStart();
        for (var attempt = 0; attempt < 50 && current.State is DataSourceDiscoveryState.Pending
                 or DataSourceDiscoveryState.Running or DataSourceDiscoveryState.WaitingForChild; attempt++)
        {
            await Task.Delay(50);
            current = await client.GetFromJsonAsync<DataSourceDiscoveryResource>($"/api/datasourcediscoveries/{current.Name}")
                ?? throw new AssertFailedException("Discovery status missing.");
        }
        return current;
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
        ToolProviderResource provider,
        ToolSchema? schema = null) => store.PutExactAsync(scope, new ToolResource
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
                Schema = schema,
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
        ResourceScopeRef? ownerScope = null,
        bool routed = false)
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
        if (routed)
        {
            var store = services.GetRequiredService<IResourceStore>();
            var provider = await PutProviderAsync(store, scope, "route-provider");
            _ = await PutToolAsync(store, scope, "route-fetch", provider.Value,
                new ToolSchema
                {
                    Input = JsonSerializer.SerializeToElement(new
                    {
                        type = "object",
                        properties = new { selector = new { type = "object" }, profileName = new { type = "string" } },
                        required = new[] { "selector", "profileName" }
                    })
                });
            _ = await services.GetRequiredService<ToolSetService>().CreateAsync(new ToolSetResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = ToolResourceKinds.ToolSet,
                Metadata = new() { Name = "route-tools" },
                ScopeRef = scope,
                Definition = new()
                {
                    DisplayName = "Routed acquisition Tools",
                    Version = "1.0.0",
                    Publish = true,
                    Members = [new() { Tool = new("route-fetch", scope), Capability = "source.fetch", Route = "fetch" }]
                }
            }, default);
        }

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
                    .. (routed ? new FlowStepDefinition[] { new ToolRouteFlowStepDefinition { Name = "fetch", ToolSet = new("route-tools", "1.0.0", ScopeRef: scope), Capability = "source.fetch", Route = "fetch", ArgumentsMapping = JsonSerializer.SerializeToElement(new { selector = "${input.target.selector}", profileName = "${input.profile.name}" }) } } : []),
                    new OutputFlowStepDefinition { Name = "output", OutputMapping = output }
                ],
                Transitions = routed
                    ? [new("input-fetch", "input", "completed", "fetch"), new("fetch-output", "fetch", "success", "output")]
                    : [new("input-output", "input", "completed", "output")]
            }), ResourceNamespace.Default, default);
        await flows.PublishVersionExactAsync(scope, new(name), "1.0.0", true, default);
    }

    private static WorkflowFlowDefinition GraphOnlyDefinition() => new(
        "input",
        [new FlowNode("input", FlowNodeKind.Input)],
        []);

    private static async Task CreatePublishedDiscoveryFlowAsync(IServiceProvider services, RequestContext context, string name, ResourceScopeRef? ownerScope = null)
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
                parameters = new { type = "object" },
                caller = new { type = "object" },
                correlationId = new { type = "string" },
                discoveryId = new { type = "string" }
            },
            required = new[] { "dataSourceId", "dataSourceUid", "dataSourceGeneration", "profile",
                "sourceConfiguration", "parameters", "caller", "correlationId", "discoveryId" }
        });
        var outputSchema = JsonSerializer.SerializeToElement(new
        { type = "object", properties = new { choices = new { type = "array" }, artifacts = new { type = "array" } }, required = new[] { "choices" } });
        var output = JsonSerializer.SerializeToElement(new
        {
            choices = new[] { new { id = "documents", displayName = "Documents", selector = OpaquePayload } },
            artifacts = Array.Empty<object>()
        });
        var flows = services.GetRequiredService<FlowService>();
        var scope = ownerScope ?? ResourceScopeRef.Workspace(context.WorkspaceId);
        await flows.CreateExactAsync(scope, new CreateFlowCommand(name, null, "1.0.0", true,
            GraphOnlyDefinition(),
            new Dictionary<string, string> { [FlowMetadataKeys.Contract] = DataSourceFlowContracts.Discovery },
            new FlowGraphDefinition
            {
                EntryStep = "input",
                InputSchema = inputSchema,
                OutputSchema = outputSchema,
                Steps = [new InputFlowStepDefinition { Name = "input", Schema = inputSchema },
                    new OutputFlowStepDefinition { Name = "output", OutputMapping = output }],
                Transitions = [new("input-output", "input", "completed", "output")]
            }), ResourceNamespace.Default, default);
        await flows.PublishVersionExactAsync(scope, new(name), "1.0.0", true, default);
    }
    private static JsonElement OpaquePayload => JsonSerializer.SerializeToElement(new Dictionary<string, object?>
    {
        ["PascalCase"] = new Dictionary<string, object?> { ["InnerKey"] = new object?[] { "value", 42, true, null } },
        ["pascalCase"] = "distinct",
        ["UPPER_CASE"] = false
    });

    private static void AssertFlowInputContract(RecordingDataSourceFlowGateway gateway, string runId, bool recovered, bool acquisition)
    {
        var requests = gateway.Requests.Where(request => request.RunId == runId).ToArray();
        Assert.AreEqual(recovered ? 2 : 1, requests.Length);
        foreach (var request in requests)
        {
            var input = request.Input;
            var profile = input.GetProperty("profile");
            Assert.AreEqual("targeted", profile.GetProperty("name").GetString());
            Assert.AreEqual("target-acquire", profile.GetProperty("acquisitionFlow").GetProperty("name").GetString());
            Assert.IsFalse(profile.TryGetProperty("Name", out _));
            var schema = profile.GetProperty("definition").GetProperty("configurationSchema");
            Assert.IsTrue(schema.GetProperty("properties").TryGetProperty("ExtensionData", out _));
            Assert.IsTrue(JsonElement.DeepEquals(OpaquePayload, schema.GetProperty("ExtensionHints")));
            Assert.IsTrue(JsonElement.DeepEquals(OpaquePayload, input.GetProperty("sourceConfiguration").GetProperty("ExtensionData")));
            Assert.IsTrue(input.GetProperty("caller").TryGetProperty("principalId", out _));
            Assert.IsTrue(JsonElement.DeepEquals(OpaquePayload, input.GetProperty("parameters")));
            if (acquisition)
            {
                Assert.IsTrue(JsonElement.DeepEquals(OpaquePayload, input.GetProperty("target").GetProperty("selector")));
                Assert.IsTrue(JsonElement.DeepEquals(profile, input.GetProperty("target").GetProperty("profile")));
            }
        }
        if (recovered) Assert.IsTrue(JsonElement.DeepEquals(requests[0].Input, requests[1].Input));
    }

    private sealed class CompletedArtifactFlowGateway : IDataSourceAcquisitionFlowGateway
    {
        public string ArtifactId { get; set; } = string.Empty;
        public Task<DataSourceFlowRunSnapshot> StartAsync(DataSourceFlowRunRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(Snapshot(request.RunId));
        public Task<DataSourceFlowRunSnapshot?> GetAsync(Guid workspaceId, string runId, Guid tenantId, Guid principalId, CancellationToken cancellationToken) =>
            Task.FromResult<DataSourceFlowRunSnapshot?>(Snapshot(runId));
        public Task<DataSourceFlowRunSnapshot> CancelAsync(Guid workspaceId, string runId, Guid tenantId, Guid principalId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        private DataSourceFlowRunSnapshot Snapshot(string runId) => new(runId, DataSourceAcquisitionState.Succeeded,
            JsonSerializer.SerializeToElement(new { artifacts = new[] { new { artifactId = ArtifactId, kind = "Staged", disposition = "Intermediate" } } }),
            null, null, null);
    }

    private sealed class AcquisitionToolFlowGateway : IDataSourceAcquisitionFlowGateway
    {
        private readonly ConcurrentDictionary<string, DataSourceFlowRunSnapshot> completed = new(StringComparer.Ordinal);
        private int sequence;
        public ConcurrentQueue<string> ArtifactIds { get; } = new();
        public ConcurrentQueue<JsonElement> AcquisitionInputs { get; } = new();

        public Task<DataSourceFlowRunSnapshot> StartAsync(DataSourceFlowRunRequest request, CancellationToken cancellationToken)
        {
            JsonElement output;
            if (request.Flow.Name == "tool-discover")
            {
                output = JsonSerializer.SerializeToElement(new
                {
                    choices = new[] { new { id = "documents", displayName = "Documents", selector = OpaquePayload } }
                });
            }
            else
            {
                AcquisitionInputs.Enqueue(request.Input);
                Assert.IsTrue(ArtifactIds.TryDequeue(out var artifactId));
                output = JsonSerializer.SerializeToElement(new
                {
                    artifacts = new[] { new { artifactId, kind = "Staged", disposition = "Publishable", name = "catalog.json", mediaType = "application/json" } }
                });
            }
            var snapshot = new DataSourceFlowRunSnapshot(request.RunId, DataSourceAcquisitionState.Succeeded,
                output, null, null, DateTimeOffset.UnixEpoch.AddSeconds(Interlocked.Increment(ref sequence)));
            completed[request.RunId] = snapshot;
            return Task.FromResult(snapshot);
        }

        public Task<DataSourceFlowRunSnapshot?> GetAsync(Guid workspaceId, string runId, Guid tenantId, Guid principalId,
            CancellationToken cancellationToken) => Task.FromResult(completed.GetValueOrDefault(runId));

        public Task<DataSourceFlowRunSnapshot> CancelAsync(Guid workspaceId, string runId, Guid tenantId, Guid principalId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    // Simulates a durable Pending operation whose dispatch did not reach Flow.
    private sealed class RecordingDataSourceFlowGateway(DataSourceAcquisitionFlowGateway inner) : IDataSourceAcquisitionFlowGateway
    {
        public bool DeferStarts { get; set; }
        public ConcurrentQueue<DataSourceFlowRunRequest> Requests { get; } = new();

        public Task<DataSourceFlowRunSnapshot> StartAsync(DataSourceFlowRunRequest request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request);
            return DeferStarts
                ? Task.FromResult(new DataSourceFlowRunSnapshot(request.RunId, DataSourceAcquisitionState.Pending, null, null, null, null))
                : inner.StartAsync(request, cancellationToken);
        }

        public Task<DataSourceFlowRunSnapshot?> GetAsync(Guid workspaceId, string runId, Guid tenantId, Guid principalId,
            CancellationToken cancellationToken) => inner.GetAsync(workspaceId, runId, tenantId, principalId, cancellationToken);

        public Task<DataSourceFlowRunSnapshot> CancelAsync(Guid workspaceId, string runId, Guid tenantId, Guid principalId,
            CancellationToken cancellationToken) => inner.CancelAsync(workspaceId, runId, tenantId, principalId, cancellationToken);
    }

    private sealed class RecordingFlowToolPipeline(ICurrentRequestContext requestContext) : IToolExecutionPipeline
    {
        public ConcurrentQueue<(ToolExecutionContext Context, RequestContext Request, ControlPlaneAccessMode Mode)> Invocations { get; } = new();
        public ValueTask<JsonElement?> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken = default)
        {
            Invocations.Enqueue((context, requestContext.Current, requestContext.AccessMode));
            return ValueTask.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new { artifacts = Array.Empty<object>() }));
        }
    }

}
