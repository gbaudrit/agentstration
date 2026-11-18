using System.Net;
using System.Net.Http.Json;
using Agentstration.Identity.Contracts;
using Agentstration.Knowledge.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Storage.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class KnowledgeMetadataApiTests : ResourceManagementApiTestBase
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SnapshotAndStatusEndpointsDoNotContactTheContentProvider(bool instance)
    {
        var unavailable = new UnavailablePublications();
        await using var original = Factory();
        await using var factory = original.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Replace(ServiceDescriptor.Singleton<IStoragePublicationGateway>(unavailable))));
        var context = await GetBootstrapContextAsync(factory);
        if (instance)
        {
            var operations = await factory.Services.GetRequiredService<IOperationalTopologyProvisioner>().GetInstanceOperationsWorkspaceAsync(default);
            context = context with { TenantId = operations.TenantId, WorkspaceId = operations.Id };
        }
        var workspace = ResourceScopeRef.Workspace(context.WorkspaceId);
        var owner = instance ? ResourceScopeRef.Instance : workspace;
        var scopes = factory.Services.GetRequiredService<IRequestContextScopeFactory>();
        using var request = instance ? scopes.PushInstance(context) : scopes.Push(context);
        var store = factory.Services.GetRequiredService<IResourceStore>();
        var source = (await store.PutExactAsync(owner, new KnowledgeSourceResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = KnowledgeResourceKinds.KnowledgeSource,
            Metadata = new() { Name = "metadata-only" },
            ScopeRef = owner,
            Generation = 1,
            Definition = new() { DisplayName = "Metadata only" }
        }, null, true, default)).Value;
        var target = new StorageContentRequest(new(new(owner, ResourceNamespace.Default, "Storage", "unavailable"),
            Guid.NewGuid(), 1, "binding"), new(owner, source.Address), new());
        KnowledgeSnapshotResource active = null!;
        using (scopes.Push(context))
        {
            for (var index = 0; index < 3; index++)
            {
                active = (await store.CreateImmutableAsync(new KnowledgeSnapshotResource
                {
                    ApiVersion = ResourceApiVersions.CoreV1,
                    Kind = KnowledgeResourceKinds.KnowledgeSnapshot,
                    Metadata = new() { Name = "snapshot-" + index },
                    ScopeRef = workspace,
                    KnowledgeSourceUid = source.Uid,
                    KnowledgeSourceScopeRef = owner,
                    KnowledgeSourceName = source.Name,
                    KnowledgeSourceNamespace = source.Namespace,
                    KnowledgeSourceGeneration = source.Generation,
                    RequestHash = "request",
                    PublishedAt = DateTimeOffset.UnixEpoch.AddMinutes(index),
                    PublishedBy = context.PrincipalId,
                    ProjectionId = "projection-" + index,
                    ProjectionUid = Guid.NewGuid(),
                    ProjectionFlowRunId = "run-" + index,
                    ProjectionFlow = new("projection", ResourceNamespace.Default, "1", false, null, null),
                    Artifacts = Enumerable.Range(0, 256).Select(document => new KnowledgeSnapshotArtifact
                    {
                        ArtifactId = "document-" + document,
                        ProducerFlowRunId = "producer",
                        ProducerFlowStepId = "step",
                        StorageFlowRunId = "storage",
                        MediaType = "application/json",
                        Length = 2,
                        Sha256 = "digest",
                        Content = new(target, "document-" + document, "exact-version", 2, "digest")
                    }).ToArray()
                }, default)).Value;
            }
            _ = await store.PutExactAsync(workspace, new KnowledgeSnapshotObservedResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = KnowledgeResourceKinds.KnowledgeSnapshotObservedState,
                Metadata = new() { Name = "snapshot-state-" + source.Uid.ToString("N") },
                ScopeRef = workspace,
                KnowledgeSourceUid = source.Uid,
                ActiveSnapshotName = active.Name,
                ActiveSnapshotUid = active.Uid
            }, null, true, default);
        }
        using var client = factory.CreateClient();
        SelectConsoleContext(client, owner);
        var route = "/api/knowledgesources/" + source.Name;
        foreach (var suffix in new[] { "", "/projections", "/snapshots", "/snapshots/active" })
        {
            using var response = await client.GetAsync(route + suffix + "?scopeRef=" + Uri.EscapeDataString(owner.Value));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        }
        var detail = await client.GetFromJsonAsync<KnowledgeSnapshotView>("/api/knowledgesnapshots/" + active.Name);
        Assert.AreEqual(KnowledgeSnapshotLifecycleState.Active, detail!.LifecycleState);
        Assert.AreEqual(KnowledgeContentAvailability.Unknown, detail.ContentAvailability);
        Assert.HasCount(256, detail.Snapshot.Artifacts);
        using var statusResponse = await client.PostAsJsonAsync(route + "/content/status?scopeRef=" + Uri.EscapeDataString(owner.Value), new KnowledgeStatusRequest());
        Assert.AreEqual(HttpStatusCode.OK, statusResponse.StatusCode, await statusResponse.Content.ReadAsStringAsync());
        var status = await statusResponse.Content.ReadFromJsonAsync<KnowledgeAccessStatus>();
        Assert.AreEqual("ready", status!.State);
        Assert.AreEqual(KnowledgeContentAvailability.Unknown, status.ContentAvailability);
        Assert.AreEqual(0, unavailable.Calls);
    }

    private sealed class UnavailablePublications : IStoragePublicationGateway
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        private InvalidOperationException Unexpected()
        { Interlocked.Increment(ref calls); return new("Consultation must not contact the content provider."); }
        public Task ValidateAsync(StorageContentRequest target, CancellationToken ct) => throw Unexpected();
        public Task<StoragePublicationReference> PrepareAsync(StorageContentRequest target, string id, ReadOnlyMemory<byte> content, CancellationToken ct) => throw Unexpected();
        public Task SealAsync(StoragePublicationReference reference, CancellationToken ct) => throw Unexpected();
        public Task<ReadOnlyMemory<byte>> ReadAsync(StoragePublicationReference reference, long offset, int length, CancellationToken ct) => throw Unexpected();
    }
}
