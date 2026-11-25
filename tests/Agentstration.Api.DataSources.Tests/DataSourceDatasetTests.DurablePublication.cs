using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Artifacts;
using Agentstration.Artifacts.Contracts;
using Agentstration.DataSources;
using Agentstration.DataSources.Contracts;
using Agentstration.Flows;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure.Storage;
using Agentstration.Resources;
using Agentstration.Storage;
using Agentstration.Storage.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Management.Tests;

public sealed partial class DataSourceDatasetTests
{
    [TestMethod]
    public async Task DurablePublicationIsAnActiveFlowStepAndObservationCannotDriveOrCancelIt()
    {
        PublicationBarrier? publication = null;
        await using var factory = Factory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton<StoragePublicationGateway>();
            services.RemoveAll<IStoragePublicationGateway>();
            services.AddSingleton<IStoragePublicationGateway>(provider => publication = new(provider.GetRequiredService<StoragePublicationGateway>(), pause: true));
        }));
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var fixture = await PrepareAsync(factory.Services);
        var scopes = factory.Services.GetRequiredService<IRequestContextScopeFactory>();
        using var scope = scopes.PushInstance(fixture.Context);
        var source = await factory.Services.GetRequiredService<DataSourceManagementService>().CreateAsync(
            Source("durable-delay", fixture, JsonSerializer.SerializeToElement(new { dataset = "durable" })), default);
        using var client = factory.CreateClient(); SelectConsoleContext(client, Owner);
        using var accepted = await client.PostAsJsonAsync($"/api/datasources/{source.Value.Name}/acquisitions?scopeRef=/instance", new StartDataSourceAcquisitionRequest());
        Assert.AreEqual(HttpStatusCode.Accepted, accepted.StatusCode, await accepted.Content.ReadAsStringAsync());
        var acquisition = (await accepted.Content.ReadFromJsonAsync<DataSourceAcquisitionResource>())!;
        Assert.IsTrue(acquisition.Dataset!.DurablePublication);
        await publication!.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            for (var index = 0; index < 3; index++)
            {
                var detail = (await client.GetFromJsonAsync<DataSourceAcquisitionResource>($"/api/datasourceacquisitions/{acquisition.Name}"))!;
                Assert.AreEqual(DataSourceAcquisitionState.Running, detail.State);
                Assert.AreEqual("publication-seal", detail.Progress!.StepName);
                Assert.AreEqual("Active", detail.Progress.State);
                Assert.IsNotNull(detail.Publication);
                Assert.IsNull(detail.PublicationSealedAt);
                Assert.IsNull(detail.Manifest);
                var history = (await client.GetFromJsonAsync<DataSourceAcquisitionResource[]>($"/api/datasources/{source.Value.Name}/acquisitions?scopeRef=/instance"))!;
                Assert.AreEqual(detail.Progress, history.Single().Progress);
            }
            Assert.AreEqual(1, publication.Prepares);
            Assert.AreEqual(1, publication.Seals);
            using (scopes.Push(fixture.Context))
            {
                var root = (await factory.Services.GetRequiredService<IFlowRepository>().GetRunExactAsync(acquisition.ScopeRef!.Value, acquisition.FlowRunId, default))!.Value;
                Assert.AreEqual(FlowRunStatus.Running, root.Status);
                Assert.AreEqual(FlowStepRunStatus.Succeeded, root.Steps.Single(step => step.StepName == "transformations").Status);
                Assert.AreEqual(FlowStepRunStatus.Running, root.Steps.Single(step => step.StepName == "publication-seal").Status);
                Assert.AreEqual(FlowStepRunStatus.NotStarted, root.Steps.Single(step => step.StepName == "publication-register").Status);
            }
            using var cancelledObserver = new CancellationTokenSource(); cancelledObserver.Cancel();
            await Assert.ThrowsAsync<TaskCanceledException>(() => client.GetAsync($"/api/datasourceacquisitions/{acquisition.Name}", cancelledObserver.Token));
        }
        finally { publication.Release.TrySetResult(); }
        var completed = await WaitAsync(client, acquisition.Name);
        Assert.AreEqual("publication-register", completed.Progress!.StepName);
        Assert.AreEqual("Succeeded", completed.Progress.State);
        Assert.AreEqual(1, completed.Progress.Attempt);
        Assert.IsNotNull(completed.PublicationSealedAt);
        Assert.HasCount(1, completed.Manifest!.Artifacts);
        Assert.AreEqual(1, publication.Prepares);
        Assert.AreEqual(1, publication.Seals);
        Assert.AreEqual("durable", await ReadDatasetAsync(client, acquisition.Name));
    }

    [TestMethod]
    [DataRow("prepare")]
    [DataRow("seal")]
    [DataRow("register")]
    public async Task DurablePublicationRecoversLostProviderOrArtifactAcknowledgementWithoutDuplicateOutput(string interrupt)
    {
        PublicationBarrier? publication = null;
        RegistrationInterruption? registrar = null;
        await using var factory = Factory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton<StoragePublicationGateway>();
            services.RemoveAll<IStoragePublicationGateway>();
            services.AddSingleton<IStoragePublicationGateway>(provider => publication = new(provider.GetRequiredService<StoragePublicationGateway>(), interrupt: interrupt));
            services.RemoveAll<IStoredArtifactRegistrar>();
            services.AddSingleton<IStoredArtifactRegistrar>(provider => registrar = new(provider.GetRequiredService<ArtifactManagementService>(), interrupt == "register"));
        }));
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var fixture = await PrepareAsync(factory.Services);
        using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(fixture.Context);
        var source = await factory.Services.GetRequiredService<DataSourceManagementService>().CreateAsync(
            Source("durable-retry", fixture, JsonSerializer.SerializeToElement(new { dataset = "retained" })), default);
        using var client = factory.CreateClient(); SelectConsoleContext(client, Owner);
        var acquired = await AcquireAsync(client, source.Value.Name);
        Assert.AreEqual(interrupt == "prepare" ? 2 : 1, publication!.Prepares);
        Assert.AreEqual(interrupt == "seal" ? 2 : 1, publication.Seals);
        Assert.AreEqual(interrupt == "register" ? 2 : 1, registrar!.Calls);
        Assert.HasCount(1, acquired.Manifest!.Artifacts);
        Assert.HasCount(1, acquired.Publication!.Records!.Members);
        using var workspace = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(fixture.Context);
        var root = (await factory.Services.GetRequiredService<IFlowRepository>().GetRunExactAsync(acquired.ScopeRef!.Value, acquired.FlowRunId, default))!.Value;
        Assert.AreEqual(FlowRunStatus.Succeeded, root.Status);
        Assert.AreEqual(1, root.Steps.Count(step => step.ChildFlowRunId is not null));
        Assert.IsTrue(root.Steps.Where(step => step.StepType == "tool" && step.StepName.StartsWith("publication-", StringComparison.Ordinal)).All(step => step.Status == FlowStepRunStatus.Succeeded));
        Assert.AreEqual(2, root.Steps.Single(step => step.StepName == "publication-" + interrupt).OperationProgress!.Attempt);
    }

    [TestMethod]
    public async Task PublicationRechecksRevocationAndRejectsForeignOrForgedExecutionIdentities()
    {
        PublicationBarrier? publication = null;
        await using var factory = Factory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton<StoragePublicationGateway>();
            services.RemoveAll<IStoragePublicationGateway>();
            services.AddSingleton<IStoragePublicationGateway>(provider => publication = new(provider.GetRequiredService<StoragePublicationGateway>(), pause: true));
        }));
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var fixture = await PrepareAsync(factory.Services);
        var scopes = factory.Services.GetRequiredService<IRequestContextScopeFactory>();
        using var scope = scopes.PushInstance(fixture.Context);
        var sources = factory.Services.GetRequiredService<DataSourceManagementService>();
        var source = await sources.CreateAsync(Source("revoked-publication", fixture, JsonSerializer.SerializeToElement(new { dataset = "private" })), default);
        var accepted = await factory.Services.GetRequiredService<DataSourceAcquisitionService>().StartAsync(source.Value.Namespace, source.Value.Name, Owner, Empty, null, null, default);
        await publication!.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var execution = factory.Services.GetRequiredService<DataSourceDatasetAcquisitionService>();
        try
        {
            var forged = await Assert.ThrowsAsync<DataSourceAcquisitionException>(() => execution.ExecutePublicationStepAsync(accepted.Value.Name, "foreign-run", "register", default));
            Assert.AreEqual("data_source_invocation_invalid", forged.Code);
            using (scopes.Push(await factory.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default)))
                await Assert.ThrowsAsync<DataSourceAcquisitionException>(() => execution.ExecutePublicationStepAsync(accepted.Value.Name, accepted.Value.FlowRunId, "register", default));
            using (scopes.PushInstance(fixture.Context with { Restriction = new(Guid.NewGuid(), fixture.Context.WorkspaceId, new HashSet<string> { AuthorizationPermissions.RunsRead }) }))
                await Assert.ThrowsAsync<AuthorizationDeniedException>(() => execution.ExecutePublicationStepAsync(accepted.Value.Name, accepted.Value.FlowRunId, "register", default));
            _ = await sources.PutAsync(source.Value.Namespace, source.Value.Name, Owner, source.Value.Definition with { Enabled = false }, source.ETag, default);
        }
        finally { publication.Release.TrySetResult(); }
        using var client = factory.CreateClient(); SelectConsoleContext(client, Owner);
        DataSourceAcquisitionResource? failed = null;
        for (var attempt = 0; attempt < 300; attempt++)
        {
            failed = await client.GetFromJsonAsync<DataSourceAcquisitionResource>($"/api/datasourceacquisitions/{accepted.Value.Name}");
            if (failed!.State == DataSourceAcquisitionState.Failed) break;
            await Task.Delay(20);
        }
        Assert.AreEqual(DataSourceAcquisitionState.Failed, failed!.State);
        Assert.IsNull(failed.Manifest);
        Assert.IsNotNull(failed.Publication);
    }

    [TestMethod]
    public async Task ExplicitPublicationRetryRetainsAcquisitionRootAndSuccessfulTransformations()
    {
        PublicationBarrier? publication = null;
        await using var factory = Factory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton<StoragePublicationGateway>();
            services.RemoveAll<IStoragePublicationGateway>();
            services.AddSingleton<IStoragePublicationGateway>(provider => publication = new(provider.GetRequiredService<StoragePublicationGateway>(), interrupt: "refuse-seal"));
        }));
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var fixture = await PrepareAsync(factory.Services);
        var scopes = factory.Services.GetRequiredService<IRequestContextScopeFactory>();
        using var scope = scopes.PushInstance(fixture.Context);
        var source = await factory.Services.GetRequiredService<DataSourceManagementService>().CreateAsync(Source("resume-publication", fixture, JsonSerializer.SerializeToElement(new { dataset = "retained" })), default);
        var acquisitions = factory.Services.GetRequiredService<DataSourceAcquisitionService>();
        var accepted = await acquisitions.StartAsync(source.Value.Namespace, source.Value.Name, Owner, Empty, null, null, default);
        using var client = factory.CreateClient(); SelectConsoleContext(client, Owner);
        DataSourceAcquisitionResource? failed = null;
        for (var attempt = 0; attempt < 300; attempt++)
        {
            failed = await client.GetFromJsonAsync<DataSourceAcquisitionResource>($"/api/datasourceacquisitions/{accepted.Value.Name}");
            if (failed!.State == DataSourceAcquisitionState.Failed) break;
            await Task.Delay(20);
        }
        Assert.AreEqual(DataSourceAcquisitionState.Failed, failed!.State);
        Assert.IsNotNull(failed.Publication);
        var repository = factory.Services.GetRequiredService<IFlowRepository>();
        FlowRun original;
        using (scopes.Push(fixture.Context)) original = (await repository.GetRunExactAsync(failed.ScopeRef!.Value, failed.FlowRunId, default))!.Value;
        _ = await acquisitions.RetryAsync(failed.Namespace, failed.Name, null, default);
        var completed = await WaitAsync(client, failed.Name);
        Assert.AreEqual(failed.Uid, completed.Uid);
        Assert.AreEqual(failed.FlowRunId, completed.FlowRunId);
        Assert.AreEqual(1, publication!.Prepares);
        Assert.AreEqual(2, publication.Seals);
        using (scopes.Push(fixture.Context))
        {
            var recovered = (await repository.GetRunExactAsync(failed.ScopeRef!.Value, failed.FlowRunId, default))!.Value;
            Assert.IsTrue(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(original.Steps.Single(step => step.StepName == "transformations")), JsonSerializer.SerializeToElement(recovered.Steps.Single(step => step.StepName == "transformations"))));
            CollectionAssert.AreEqual(original.Steps.SelectMany(step => step.ChildFlowRunIds).ToArray(), recovered.Steps.SelectMany(step => step.ChildFlowRunIds).ToArray());
            Assert.AreEqual(FlowRunStatus.Succeeded, recovered.Status);
        }
        Agentstration.ResourceManagement.StoredResource<DataSourceAcquisitionResource> missingManifest;
        using (scopes.Push(fixture.Context))
        {
            var resources = factory.Services.GetRequiredService<Agentstration.ResourceManagement.IResourceStore>();
            var record = (await resources.GetExactAsync<DataSourceAcquisitionResource>(new(completed.ScopeRef!.Value, completed.Namespace, completed.Kind, completed.Name), default))!;
            missingManifest = await resources.PutExactAsync(record.Value.ScopeRef!.Value,
                record.Value with { Generation = record.Value.Generation + 1, Manifest = null }, record.ETag, false, default);
        }
        Assert.IsNull((await acquisitions.GetAsync(completed.Namespace, completed.Name, default)).Value.Manifest,
            "Terminal history observation must not repair Artifact metadata.");
        var repaired = await factory.Services.GetRequiredService<DataSourceDatasetAcquisitionService>().ReconcileArtifactAsync(missingManifest, default);
        Assert.AreEqual(completed.Manifest!.Artifacts.Single().ArtifactId, repaired.Value.Manifest!.Artifacts.Single().ArtifactId);
        Assert.AreEqual(1, publication.Prepares);
        Assert.AreEqual(2, publication.Seals);
    }

    private sealed class RegistrationInterruption(IStoredArtifactRegistrar inner, bool interrupt) : IStoredArtifactRegistrar
    {
        public int Calls { get; private set; }
        public async Task<FlowRunArtifactResource> RegisterStoredAsync(RegisterStoredArtifactRequest request, CancellationToken token)
        {
            Calls++;
            var result = await inner.RegisterStoredAsync(request, token);
            if (interrupt && Calls == 1) throw new StorageException("storage_publication_unavailable", "Injected lost acknowledgement.", 503);
            return result;
        }
    }

    private sealed class PublicationBarrier(IStoragePublicationGateway inner, bool pause = false, string? interrupt = null) : IStoragePublicationGateway
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Prepares { get; private set; }
        public int Seals { get; private set; }
        public Task ValidateAsync(StorageContentRequest target, CancellationToken token) => inner.ValidateAsync(target, token);
        public Task<StoragePublicationReference> PrepareAsync(StorageContentRequest target, string id, ReadOnlyMemory<byte> content, CancellationToken token) => inner.PrepareAsync(target, id, content, token);
        public Task<ReadOnlyMemory<byte>> ReadAsync(StoragePublicationReference reference, long offset, int length, CancellationToken token) => inner.ReadAsync(reference, offset, length, token);
        public Task<bool> RetireAsync(StoragePublicationReference reference, CancellationToken token) => inner.RetireAsync(reference, token);
        public async Task<StoragePublicationReference> PrepareRecordsAsync(StorageContentRequest target, string id, IReadOnlyList<JsonElement> records, CancellationToken token, DateTimeOffset? notAfter = null)
        {
            Prepares++;
            var result = await inner.PrepareRecordsAsync(target, id, records, token, notAfter);
            if (interrupt == "prepare" && Prepares == 1) throw new StorageException("storage_publication_unavailable", "Injected lost acknowledgement.", 503);
            return result;
        }
        public async Task SealAsync(StoragePublicationReference reference, CancellationToken token)
        {
            Seals++;
            Entered.TrySetResult();
            if (interrupt == "refuse-seal" && Seals == 1) throw new StorageException("fixture_publication_refused", "Injected publication refusal.");
            if (pause) await Release.Task.WaitAsync(token);
            await inner.SealAsync(reference, token);
            if (interrupt == "seal" && Seals == 1) throw new StorageException("storage_publication_unavailable", "Injected lost acknowledgement.", 503);
        }
    }
}
