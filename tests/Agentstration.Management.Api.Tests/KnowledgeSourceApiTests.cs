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
using Agentstration.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class KnowledgeSourceApiTests : ModelManagementApiTestBase
{
    [TestMethod]
    public async Task WorkspacesReceiveProtectedPublishedBuiltInDataSourceProfilesIdempotently()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        var scope = ResourceScopeRef.Workspace(context.WorkspaceId);
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
            Assert.AreEqual(item.Value, stored.Value.Definition.AcquisitionFlow.Name);
            Assert.AreEqual("true", stored.Value.Metadata.Annotations[ResourceProvenanceAnnotations.BuiltIn]);
            var revisions = await profiles.ListRevisionsAsync(ResourceNamespace.Default, item.Key, scope, default);
            Assert.HasCount(1, revisions);
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
        var context = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        var scope = ResourceScopeRef.Workspace(context.WorkspaceId);
        var sourceManagement = factory.Services.GetRequiredService<DataSourceManagementService>();
        var acquisitions = factory.Services.GetRequiredService<DataSourceAcquisitionService>();
        var runs = factory.Services.GetRequiredService<FlowRunService>();
        var executionScope = new FlowRunScope(context.TenantId, new WorkspaceId(context.WorkspaceId), context.PrincipalId);

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
                    Profile = new("web-builtin", scope),
                    Configuration = JsonSerializer.SerializeToElement(new { url })
                }
            }, default);
            var started = await acquisitions.StartAsync(source.Value.Namespace, source.Value.Name, scope,
                JsonSerializer.SerializeToElement(new { }), name, name, default);
            await runs.ExecuteAsync(new(started.Value.FlowRunId, executionScope), default);
            var parent = await runs.GetAsync(executionScope.WorkspaceId, started.Value.FlowRunId, default);
            var childRunId = parent!.Value.Steps.Single(step => step.StepName == "persist").ChildFlowRunId;
            Assert.IsNotNull(childRunId);
            await runs.ExecuteAsync(new(childRunId, executionScope), default);
            await runs.ExecuteAsync(new(started.Value.FlowRunId, executionScope), default);
            var completed = await acquisitions.GetAsync(started.Value.Namespace, started.Value.Name, default);
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
                    Profile = new("artifact-import-builtin", scope),
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
