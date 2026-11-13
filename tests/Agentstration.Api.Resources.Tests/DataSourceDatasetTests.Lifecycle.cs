using System.Text.Json;
using Agentstration.DataSources;
using Agentstration.DataSources.Contracts;
using Agentstration.Flows;
using Agentstration.Flows.Storage.Sqlite;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure.Flows;
using Agentstration.Infrastructure.Knowledge;
using Agentstration.Infrastructure.Storage;
using Agentstration.Knowledge;
using Agentstration.Knowledge.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Storage.Sqlite;
using Agentstration.Storage;
using Agentstration.Storage.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Management.Tests;

public sealed partial class DataSourceDatasetTests
{
    private static readonly JsonSerializerOptions LifecycleJson = new(JsonSerializerDefaults.Web);
    [TestMethod]
    public async Task SourceTtlRefreshesBeforeFallbackAndRejectsAnOverAgeFallback()
    {
        var clock = new NativeAccessClock(TimeProvider.System.GetUtcNow());
        ControlledContent? controlled = null;
        await using var factory = Factory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton(p => ActivatorUtilities.CreateInstance<LocalStorageObjectProvider>(p, clock));
            services.AddSingleton<IStorageContentGateway>(p => controlled = new(ActivatorUtilities.CreateInstance<StorageContentGateway>(p, clock)));
            services.AddSingleton<IStoragePublicationGateway>(p => ActivatorUtilities.CreateInstance<LocalStoragePublicationGateway>(p, clock));
            services.AddSingleton(p => ActivatorUtilities.CreateInstance<DataSourceDatasetAcquisitionService>(p, clock));
            services.AddSingleton<IKnowledgeProjectionInputResolver>(p => ActivatorUtilities.CreateInstance<KnowledgeProjectionInputResolver>(p, clock));
            services.AddSingleton(p => ActivatorUtilities.CreateInstance<KnowledgeProjectionService>(p, clock));
            services.AddSingleton(p => ActivatorUtilities.CreateInstance<KnowledgeAccessService>(p, clock));
            services.AddSingleton(p => ActivatorUtilities.CreateInstance<SourceHistoryMaintenance>(p, clock));
        }));
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var fixture = await PrepareAsync(factory.Services);
        using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(fixture.Context);
        var sources = factory.Services.GetRequiredService<DataSourceManagementService>();
        var definition = Source("ttl-input", fixture, JsonSerializer.SerializeToElement(new { dataset = "retained" }));
        var source = await sources.CreateAsync(definition with
        {
            Definition = definition.Definition with
            { Dataset = definition.Definition.Dataset! with { Lifecycle = new() { TimeToLive = TimeSpan.FromHours(1) } } }
        }, default);
        var management = factory.Services.GetRequiredService<KnowledgeSourceManagementService>();
        var knowledge = await management.CreateAsync(new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = KnowledgeResourceKinds.KnowledgeSource,
            Metadata = new() { Name = "ttl-knowledge" },
            ScopeRef = Owner,
            Definition = new()
            {
                DisplayName = "TTL knowledge",
                Output = new(fixture.Storage, new(Write: true)),
                Lifecycle = new() { TimeToLive = TimeSpan.FromMinutes(20), KeepHistory = true, HistoryRetention = TimeSpan.FromDays(1) },
                ProjectionFlow = new() { Name = "knowledge-projection-builtin", ScopeRef = Owner },
                DataSources = [new() { Name = "input", DataSource = new(source.Value.Name, Owner) }]
            }
        }, default);
        var id = new KnowledgeSourceId(knowledge.Value.Name, ScopeRef: Owner);
        var projections = factory.Services.GetRequiredService<KnowledgeProjectionService>();
        var access = factory.Services.GetRequiredService<KnowledgeAccessService>();
        var first = await projections.StartAsync(id, new(), default);
        var firstInput = first.Value.Inputs.Single();
        Assert.AreEqual(clock.Now, firstInput.AcquiredAt);
        clock.Now += TimeSpan.FromMinutes(21);
        Assert.IsTrue((await access.GetStatusAsync(id, new(), default)).PreparationRequired);
        var second = await projections.StartAsync(id, new(), default);
        Assert.AreEqual(firstInput.AcquisitionId, second.Value.Inputs.Single().AcquisitionId);
        Assert.AreNotEqual(first.Value.SnapshotName, second.Value.SnapshotName);
        Assert.IsFalse((await access.GetStatusAsync(id, new(), default)).PreparationRequired);
        clock.Now += TimeSpan.FromMinutes(40);
        var third = await projections.StartAsync(id, new(), default);
        Assert.AreNotEqual(firstInput.AcquisitionId, third.Value.Inputs.Single().AcquisitionId);
        Assert.IsNull(third.Value.Inputs.Single().RefreshFailure);
        var fresh = third.Value.Inputs.Single();
        clock.Now += TimeSpan.FromMinutes(61);
        controlled!.FailWrites = true;
        var refused = await Assert.ThrowsAsync<KnowledgeProjectionException>(() => projections.StartAsync(id, new(), default));
        Assert.AreEqual("fixture_write_failed", refused.Code);
        Assert.AreEqual(third.Value.SnapshotName, (await access.GetStatusAsync(id, new(), default)).ActiveSnapshotId);
        var invalid = knowledge.Value.Definition with
        {
            DataSources = [knowledge.Value.Definition.DataSources.Single() with
        { AllowStaleOnRefreshFailure = true, MaximumFallbackAge = TimeSpan.FromMinutes(30) }]
        };
        Assert.AreEqual("knowledge_source_fallback_invalid", (await Assert.ThrowsAsync<KnowledgeSourceValidationException>(() =>
            management.PutExactAsync(Owner, id, invalid, knowledge.ETag, default))).Code);
        knowledge = await management.PutExactAsync(Owner, id, knowledge.Value.Definition with
        { DataSources = [knowledge.Value.Definition.DataSources.Single() with { AllowStaleOnRefreshFailure = true, MaximumFallbackAge = TimeSpan.FromHours(4) }] }, knowledge.ETag, default);
        var fallback = await projections.StartAsync(id, new(), default);
        Assert.AreEqual(fresh.AcquisitionId, fallback.Value.Inputs.Single().AcquisitionId);
        Assert.AreEqual("fixture_write_failed", fallback.Value.Inputs.Single().RefreshFailure!.Code);
        Assert.IsNotNull(fallback.Value.Inputs.Single().RefreshFailure!.AcquisitionId);
        clock.Now += TimeSpan.FromHours(3);
        await Assert.ThrowsAsync<KnowledgeProjectionException>(() => projections.StartAsync(id, new(), default));
        Assert.AreEqual(fallback.Value.SnapshotName, (await access.GetStatusAsync(id, new(), default)).ActiveSnapshotId);
        var denied = await access.ReadAsync(id, new(), default);
        for (var attempt = 0; denied.State == "preparing" && attempt < 100; attempt++)
        {
            await Task.Delay(50);
            denied = await access.ReadAsync(id, new() { PreparationId = denied.PreparationId }, default);
        }
        Assert.AreEqual("failed", denied.State);
        knowledge = await management.PutExactAsync(Owner, id, knowledge.Value.Definition with
        { Lifecycle = knowledge.Value.Definition.Lifecycle! with { AllowExpiredOnRefreshFailure = true } }, knowledge.ETag, default);
        var permitted = await AwaitNativeAsync(await access.ReadAsync(id, new(), default), result => access.ReadAsync(id,
            new() { PreparationId = result.PreparationId }, default));
        Assert.AreEqual(fallback.Value.SnapshotName, permitted.SnapshotId);
    }

    [TestMethod]
    public async Task ReplacementRetiresOldContentOnlyAfterTheSnapshotReadLeaseExpires()
    {
        var clock = new NativeAccessClock(TimeProvider.System.GetUtcNow());
        await using var factory = Factory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton(p => ActivatorUtilities.CreateInstance<LocalStorageObjectProvider>(p, clock));
            services.AddSingleton<IStorageContentGateway>(p => ActivatorUtilities.CreateInstance<StorageContentGateway>(p, clock));
            services.AddSingleton<IStoragePublicationGateway>(p => ActivatorUtilities.CreateInstance<LocalStoragePublicationGateway>(p, clock));
            services.AddSingleton(p => ActivatorUtilities.CreateInstance<DataSourceDatasetAcquisitionService>(p, clock));
            services.AddSingleton<IKnowledgeProjectionInputResolver>(p => ActivatorUtilities.CreateInstance<KnowledgeProjectionInputResolver>(p, clock));
            services.AddSingleton(p => ActivatorUtilities.CreateInstance<KnowledgeProjectionService>(p, clock));
            services.AddSingleton(p => ActivatorUtilities.CreateInstance<KnowledgeAccessService>(p, clock));
            services.AddSingleton(p => ActivatorUtilities.CreateInstance<SourceHistoryMaintenance>(p, clock));
        }));
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var fixture = await PrepareAsync(factory.Services);
        using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(fixture.Context);
        var definition = Source("retention-input", fixture, JsonSerializer.SerializeToElement(new { dataset = "retention" }));
        var source = await factory.Services.GetRequiredService<DataSourceManagementService>().CreateAsync(definition with
        { Definition = definition.Definition with { Dataset = definition.Definition.Dataset! with { Lifecycle = new() { TimeToLive = TimeSpan.FromMinutes(1) } } } }, default);
        var knowledge = await factory.Services.GetRequiredService<KnowledgeSourceManagementService>().CreateAsync(new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = KnowledgeResourceKinds.KnowledgeSource,
            Metadata = new() { Name = "retention-knowledge" },
            ScopeRef = Owner,
            Definition = new()
            {
                DisplayName = "Retention",
                Output = new(fixture.Storage, new(Write: true)),
                Lifecycle = new(),
                ProjectionFlow = new() { Name = "knowledge-projection-builtin", ScopeRef = Owner },
                DataSources = [new() { Name = "input", DataSource = new(source.Value.Name, Owner) }]
            }
        }, default);
        var id = new KnowledgeSourceId(knowledge.Value.Name, ScopeRef: Owner);
        var projections = factory.Services.GetRequiredService<KnowledgeProjectionService>();
        var first = await projections.StartAsync(id, new(), default);
        var access = factory.Services.GetRequiredService<KnowledgeAccessService>();
        var page = await access.ReadAsync(id, new() { MaxCharacters = 1 }, default);
        Assert.IsNotNull(page.NextCursor);
        clock.Now += TimeSpan.FromMinutes(2);
        var second = await projections.StartAsync(id, new(), default);
        Assert.AreNotEqual(first.Value.Inputs.Single().AcquisitionId, second.Value.Inputs.Single().AcquisitionId);
        var history = factory.Services.GetRequiredService<SourceHistoryMaintenance>();
        await history.MaintainAsync(knowledge.Value, default);
        var snapshots = factory.Services.GetRequiredService<KnowledgeSnapshotService>();
        var protectedSnapshot = await snapshots.GetAsync(new(first.Value.SnapshotName!), default);
        Assert.AreEqual(KnowledgeSnapshotLifecycleState.Retiring, protectedSnapshot.LifecycleState);
        var continuation = await access.ReadAsync(id, new() { Cursor = page.NextCursor }, default);
        Assert.AreEqual(first.Value.SnapshotName, continuation.SnapshotId);
        // Retired metadata at the head of a bounded cleanup batch must not starve later publications (#1137).
        using (factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(fixture.Context))
        {
            var resources = factory.Services.GetRequiredService<IResourceStore>();
            var retiredHead = new Dictionary<Guid, DateTimeOffset>();
            for (var index = 0; index < 16; index++)
            {
                var old = await resources.CreateImmutableAsync(protectedSnapshot.Snapshot with
                {
                    Uid = Guid.NewGuid(),
                    Generation = 1,
                    Metadata = new() { Name = "retired-fixture-" + index },
                    PublishedAt = protectedSnapshot.Snapshot.PublishedAt.AddDays(-1 - index),
                    Artifacts = []
                }, default);
                retiredHead.Add(old.Value.Uid, clock.Now);
            }
            var address = new ScopedResourceAddress(ResourceScopeRef.Workspace(fixture.Context.WorkspaceId), knowledge.Value.Namespace,
                KnowledgeResourceKinds.KnowledgeSnapshotObservedState, "snapshot-state-" + knowledge.Value.Uid.ToString("N"));
            var state = (await resources.GetExactAsync<KnowledgeSnapshotObservedResource>(address, default))!;
            await resources.PutExactAsync(address.ScopeRef, state.Value with
            {
                Generation = state.Value.Generation + 1,
                RetiredSnapshots = state.Value.RetiredSnapshots.Concat(retiredHead).ToDictionary(pair => pair.Key, pair => pair.Value)
            }, state.ETag, false, default);
        }
        clock.Now += KnowledgeAccessService.SessionLifetime;
        await history.MaintainAsync(knowledge.Value, default);
        Assert.AreEqual(KnowledgeSnapshotLifecycleState.Retired, (await snapshots.GetAsync(new(first.Value.SnapshotName!), default)).LifecycleState);
        Assert.AreEqual(second.Value.SnapshotName, (await snapshots.GetActiveAsync(id, default))!.Snapshot.Name);
        await Assert.ThrowsAsync<StorageException>(() => factory.Services.GetRequiredService<IStoragePublicationGateway>()
            .ReadAsync(protectedSnapshot.Snapshot.Artifacts.Single().Content!, 0, 1, default));
        var acquisitions = factory.Services.GetRequiredService<DataSourceAcquisitionService>();
        var latest = (await acquisitions.GetAsync(source.Value.Namespace, second.Value.Inputs.Single().AcquisitionId, default)).Value;
        await history.MaintainAsync(latest, default);
        var retired = (await acquisitions.GetAsync(source.Value.Namespace, first.Value.Inputs.Single().AcquisitionId, default)).Value;
        Assert.IsNotNull(retired.ContentRetiredAt);
        Assert.IsNotNull(retired.Content); // Exact provenance remains after the bytes are retired.
        Assert.IsNull((await acquisitions.GetAsync(source.Value.Namespace, latest.Name, default)).Value.ContentRetiredAt);
    }

    [TestMethod]
    public async Task ConcurrentDirectAdmissionIsBoundedAndDoesNotJoinAnotherProjectionsHandoff()
    {
        await using var factory = Factory();
        Fixture fixture;
        await using (var worker = await AwpTestWorker.StartAsync(factory)) fixture = await PrepareAsync(factory.Services);
        using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(fixture.Context);
        var definition = Source("bounded-direct", fixture, JsonSerializer.SerializeToElement(new { dataset = "ephemeral" }));
        var source = await factory.Services.GetRequiredService<DataSourceManagementService>().CreateAsync(definition with
        { Definition = definition.Definition with { Dataset = definition.Definition.Dataset! with { Storage = null, Lifecycle = new() { AllowDirect = true } } } }, default);
        var datasets = factory.Services.GetRequiredService<DataSourceDatasetAcquisitionService>();
        var starts = Enumerable.Range(0, DataSourceDatasetAcquisitionService.MaximumActiveDirectAcquisitions)
            .Select(index => datasets.StartAsync(source.Value, Empty, null, "projection-" + index, default));
        var admitted = await Task.WhenAll(starts);
        Assert.AreEqual(admitted.Length, admitted.Select(value => value.Value.Name).Distinct().Count());
        var full = await Assert.ThrowsAsync<DataSourceAcquisitionException>(() => datasets.StartAsync(source.Value, Empty, null, "extra", default));
        Assert.AreEqual("data_source_direct_capacity", full.Code);
        var repeated = await datasets.StartAsync(source.Value, Empty, null, "projection-0", default);
        Assert.AreEqual(admitted[0].Value.Name, repeated.Value.Name);
    }

    private sealed class ControlledContent(IStorageContentGateway inner) : IStorageContentGateway
    {
        public bool FailWrites { get; set; }
        public Task ValidateAsync(StorageContentRequest target, CancellationToken cancellationToken = default) => inner.ValidateAsync(target, cancellationToken);
        public Task<StorageObjectPage> ListAsync(StorageContentRequest target, string prefix, int limit, string? cursor = null, CancellationToken cancellationToken = default) => inner.ListAsync(target, prefix, limit, cursor, cancellationToken);
        public Task<StorageObject> StatAsync(StorageContentRequest target, string reference, CancellationToken cancellationToken = default) => inner.StatAsync(target, reference, cancellationToken);
        public Task<StorageObjectContent> ReadAsync(StorageContentRequest target, string reference, long offset, int maximumBytes, CancellationToken cancellationToken = default) => inner.ReadAsync(target, reference, offset, maximumBytes, cancellationToken);
        public Task<bool> RetireAsync(StorageContentRequest target, StorageObject exact, CancellationToken cancellationToken = default) => inner.RetireAsync(target, exact, cancellationToken);
        public Task<StorageObject> WriteAsync(StorageContentRequest target, StorageWrite request, CancellationToken cancellationToken = default) => FailWrites
            ? throw new StorageException("fixture_write_failed", "Synthetic refresh failure.") : inner.WriteAsync(target, request, cancellationToken);
    }

    [TestMethod]
    public async Task DirectAndStoredInputsPublishTogetherWithoutPersistingTheDirectExecutionPayload()
    {
        await using var factory = Factory();
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var fixture = await PrepareAsync(factory.Services);
        using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(fixture.Context);
        var sources = factory.Services.GetRequiredService<DataSourceManagementService>();
        var directDefinition = Source("direct-input", fixture, JsonSerializer.SerializeToElement(new { dataset = "direct-result" }));
        var direct = await sources.CreateAsync(directDefinition with
        {
            Definition = directDefinition.Definition with
            { Dataset = directDefinition.Definition.Dataset! with { Storage = null, Lifecycle = new() { AllowDirect = true } } }
        }, default);
        var storedDefinition = Source("stored-input", fixture, JsonSerializer.SerializeToElement(new { dataset = "stored-result" }));
        var stored = await sources.CreateAsync(storedDefinition with
        {
            Definition = storedDefinition.Definition with
            { Dataset = storedDefinition.Definition.Dataset! with { Lifecycle = new() { TimeToLive = TimeSpan.FromHours(1) } } }
        }, default);
        var knowledge = await factory.Services.GetRequiredService<KnowledgeSourceManagementService>().CreateAsync(new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = KnowledgeResourceKinds.KnowledgeSource,
            Metadata = new() { Name = "mixed-lifecycle" },
            ScopeRef = Owner,
            Definition = new()
            {
                DisplayName = "Mixed lifecycle",
                Output = new(fixture.Storage, new(Write: true)),
                Lifecycle = new(),
                ProjectionFlow = new() { Name = "knowledge-projection-builtin", ScopeRef = Owner },
                DataSources = [new() { Name = "direct", DataSource = new(direct.Value.Name, Owner) }, new() { Name = "stored", DataSource = new(stored.Value.Name, Owner) }]
            }
        }, default);
        var projections = factory.Services.GetRequiredService<KnowledgeProjectionService>();
        var first = await projections.StartAsync(new(knowledge.Value.Name, ScopeRef: Owner), new(), default);
        Assert.AreEqual(KnowledgeProjectionState.Succeeded, first.Value.State, first.Value.ErrorMessage);
        var directEvidence = first.Value.Inputs.Single(value => value.BindingName == "direct");
        Assert.IsNotNull(directEvidence.DirectInput);
        Assert.IsNull(directEvidence.StorageInput);
        Assert.IsTrue(directEvidence.AcquiredArtifacts.All(value => value.ContentBase64 is null));
        Assert.IsTrue(directEvidence.PreparedArtifacts.All(value => value.ContentBase64 is null));
        Assert.IsTrue(first.Value.Manifest!.Artifacts.All(value => value.ContentBase64 is null));
        var acquisitions = factory.Services.GetRequiredService<DataSourceAcquisitionService>();
        var directAcquisition = (await acquisitions.GetAsync(direct.Value.Namespace, directEvidence.AcquisitionId, default)).Value;
        Assert.IsNotNull(directAcquisition.DirectResult);
        Assert.IsNull(directAcquisition.Content);
        Assert.IsNull(directAcquisition.Manifest);
        var payload = await factory.Services.GetRequiredService<DataSourceDatasetAcquisitionService>().ReadDirectAsync(directAcquisition, directEvidence.DirectInput.RequestId, default);
        Assert.AreEqual("direct-result", payload.GetProperty("dataset").GetString());
        await Assert.ThrowsAsync<DataSourceAcquisitionException>(() => factory.Services.GetRequiredService<DataSourceDatasetAcquisitionService>()
            .ReadDirectAsync(directAcquisition, "another-projection", default));
        await using var flowDb = await factory.Services.GetRequiredService<IDbContextFactory<FlowDbContext>>().CreateDbContextAsync();
        var rawRuns = await flowDb.Database.SqlQueryRaw<string>("SELECT Payload AS Value FROM FlowResources WHERE Kind = 'run' AND Key = {0}",
            $"scope:{ResourceScopeRef.Workspace(fixture.Context.WorkspaceId).Value}:run:{directAcquisition.FlowRunId}").ToArrayAsync();
        Assert.HasCount(1, rawRuns);
        var metadata = JsonSerializer.Deserialize<FlowRun>(rawRuns[0], LifecycleJson)!;
        Assert.IsTrue(metadata.TransientPayloads);
        Assert.AreEqual(JsonValueKind.Null, metadata.Input.ValueKind);
        Assert.IsNull(metadata.Output);
        Assert.IsTrue(metadata.Steps.All(step => step.Output is null && step.ResolvedInput is null));
        Assert.IsFalse(new DirectExecutionPayloadPersistence(TimeProvider.System).Restore(metadata).TransientPayloadsAvailable);
        await using var runtimeDb = await factory.Services.GetRequiredService<IDbContextFactory<RuntimeRunDbContext>>().CreateDbContextAsync();
        var rawAssignments = await runtimeDb.Database.SqlQueryRaw<string>("SELECT Payload AS Value FROM RuntimeWorkerAssignments WHERE TargetRunId = {0}", directAcquisition.FlowRunId).ToArrayAsync();
        Assert.HasCount(1, rawAssignments);
        using var assignment = JsonDocument.Parse(rawAssignments[0]);
        Assert.IsTrue(assignment.RootElement.GetProperty("transientPayloads").GetBoolean());
        Assert.IsTrue(assignment.RootElement.GetProperty("executionEvents").EnumerateArray().All(value => value.GetProperty("payload").ValueKind == JsonValueKind.Null));
        var second = await projections.StartAsync(new(knowledge.Value.Name, ScopeRef: Owner), new(), default);
        Assert.AreEqual(KnowledgeProjectionState.Succeeded, second.Value.State, second.Value.ErrorMessage);
        Assert.AreNotEqual(directEvidence.AcquisitionId, second.Value.Inputs.Single(value => value.BindingName == "direct").AcquisitionId);
        Assert.AreEqual(first.Value.Inputs.Single(value => value.BindingName == "stored").AcquisitionId,
            second.Value.Inputs.Single(value => value.BindingName == "stored").AcquisitionId);
    }

    [TestMethod]
    public void DirectAcquisitionRequiresPermissionAndRetainedPoliciesRequireStorage()
    {
        var native = new DataSourceDatasetProperties(null, new(Write: true), null!);
        Assert.AreEqual("data_source_direct_permission_required", Assert.ThrowsExactly<DataSourceValidationException>(() => DataSourceDatasetCompositionService.ValidateLifecycle(native)).Code);
        DataSourceDatasetCompositionService.ValidateLifecycle(native with { Lifecycle = new() { AllowDirect = true } });
        foreach (var policy in new DataSourceAcquisitionPolicy[]
        { new() { AllowDirect = true, TimeToLive = TimeSpan.FromHours(1) }, new() { AllowDirect = true, KeepHistory = true, HistoryRetention = TimeSpan.FromDays(1) } })
            Assert.AreEqual("data_source_retained_storage_required", Assert.ThrowsExactly<DataSourceValidationException>(() => DataSourceDatasetCompositionService.ValidateLifecycle(native with { Lifecycle = policy })).Code);
    }
}
