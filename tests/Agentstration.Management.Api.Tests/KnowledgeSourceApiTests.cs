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
using Agentstration.Infrastructure;
using Agentstration.Infrastructure.Knowledge;
using Agentstration.Knowledge;
using Agentstration.Knowledge.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class KnowledgeSourceApiTests : ModelManagementApiTestBase
{
    [TestMethod]
    public async Task KnowledgeSourcesPinThreeScopeDependenciesAndRequireDescendantUseGrants()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        var instanceScope = ResourceScopeRef.Instance;
        var tenantScope = ResourceScopeRef.Tenant(context.TenantId);
        var workspaceScope = ResourceScopeRef.Workspace(context.WorkspaceId);
        var dataSources = factory.Services.GetRequiredService<DataSourceManagementService>();
        var source = await dataSources.CreateAsync(new DataSourceResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = DataSourceResourceKinds.DataSource,
            Metadata = new() { Name = "shared-artifact-origin" },
            ScopeRef = instanceScope,
            Definition = new()
            {
                DisplayName = "Shared artifact origin",
                Profile = new("artifact-import-builtin", instanceScope),
                Configuration = JsonSerializer.SerializeToElement(new { artifactIds = Array.Empty<string>() })
            }
        }, default);
        var policies = factory.Services.GetRequiredService<IResourceAccessPolicyService>();
        using (factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(context))
            _ = await policies.PutAsync(new(new(source.Value.Uid, instanceScope, source.Value.Namespace,
                source.Value.Kind, source.Value.Generation),
                [new(tenantScope, [ResourceAccessAction.Discover, ResourceAccessAction.Use], true)]), null, default);

        var knowledge = factory.Services.GetRequiredService<KnowledgeSourceManagementService>();
        async Task<StoredResource<KnowledgeSourceResource>> CreateAsync(ResourceScopeRef scope, string name) =>
            await knowledge.CreateAsync(new KnowledgeSourceResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = KnowledgeResourceKinds.KnowledgeSource,
                Metadata = new() { Name = name },
                ScopeRef = scope,
                Definition = new()
                {
                    DisplayName = $"Knowledge {scope}",
                    DataSources =
                    [
                        new KnowledgeDataSourceBinding
                        {
                            Name = "origin",
                            DataSource = new(source.Value.Name, instanceScope)
                        }
                    ],
                    ProjectionFlow = new()
                    {
                        Name = KnowledgePlatformResourceProvisioner.ProjectionFlowName,
                        ScopeRef = instanceScope
                    },
                    RetrievalFlow = new()
                    {
                        Name = KnowledgePlatformResourceProvisioner.RetrievalFlowName,
                        ScopeRef = instanceScope
                    }
                }
            }, default);

        var instance = await CreateAsync(instanceScope, "instance-knowledge");
        var tenant = await CreateAsync(tenantScope, "tenant-knowledge");
        var workspace = await CreateAsync(workspaceScope, "workspace-knowledge");

        using (var client = factory.CreateClient())
        using (var response = await client.PostAsJsonAsync("/api/knowledgesources",
                   new CreateKnowledgeSourceRequest("api-instance-knowledge", instance.Value.Definition,
                       ScopeRef: instanceScope)))
        {
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode,
                await response.Content.ReadAsStringAsync());
            var apiCreated = await response.Content.ReadFromJsonAsync<KnowledgeSourceResource>();
            Assert.AreEqual(instanceScope, apiCreated?.ScopeRef);
            StringAssert.Contains(response.Headers.Location?.OriginalString ?? string.Empty,
                Uri.EscapeDataString(instanceScope.Value));
        }

        foreach (var stored in new[] { instance, tenant, workspace })
        {
            Assert.IsNotNull(stored.Value.Composition);
            Assert.IsFalse(string.IsNullOrWhiteSpace(stored.Value.Composition.DependencyHash));
            Assert.AreEqual(instanceScope, stored.Value.Composition.ProjectionFlow.ScopeRef);
            Assert.AreEqual(instanceScope, stored.Value.Composition.RetrievalFlow.ScopeRef);
            var dependency = stored.Value.Composition.DataSources.Single();
            Assert.AreEqual(instanceScope, dependency.DataSourceScopeRef);
            Assert.AreEqual(source.Value.Uid, dependency.DataSourceUid);
            Assert.AreEqual(source.Value.Generation, dependency.DataSourceGeneration);
            Assert.AreEqual(instanceScope, dependency.ProfileScopeRef);
            Assert.IsFalse(string.IsNullOrWhiteSpace(dependency.ProfileDefinitionHash));
            Assert.IsFalse(string.IsNullOrWhiteSpace(dependency.ProfileDependencyHash));
        }

        await Assert.ThrowsExactlyAsync<KnowledgeSourceNotFoundException>(() =>
            knowledge.ResolveForUseAsync(new(instance.Value.Name, instance.Value.Namespace, instanceScope),
                workspaceScope, default));

        using (factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(context))
            _ = await policies.PutAsync(new(new(instance.Value.Uid, instanceScope, instance.Value.Namespace,
                instance.Value.Kind, instance.Value.Generation),
                [new(tenantScope, [ResourceAccessAction.Discover, ResourceAccessAction.Use], true)]), null, default);
        var inherited = await knowledge.ResolveForUseAsync(
            new(instance.Value.Name, instance.Value.Namespace, instanceScope), workspaceScope, default);
        Assert.AreEqual(instance.Value.Uid, inherited.Value.Uid);

        var otherWorkspaceId = Guid.NewGuid();
        var otherWorkspace = new Workspace(otherWorkspaceId, context.TenantId,
            $"knowledge-{otherWorkspaceId:N}", "Knowledge isolation", WorkspaceStatus.Initializing,
            DateTimeOffset.UtcNow);
        await factory.Services.GetRequiredService<IIdentityStore>().AddWorkspaceAsync(otherWorkspace, default);
        await factory.Services.GetRequiredService<IWorkspaceProvisioner>().ProvisionAsync(otherWorkspace, default);
        var store = factory.Services.GetRequiredService<IResourceStore>();
        KnowledgeProjectionResource Projection(string name, Guid workspaceId) => new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = KnowledgeResourceKinds.KnowledgeProjection,
            Metadata = new() { Name = name },
            ScopeRef = ResourceScopeRef.Workspace(workspaceId),
            Generation = 1,
            KnowledgeSourceUid = instance.Value.Uid,
            KnowledgeSourceScopeRef = instanceScope,
            KnowledgeSourceName = instance.Value.Name,
            KnowledgeSourceNamespace = instance.Value.Namespace,
            KnowledgeSourceGeneration = instance.Value.Generation,
            ProjectionFlow = instance.Value.Composition!.ProjectionFlow,
            RetrievalFlow = instance.Value.Composition.RetrievalFlow,
            ProjectionFlowRunId = $"run-{name}",
            State = KnowledgeProjectionState.Succeeded,
            CorrelationId = name,
            CreatedBy = context.PrincipalId,
            TenantId = context.TenantId,
            WorkspaceId = workspaceId,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _ = await store.PutExactAsync(workspaceScope, Projection("projection-a", context.WorkspaceId),
            null, true, default);
        var otherScope = ResourceScopeRef.Workspace(otherWorkspaceId);
        var contextScopes = factory.Services.GetRequiredService<IRequestContextScopeFactory>();
        using (contextScopes.Push(context with { WorkspaceId = otherWorkspaceId }))
            _ = await store.PutExactAsync(otherScope, Projection("projection-b", otherWorkspaceId),
                null, true, default);
        var projections = factory.Services.GetRequiredService<KnowledgeProjectionService>();
        var sourceId = new KnowledgeSourceId(instance.Value.Name, instance.Value.Namespace, instanceScope);
        Assert.AreEqual("projection-a", (await projections.ListAsync(sourceId, default)).Single().Name);
        using (contextScopes.Push(context with { WorkspaceId = otherWorkspaceId }))
        {
            Assert.AreEqual("projection-b", (await projections.ListAsync(sourceId, default)).Single().Name);
        }

        using (factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(context))
            _ = await dataSources.PutAsync(source.Value.Namespace, source.Value.Name, instanceScope,
                source.Value.Definition with { Description = "Changed dependency" }, source.ETag, default);
        var stale = await knowledge.GetReadinessAsync(
            new(instance.Value.Name, instance.Value.Namespace, instanceScope), default);
        Assert.IsFalse(stale.Ready);
        StringAssert.Contains(string.Join(' ', stale.Issues), "stale");
    }

    [TestMethod]
    public async Task InstanceReceivesProtectedPublishedBuiltInDataSourceProfilesIdempotently()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        var scope = ResourceScopeRef.Instance;
        var workspaceScope = ResourceScopeRef.Workspace(context.WorkspaceId);
        var profiles = factory.Services.GetRequiredService<DataSourceProfileService>();
        var expected = new Dictionary<string, string>
        {
            ["web-builtin"] = KnowledgePlatformResourceProvisioner.WebAcquisitionFlowName,
            ["rest-builtin"] = KnowledgePlatformResourceProvisioner.RestAcquisitionFlowName,
            ["artifact-import-builtin"] = KnowledgePlatformResourceProvisioner.ArtifactImportAcquisitionFlowName
        };
        var before = new Dictionary<string, (Guid Uid, long Generation, string ETag)>();

        foreach (var item in expected)
        {
            var stored = await profiles.GetAsync(ResourceNamespace.Default, item.Key, scope, default);
            Assert.IsNotNull(stored);
            Assert.AreEqual(KnowledgePlatformResourceProvisioner.ProfileVersion, stored.Value.ActiveVersion);
            Assert.AreEqual(scope, stored.Value.ScopeRef);
            Assert.AreEqual(item.Value, stored.Value.Definition.AcquisitionFlow.Name);
            Assert.AreEqual(scope, stored.Value.Definition.AcquisitionFlow.ScopeRef);
            Assert.AreEqual("true", stored.Value.Metadata.Annotations[ResourceProvenanceAnnotations.BuiltIn]);
            Assert.AreEqual(ResourceOriginKind.BuiltIn, stored.Value.Governance.Origin.Kind);
            Assert.AreEqual(ResourceManagementAuthorityKind.Core, stored.Value.Governance.ManagedBy?.Kind);
            var revisions = await profiles.ListRevisionsAsync(ResourceNamespace.Default, item.Key, scope, default);
            Assert.HasCount(1, revisions);
            Assert.AreEqual(scope, revisions[0].Value.ScopeRef);
            Assert.AreEqual(scope, revisions[0].Value.AcquisitionFlow.ScopeRef);
            Assert.IsTrue(revisions[0].Value.ToolBindings.All(binding =>
                binding.ToolScopeRef == scope && binding.ProviderScopeRef == scope));
            Assert.IsNull(await profiles.GetAsync(ResourceNamespace.Default, item.Key, workspaceScope, default));
            before[item.Key] = (stored.Value.Uid, stored.Value.Generation, stored.ETag);
        }

        await factory.Services.GetRequiredService<IWorkspacePlatformResourceProvisioner>()
            .EnsureAsync(context.TenantId, context.WorkspaceId, default);

        foreach (var item in before)
        {
            var stored = await profiles.GetAsync(ResourceNamespace.Default, item.Key, scope, default);
            Assert.IsNotNull(stored);
            Assert.AreEqual(item.Value.Uid, stored.Value.Uid);
            Assert.AreEqual(item.Value.Generation, stored.Value.Generation);
            Assert.AreEqual(item.Value.ETag, stored.ETag);
        }

        var web = await profiles.GetAsync(ResourceNamespace.Default, "web-builtin", scope, default);
        var protectedMutation = await Assert.ThrowsAsync<DataSourceValidationException>(() => profiles.PutAsync(
            ResourceNamespace.Default, "web-builtin", scope,
            web!.Value.Definition with { Description = "Administrator mutation" }, web.ETag, default));
        Assert.AreEqual("data_source_profile_builtin_protected", protectedMutation.Code);

        using var client = factory.CreateClient();
        using var removedSurface = await client.GetAsync("/api/knowledgesourceprofiles");
        Assert.AreEqual(HttpStatusCode.NotFound, removedSurface.StatusCode);
    }

    [TestMethod]
    public async Task WebBuiltInDataSourceProfileProjectsAndSearchesEveryResource()
    {
        await using var factory = Factory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHttpContentFetcher>();
            services.AddSingleton<IHttpContentFetcher>(new StubHttpFetcher(request =>
            {
                var sourceName = request.Url.AbsolutePath.Contains("secondary", StringComparison.Ordinal)
                    ? "secondary" : "primary";
                var repetitions = sourceName == "primary" ? string.Join(' ', Enumerable.Repeat("shared", 12)) : "shared";
                return new(request.Url, $"{sourceName}.html", "text/html",
                    JsonSerializer.SerializeToUtf8Bytes($"<html><body>{sourceName} {repetitions}</body></html>"));
            }));
        }));
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var context = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        var scope = ResourceScopeRef.Workspace(context.WorkspaceId);
        var sourceManagement = factory.Services.GetRequiredService<DataSourceManagementService>();
        var acquisitions = factory.Services.GetRequiredService<DataSourceAcquisitionService>();
        async Task<(DataSourceResource Source, DataSourceAcquisitionResource Acquisition)> AcquireAsync(
            string name, string url)
        {
            var source = await sourceManagement.CreateAsync(new DataSourceResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = DataSourceResourceKinds.DataSource,
                Metadata = new() { Name = name },
                ScopeRef = scope,
                Definition = new()
                {
                    DisplayName = name,
                    Profile = new("web-builtin", ResourceScopeRef.Instance),
                    Configuration = JsonSerializer.SerializeToElement(new { url })
                }
            }, default);
            var started = await acquisitions.StartAsync(source.Value.Namespace, source.Value.Name, scope,
                JsonSerializer.SerializeToElement(new { }), name, name, default);
            var completed = started;
            for (var attempt = 0; attempt < 200
                && completed.Value.State is DataSourceAcquisitionState.Pending
                    or DataSourceAcquisitionState.Running
                    or DataSourceAcquisitionState.WaitingForChild;
                attempt++)
            {
                await Task.Delay(25);
                completed = await acquisitions.GetAsync(started.Value.Namespace, started.Value.Name, default);
            }
            Assert.AreEqual(DataSourceAcquisitionState.Succeeded, completed.Value.State, completed.Value.ErrorMessage);
            return (source.Value, completed.Value);
        }

        var primary = await AcquireAsync("web-primary", "https://example.test/docs/primary.html");
        var secondary = await AcquireAsync("web-secondary", "https://example.test/docs/secondary.html");
        var artifact = primary.Acquisition.Manifest?.Artifacts.Single();
        Assert.IsNotNull(artifact);
        Assert.AreEqual(DataSourceArtifactDisposition.Publishable, artifact.Disposition);
        Assert.AreEqual("web-builtin", primary.Acquisition.Composition.Profile.Name);
        Assert.AreEqual(DataSourceFlowContracts.Acquisition, primary.Acquisition.Composition.Flow.Contract);
        var durable = await factory.Services.GetRequiredService<ArtifactManagementService>()
            .GetFlowRunArtifactAsync(FlowRunArtifactId.Parse(artifact.ArtifactId), default);
        Assert.IsNotNull(durable);
        Assert.AreEqual("text/html", durable.Value.Receipt.MediaType);

        var knowledgeSource = await factory.Services.GetRequiredService<KnowledgeSourceManagementService>().CreateAsync(
            new KnowledgeSourceResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = KnowledgeResourceKinds.KnowledgeSource,
                Metadata = new() { Name = "web-knowledge" },
                ScopeRef = scope,
                Definition = new()
                {
                    DisplayName = "Web knowledge",
                    DataSources =
                    [
                        new KnowledgeDataSourceBinding
                        {
                            Name = "primary",
                            DataSource = new(primary.Source.Name, scope)
                        },
                        new KnowledgeDataSourceBinding
                        {
                            Name = "secondary",
                            DataSource = new(secondary.Source.Name, scope)
                        }
                    ],
                    ProjectionFlow = new() { Name = KnowledgePlatformResourceProvisioner.ProjectionFlowName },
                    RetrievalFlow = new() { Name = KnowledgePlatformResourceProvisioner.RetrievalFlowName }
                }
            }, default);

        using var client = factory.CreateClient();
        using var projectionResponse = await client.PostAsJsonAsync(
            $"/api/knowledgesources/{knowledgeSource.Value.Name}/projections",
            new StartKnowledgeProjectionRequest());
        Assert.AreEqual(HttpStatusCode.Created, projectionResponse.StatusCode,
            await projectionResponse.Content.ReadAsStringAsync());
        var projection = await projectionResponse.Content.ReadFromJsonAsync<KnowledgeProjectionResource>();
        Assert.IsNotNull(projection);
        Assert.AreEqual(KnowledgeProjectionState.Succeeded, projection.State, projection.ErrorMessage);
        Assert.IsNotNull(projection.SnapshotName);

        using var secondProjectionResponse = await client.PostAsJsonAsync(
            $"/api/knowledgesources/{knowledgeSource.Value.Name}/projections",
            new StartKnowledgeProjectionRequest());
        Assert.AreEqual(HttpStatusCode.Created, secondProjectionResponse.StatusCode,
            await secondProjectionResponse.Content.ReadAsStringAsync());
        var secondProjection = await secondProjectionResponse.Content.ReadFromJsonAsync<KnowledgeProjectionResource>();
        Assert.IsNotNull(secondProjection);
        Assert.AreEqual(KnowledgeProjectionState.Succeeded, secondProjection.State, secondProjection.ErrorMessage);
        Assert.IsNotNull(secondProjection.SnapshotName);
        Assert.AreNotEqual(projection.Name, secondProjection.Name);
        Assert.AreNotEqual(projection.SnapshotName, secondProjection.SnapshotName);

        var snapshotService = factory.Services.GetRequiredService<KnowledgeSnapshotService>();
        var publishedSnapshots = await snapshotService.ListAsync(new(knowledgeSource.Value.Name), default);
        Assert.HasCount(2, publishedSnapshots);

        using var searchResponse = await client.PostAsJsonAsync(
            $"/api/knowledgesources/{knowledgeSource.Value.Name}/search",
            new SearchKnowledgeRequest { Query = "shared", Limit = 10 });
        Assert.AreEqual(HttpStatusCode.OK, searchResponse.StatusCode,
            await searchResponse.Content.ReadAsStringAsync());
        var search = await searchResponse.Content.ReadFromJsonAsync<KnowledgeRetrievalResult>();
        Assert.IsNotNull(search);
        Assert.HasCount(10, search.Items);
        Assert.HasCount(2, search.Items.Select(item => item.ArtifactId).Distinct().ToArray());
    }

    [TestMethod]
    public async Task KnowledgeSourceProjectsDataSourcesAndRejectsTheRemovedAcquisitionShape()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        var scope = ResourceScopeRef.Workspace(context.WorkspaceId);
        var dataSource = await factory.Services.GetRequiredService<DataSourceManagementService>().CreateAsync(
            new DataSourceResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = DataSourceResourceKinds.DataSource,
                Metadata = new() { Name = "artifact-origin" },
                ScopeRef = scope,
                Definition = new()
                {
                    DisplayName = "Artifact origin",
                    Profile = new("artifact-import-builtin", ResourceScopeRef.Instance),
                    Configuration = JsonSerializer.SerializeToElement(new { artifactIds = new[] { "artifact-1" } })
                }
            }, default);
        var sources = factory.Services.GetRequiredService<KnowledgeSourceManagementService>();
        var created = await sources.CreateAsync(new KnowledgeSourceResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = KnowledgeResourceKinds.KnowledgeSource,
            Metadata = new() { Name = "projected-knowledge" },
            ScopeRef = scope,
            Definition = new()
            {
                DisplayName = "Projected knowledge",
                DataSources =
                [
                    new KnowledgeDataSourceBinding
                    {
                        Name = "origin",
                        DataSource = new(dataSource.Value.Name, scope)
                    }
                ],
                ProjectionFlow = new() { Name = KnowledgePlatformResourceProvisioner.ProjectionFlowName },
                RetrievalFlow = new() { Name = KnowledgePlatformResourceProvisioner.RetrievalFlowName }
            }
        }, default);
        var readiness = await sources.GetReadinessAsync(new(created.Value.Name), default);
        Assert.IsFalse(readiness.Ready);
        StringAssert.Contains(string.Join(' ', readiness.Issues), "has no successful acquisition with artifacts");
        Assert.HasCount(1, readiness.DataSources ?? []);
        Assert.AreEqual(KnowledgeFlowContracts.Projection, readiness.Projection?.Contract);
        Assert.AreEqual(KnowledgeFlowContracts.Retrieval, readiness.Retrieval?.Contract);

    }

    private sealed class StubHttpFetcher(Func<HttpFetchRequest, HttpFetchResult> fetch) : IHttpContentFetcher
    {
        public Task<HttpFetchResult> FetchAsync(HttpFetchRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(fetch(request));
    }
}
