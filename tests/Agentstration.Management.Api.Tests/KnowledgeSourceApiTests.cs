using System.Net;
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
    public async Task WebBuiltInDataSourceProfileAcquiresAndPersistsOneResource()
    {
        await using var factory = Factory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHttpContentFetcher>();
            services.AddSingleton<IHttpContentFetcher>(new StubHttpFetcher(
                new HttpFetchResult(new("https://example.test/docs/index.html"), "index.html", "text/html",
                    "<html><body>Agentstration data source</body></html>"u8.ToArray())));
        }));
        var context = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        var scope = ResourceScopeRef.Workspace(context.WorkspaceId);
        var source = await factory.Services.GetRequiredService<DataSourceManagementService>().CreateAsync(
            new DataSourceResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = DataSourceResourceKinds.DataSource,
                Metadata = new() { Name = "web-origin" },
                ScopeRef = scope,
                Definition = new()
                {
                    DisplayName = "Web origin",
                    Profile = new("web-builtin", scope),
                    Configuration = JsonSerializer.SerializeToElement(new
                    {
                        url = "https://example.test/docs/index.html"
                    })
                }
            }, default);
        var acquisitions = factory.Services.GetRequiredService<DataSourceAcquisitionService>();
        var started = await acquisitions.StartAsync(source.Value.Namespace, source.Value.Name, scope,
            JsonSerializer.SerializeToElement(new { }), "web-origin", "web-origin", default);
        var runs = factory.Services.GetRequiredService<FlowRunService>();
        var executionScope = new FlowRunScope(context.TenantId, new WorkspaceId(context.WorkspaceId), context.PrincipalId);

        await runs.ExecuteAsync(new(started.Value.FlowRunId, executionScope), default);
        var parent = await runs.GetAsync(executionScope.WorkspaceId, started.Value.FlowRunId, default);
        var childRunId = parent!.Value.Steps.Single(step => step.StepName == "persist").ChildFlowRunId;
        Assert.IsNotNull(childRunId);
        await runs.ExecuteAsync(new(childRunId, executionScope), default);
        await runs.ExecuteAsync(new(started.Value.FlowRunId, executionScope), default);

        var completed = await acquisitions.GetAsync(started.Value.Namespace, started.Value.Name, default);
        Assert.AreEqual(DataSourceAcquisitionState.Succeeded, completed.Value.State, completed.Value.ErrorMessage);
        var artifact = completed.Value.Manifest?.Artifacts.Single();
        Assert.IsNotNull(artifact);
        Assert.AreEqual(DataSourceArtifactDisposition.Publishable, artifact.Disposition);
        Assert.AreEqual("web-builtin", completed.Value.Composition.Profile.Name);
        Assert.AreEqual(DataSourceFlowContracts.Acquisition, completed.Value.Composition.Flow.Contract);
        var durable = await factory.Services.GetRequiredService<ArtifactManagementService>()
            .GetFlowRunArtifactAsync(FlowRunArtifactId.Parse(artifact.ArtifactId), default);
        Assert.IsNotNull(durable);
        Assert.AreEqual("text/html", durable.Value.Receipt.MediaType);
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

        var removedShape = new KnowledgeSourceResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = KnowledgeResourceKinds.KnowledgeSource,
            Metadata = new() { Name = "legacy-direct-acquisition" },
            ScopeRef = scope,
            Definition = new()
            {
                DisplayName = "Legacy direct acquisition",
                Enabled = false,
                Profile = new("web-builtin"),
                AcquisitionConfiguration = JsonSerializer.SerializeToElement(new { url = "https://example.test" }),
                IngestionFlow = new() { Name = KnowledgePlatformResourceProvisioner.WebAcquisitionFlowName }
            }
        };
        var rejected = await Assert.ThrowsAsync<KnowledgeSourceValidationException>(() =>
            sources.CreateAsync(removedShape, default));
        Assert.AreEqual("knowledge_source_data_sources_invalid", rejected.Code);
    }

    private sealed class StubHttpFetcher(HttpFetchResult result) : IHttpContentFetcher
    {
        public Task<HttpFetchResult> FetchAsync(HttpFetchRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }
}
