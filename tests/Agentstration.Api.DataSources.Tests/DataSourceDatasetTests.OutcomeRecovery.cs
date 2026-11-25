using System.Text.Json;
using Agentstration.DataSources;
using Agentstration.DataSources.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure.Flows;
using Agentstration.Infrastructure.Storage;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Storage.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Agentstration.Management.Tests;

public sealed partial class DataSourceDatasetTests
{
    [TestMethod]
    public async Task ObservationWaitsForPersistedCompletionAndRecoveryRecordsItWithoutPublishingAgain()
    {
        var clock = new NativeAccessClock(TimeProvider.System.GetUtcNow());
        PublicationBarrier? publication = null;
        await using var factory = Factory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton(p => ActivatorUtilities.CreateInstance<DataSourceDatasetAcquisitionService>(p, clock));
            services.AddSingleton<StoragePublicationGateway>();
            services.RemoveAll<IStoragePublicationGateway>();
            services.AddSingleton<IStoragePublicationGateway>(p => publication = new(p.GetRequiredService<StoragePublicationGateway>()));
            foreach (var hosted in services.Where(value => value.ServiceType == typeof(IHostedService)
                && value.ImplementationType == typeof(DataSourceAcquisitionRecoveryWorker)).ToArray()) services.Remove(hosted);
        }));
        var scopes = factory.Services.GetRequiredService<IRequestContextScopeFactory>();
        Fixture fixture;
        DataSourceAcquisitionResource completed;
        await using (var worker = await AwpTestWorker.StartAsync(factory))
        {
            fixture = await PrepareAsync(factory.Services);
            using var caller = scopes.PushInstance(fixture.Context);
            var source = await factory.Services.GetRequiredService<DataSourceManagementService>().CreateAsync(
                Source("completion-recovery", fixture, JsonSerializer.SerializeToElement(new { dataset = "retained" })), default);
            using var client = factory.CreateClient();
            SelectConsoleContext(client, Owner);
            completed = await AcquireAsync(client, source.Value.Name);
        }
        using var scope = scopes.PushInstance(fixture.Context);
        clock.Now = clock.Now.AddHours(2);
        var resources = factory.Services.GetRequiredService<IResourceStore>();
        var address = new ScopedResourceAddress(completed.ScopeRef!.Value, completed.Namespace, completed.Kind, completed.Name);
        StoredResource<DataSourceAcquisitionResource> interrupted;
        using (scopes.Push(fixture.Context))
        {
            var stored = (await resources.GetExactAsync<DataSourceAcquisitionResource>(address, default))!;
            interrupted = await resources.PutExactAsync(address.ScopeRef, stored.Value with
            {
                Generation = stored.Value.Generation + 1,
                State = DataSourceAcquisitionState.Running,
                CompletedAt = null,
                Progress = null,
                Status = new() { ProvisioningState = ProvisioningState.Accepted }
            }, stored.ETag, false, default);
        }
        var acquisitions = factory.Services.GetRequiredService<DataSourceAcquisitionService>();
        var observed = await acquisitions.GetAsync(completed.Namespace, completed.Name, default);
        Assert.AreEqual(DataSourceAcquisitionState.Running, observed.Value.State);
        Assert.IsNull(observed.Value.CompletedAt);
        Assert.AreEqual("Succeeded", observed.Value.Progress!.State);
        Assert.AreEqual(interrupted.ETag, observed.ETag);
        using (scopes.PushSystem()) await acquisitions.RecoverPendingAsync(default);
        var recovered = (await acquisitions.GetAsync(completed.Namespace, completed.Name, default)).Value;
        Assert.AreEqual(DataSourceAcquisitionState.Succeeded, recovered.State);
        Assert.AreEqual(ProvisioningState.Succeeded, recovered.Status.ProvisioningState);
        Assert.AreEqual(completed.CompletedAt, recovered.CompletedAt);
        Assert.AreEqual(completed.Progress!.CompletedAt, recovered.Progress!.CompletedAt);
        Assert.AreEqual(completed.FlowRunId, recovered.FlowRunId);
        Assert.AreEqual(completed.Manifest!.Artifacts.Single().ArtifactId, recovered.Manifest!.Artifacts.Single().ArtifactId);
        Assert.IsTrue(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(completed.Publication), JsonSerializer.SerializeToElement(recovered.Publication)));
        Assert.AreEqual(1, publication!.Prepares);
        Assert.AreEqual(1, publication.Seals);
    }
}
