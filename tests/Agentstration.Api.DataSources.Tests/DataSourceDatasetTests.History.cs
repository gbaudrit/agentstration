using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.DataSources;
using Agentstration.DataSources.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Management.Tests;

public sealed partial class DataSourceDatasetTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NativeAcquisitionHistoryDoesNotFinalizeOrRepairPublications(bool filterBySource)
    {
        var flow = new HistoryFlowGateway();
        await using var factory = Factory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IDataSourceAcquisitionFlowGateway>();
            services.AddSingleton<IDataSourceAcquisitionFlowGateway>(flow);
        }));
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var fixture = await PrepareAsync(factory.Services);
        var scopes = factory.Services.GetRequiredService<IRequestContextScopeFactory>();
        using var scope = scopes.PushInstance(fixture.Context);
        var source = (await factory.Services.GetRequiredService<DataSourceManagementService>().CreateAsync(
            Source("history", fixture, JsonSerializer.SerializeToElement(new { dataset = "history" })), default)).Value;
        var acquisitions = factory.Services.GetRequiredService<DataSourceAcquisitionService>();
        var accepted = await acquisitions.StartAsync(source.Namespace, source.Name, Owner, Empty, null, null, default);
        flow.Completed = true;
        flow.RefuseReads = true;
        var reads = flow.Reads;
        using var client = factory.CreateClient();
        SelectConsoleContext(client, Owner);
        var path = filterBySource ? $"/api/datasources/{source.Name}/acquisitions?scopeRef=/instance" : "/api/datasourceacquisitions";
        using (var response = await client.GetAsync(path))
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            var history = (await response.Content.ReadFromJsonAsync<DataSourceAcquisitionResource[]>())!;
            var item = history.Single(value => value.Uid == accepted.Value.Uid);
            Assert.AreEqual(accepted.Value.State, item.State, "A completed Flow is not a completed Storage publication.");
            Assert.AreEqual(accepted.Value.Generation, item.Generation);
            Assert.IsNull(item.Publication);
        }
        Assert.AreEqual(reads, flow.Reads, "History must not enter Flow synchronization or publication.");

        // The durable recovery path, with its own lifetime, still completes the accepted publication.
        flow.RefuseReads = false;
        using (scopes.PushSystem()) await acquisitions.RecoverPendingAsync(default);
        var recovered = (await client.GetFromJsonAsync<DataSourceAcquisitionResource[]>(path))!
            .Single(value => value.Uid == accepted.Value.Uid);
        Assert.AreEqual(DataSourceAcquisitionState.Succeeded, recovered.State, recovered.ErrorCode);
        Assert.IsNotNull(recovered.Publication);
        Assert.HasCount(1, recovered.Manifest!.Artifacts);
        Assert.AreEqual(1, flow.Starts, "Refreshing history must never rerun the acquisition.");
        Assert.AreEqual("history", await ReadDatasetAsync(client, recovered.Name));

        // Older successful records awaiting catalog repair also remain read-only in history.
        var store = factory.Services.GetRequiredService<IResourceStore>();
        StoredResource<DataSourceAcquisitionResource> legacy;
        using (scopes.Push(fixture.Context))
        {
            var address = new ScopedResourceAddress(recovered.ScopeRef!.Value, recovered.Namespace, recovered.Kind, recovered.Name);
            var stored = (await store.GetExactAsync<DataSourceAcquisitionResource>(address, default))!;
            legacy = await store.PutExactAsync(stored.Value.ScopeRef!.Value,
                stored.Value with { Generation = stored.Value.Generation + 1, Manifest = null }, stored.ETag, false, default);
        }
        var unchanged = (await client.GetFromJsonAsync<DataSourceAcquisitionResource[]>(path))!
            .Single(value => value.Uid == legacy.Value.Uid);
        Assert.IsNull(unchanged.Manifest);
        Assert.AreEqual(legacy.Value.Generation, unchanged.Generation);
    }

    private sealed class HistoryFlowGateway : IDataSourceAcquisitionFlowGateway
    {
        private DataSourceFlowRunSnapshot? snapshot;
        public bool Completed { get; set; }
        public bool RefuseReads { get; set; }
        public int Reads { get; private set; }
        public int Starts { get; private set; }

        public Task<DataSourceFlowRunSnapshot> StartAsync(DataSourceFlowRunRequest request, CancellationToken cancellationToken)
        {
            Starts++;
            snapshot = new(request.RunId, DataSourceAcquisitionState.Running, null, null, null, null);
            return Task.FromResult(snapshot);
        }

        public Task<DataSourceFlowRunSnapshot?> GetAsync(Guid workspaceId, string runId, Guid tenantId, Guid principalId,
            CancellationToken cancellationToken)
        {
            Reads++;
            if (RefuseReads) throw new InvalidOperationException("History attempted publication synchronization.");
            return Task.FromResult(snapshot is null || !Completed ? snapshot : snapshot with
            {
                State = DataSourceAcquisitionState.Succeeded,
                Output = JsonSerializer.SerializeToElement(new { dataset = "history" })
            });
        }

        public Task<DataSourceFlowRunSnapshot> CancelAsync(Guid workspaceId, string runId, Guid tenantId, Guid principalId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
