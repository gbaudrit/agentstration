using System.Text.Json;
using Agentstration.DataSources;
using Agentstration.DataSources.Contracts;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure.Flows;
using Agentstration.Infrastructure.Knowledge;
using Agentstration.Infrastructure.Storage;
using Agentstration.Knowledge;
using Agentstration.Knowledge.Contracts;
using Agentstration.Resources;
using Agentstration.Storage;
using Agentstration.Storage.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

public sealed partial class DataSourceDatasetTests
{
    [TestMethod]
    public async Task IndependentHistoryWindowsRetainExactContentUntilTheirDeadlineAndProtectTheLatestSuccess()
    {
        var clock = new NativeAccessClock(TimeProvider.System.GetUtcNow());
        await using var factory = Factory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            ConfigureCompositionLifecycleClock(services, clock)));
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var fixture = await PrepareAsync(factory.Services);
        using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(fixture.Context);
        var definition = Source("history-window", fixture, JsonSerializer.SerializeToElement(new { dataset = "history" }));
        var source = await factory.Services.GetRequiredService<DataSourceManagementService>().CreateAsync(definition with
        {
            Definition = definition.Definition with
            {
                Dataset = definition.Definition.Dataset! with
                { Lifecycle = new() { TimeToLive = TimeSpan.FromMinutes(1), KeepHistory = true, HistoryRetention = TimeSpan.FromHours(1) } }
            }
        }, default);
        var knowledgeDefinition = CompositionKnowledge("history-windows", fixture,
            [new() { Name = "input", DataSource = new(source.Value.Name, Owner) }]);
        var knowledge = await factory.Services.GetRequiredService<KnowledgeSourceManagementService>().CreateAsync(knowledgeDefinition with
        {
            Definition = knowledgeDefinition.Definition with
            { Lifecycle = new() { KeepHistory = true, HistoryRetention = TimeSpan.FromHours(2) } }
        }, default);
        var id = new KnowledgeSourceId(knowledge.Value.Name, ScopeRef: Owner);
        var projections = factory.Services.GetRequiredService<KnowledgeProjectionService>();
        var acquisitions = factory.Services.GetRequiredService<DataSourceAcquisitionService>();
        var snapshots = factory.Services.GetRequiredService<KnowledgeSnapshotService>();
        var publication = factory.Services.GetRequiredService<IStoragePublicationGateway>();
        var history = factory.Services.GetRequiredService<SourceHistoryMaintenance>();
        var first = await projections.StartAsync(id, new(), default);
        var firstAcquisition = (await acquisitions.GetAsync(source.Value.Namespace, first.Value.Inputs.Single().AcquisitionId, default)).Value;
        var firstSnapshot = await snapshots.GetAsync(new(first.Value.SnapshotName!), default);
        var originalSnapshot = JsonSerializer.Serialize(firstSnapshot.Snapshot);
        var retained = firstSnapshot.Snapshot.Artifacts.Single().Content!;
        var originalBytes = (await publication.ReadAsync(retained, 0, 65536, default)).ToArray();
        var started = clock.Now;

        clock.Now = started.AddMinutes(2);
        var second = await projections.StartAsync(id, new(), default);
        var latest = (await acquisitions.GetAsync(source.Value.Namespace, second.Value.Inputs.Single().AcquisitionId, default)).Value;
        Assert.AreNotEqual(firstAcquisition.Name, latest.Name);

        clock.Now = started.AddHours(1).AddTicks(-1);
        await history.MaintainAsync(latest, default);
        await history.MaintainAsync(knowledge.Value, default);
        Assert.IsNull((await acquisitions.GetAsync(source.Value.Namespace, firstAcquisition.Name, default)).Value.ContentRetiredAt);
        Assert.AreEqual(KnowledgeSnapshotLifecycleState.Superseded,
            (await snapshots.GetAsync(new(firstSnapshot.Snapshot.Name), default)).LifecycleState);
        Assert.IsTrue(JsonElement.DeepEquals(await ReadCompositionDatasetAsync(factory.Services, firstAcquisition),
            JsonSerializer.SerializeToElement(new { dataset = "history" })));

        clock.Now = started.AddHours(1);
        await history.MaintainAsync(latest, default);
        var retiredAcquisition = (await acquisitions.GetAsync(source.Value.Namespace, firstAcquisition.Name, default)).Value;
        Assert.IsNotNull(retiredAcquisition.ContentRetiredAt);
        await Assert.ThrowsAsync<DataSourceAcquisitionException>(() => factory.Services.GetRequiredService<DataSourceDatasetAcquisitionService>()
            .ReadAsync(retiredAcquisition, 0, 1, default));
        await history.MaintainAsync(knowledge.Value, default);
        Assert.AreEqual(KnowledgeSnapshotLifecycleState.Superseded,
            (await snapshots.GetAsync(new(firstSnapshot.Snapshot.Name), default)).LifecycleState);
        CollectionAssert.AreEqual(originalBytes, (await publication.ReadAsync(retained, 0, 65536, default)).ToArray());

        clock.Now = started.AddHours(2).AddTicks(-1);
        await history.MaintainAsync(knowledge.Value, default);
        Assert.AreEqual(KnowledgeSnapshotLifecycleState.Superseded,
            (await snapshots.GetAsync(new(firstSnapshot.Snapshot.Name), default)).LifecycleState);
        clock.Now = started.AddHours(2);
        await history.MaintainAsync(knowledge.Value, default);
        var retiredSnapshot = await snapshots.GetAsync(new(firstSnapshot.Snapshot.Name), default);
        Assert.AreEqual(KnowledgeSnapshotLifecycleState.Retired, retiredSnapshot.LifecycleState);
        Assert.AreEqual(originalSnapshot, JsonSerializer.Serialize(retiredSnapshot.Snapshot));
        await Assert.ThrowsAsync<StorageException>(() => publication.ReadAsync(retained, 0, 1, default));

        clock.Now = started.AddHours(4);
        await history.MaintainAsync(latest, default);
        await history.MaintainAsync(knowledge.Value, default);
        Assert.IsNull((await acquisitions.GetAsync(source.Value.Namespace, latest.Name, default)).Value.ContentRetiredAt);
        Assert.AreEqual(second.Value.SnapshotName, (await snapshots.GetActiveAsync(id, default))!.Snapshot.Name);
        Assert.IsNotNull(firstAcquisition.Content);
        Assert.IsNotNull((await acquisitions.GetAsync(source.Value.Namespace, firstAcquisition.Name, default)).Value.Content);
    }

    [TestMethod]
    public async Task FailedSourceTransformationPreservesThePreviousResultAndItsSuccessfulCompletionTime()
    {
        var clock = new NativeAccessClock(TimeProvider.System.GetUtcNow());
        await using var factory = Factory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            ConfigureCompositionLifecycleClock(services, clock)));
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var fixture = await PrepareAsync(factory.Services);
        using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(fixture.Context);
        var objectSchema = JsonSerializer.SerializeToElement(new { type = "object" });
        await PublishCompositionChildAsync(factory.Services, "source-success", objectSchema, dataset: true);
        await PublishCompositionChildAsync(factory.Services, "source-failure", objectSchema, fail: true);
        var definition = Source("transformation-failure", fixture, JsonSerializer.SerializeToElement(new { dataset = "original" }));
        var sources = factory.Services.GetRequiredService<DataSourceManagementService>();
        var source = await sources.CreateAsync(definition with
        {
            Definition = definition.Definition with
            {
                Dataset = definition.Definition.Dataset! with
                { Transformations = [CompositionCall("source-success", "first")] }
            }
        }, default);
        var acquisitions = factory.Services.GetRequiredService<DataSourceAcquisitionService>();
        var first = await acquisitions.StartAsync(source.Value.Namespace, source.Value.Name, Owner, Empty, null, "success", default);
        var succeeded = await WaitForCompositionAcquisitionAsync(factory.Services, first.Value, DataSourceAcquisitionState.Succeeded);
        var original = await ReadCompositionDatasetAsync(factory.Services, succeeded);
        clock.Now += TimeSpan.FromMinutes(10);
        source = await sources.PutAsync(source.Value.Namespace, source.Value.Name, Owner, source.Value.Definition with
        { Dataset = source.Value.Definition.Dataset! with { Transformations = [CompositionCall("source-failure", "failed")] } }, source.ETag, default);
        var next = await acquisitions.StartAsync(source.Value.Namespace, source.Value.Name, Owner, Empty, null, "failed", default);
        var failed = await WaitForCompositionAcquisitionAsync(factory.Services, next.Value, DataSourceAcquisitionState.Failed);
        Assert.AreEqual("fixture_sequence_failed", failed.ErrorCode);
        Assert.IsNull(failed.Content);
        Assert.IsNull(failed.Manifest);
        var previous = (await acquisitions.GetAsync(source.Value.Namespace, succeeded.Name, default)).Value;
        Assert.AreEqual(DataSourceAcquisitionState.Succeeded, previous.State);
        Assert.AreEqual(succeeded.CompletedAt, previous.CompletedAt);
        Assert.AreEqual(succeeded.Content, previous.Content);
        Assert.IsTrue(JsonElement.DeepEquals(original, await ReadCompositionDatasetAsync(factory.Services, previous)));
        Assert.AreEqual(1, (await acquisitions.ListAsync(source.Value.Namespace, source.Value.Name, Owner, default))
            .Count(value => value.State == DataSourceAcquisitionState.Succeeded));
    }

    [TestMethod]
    [DataRow(false, true)]
    [DataRow(false, false)]
    [DataRow(true, true)]
    public async Task SequenceFailuresRespectOptionalInputsAndDoNotReplaceTheActiveSnapshot(bool afterAggregation, bool required)
    {
        await using var factory = Factory();
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var fixture = await PrepareAsync(factory.Services);
        using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(fixture.Context);
        var flows = factory.Services.GetRequiredService<FlowService>();
        var manifest = (await flows.GetVersionExactAsync(Owner, new("knowledge-projection-builtin"), "1.0.0", default))!.Value.Graph!.OutputSchema!.Value;
        await PublishCompositionChildAsync(factory.Services, "manifest-success", manifest);
        await PublishCompositionChildAsync(factory.Services, "manifest-failure", manifest, fail: true);
        var sources = factory.Services.GetRequiredService<DataSourceManagementService>();
        var sourceNames = new List<string>();
        foreach (var name in new[] { "first", "second" })
        {
            var definition = Source(name, fixture, JsonSerializer.SerializeToElement(new { dataset = name }));
            var source = await sources.CreateAsync(definition with
            {
                Definition = definition.Definition with
                {
                    Dataset = definition.Definition.Dataset! with
                    { Lifecycle = new() { TimeToLive = TimeSpan.FromHours(1) } }
                }
            }, default);
            sourceNames.Add(source.Value.Name);
        }
        var management = factory.Services.GetRequiredService<KnowledgeSourceManagementService>();
        var knowledge = await management.CreateAsync(CompositionKnowledge("failure-sequence", fixture,
            [new() { Name = "first", DataSource = new(sourceNames[0], Owner) },
                new() { Name = "second", DataSource = new(sourceNames[1], Owner), Transformations = [CompositionCall("manifest-success")] }],
            [CompositionCall("manifest-success")]), default);
        var id = new KnowledgeSourceId(knowledge.Value.Name, ScopeRef: Owner);
        var projections = factory.Services.GetRequiredService<KnowledgeProjectionService>();
        var snapshots = factory.Services.GetRequiredService<KnowledgeSnapshotService>();
        var baseline = await projections.StartAsync(id, new(), default);
        var original = (await snapshots.GetActiveAsync(id, default))!.Snapshot;
        var originalSnapshot = JsonSerializer.Serialize(original);
        Assert.HasCount(2, original.Artifacts);
        knowledge = await management.PutExactAsync(Owner, id, knowledge.Value.Definition with
        {
            Transformations = [CompositionCall(afterAggregation ? "manifest-failure" : "manifest-success")],
            DataSources = knowledge.Value.Definition.DataSources.Select(binding => binding.Name == "second" ? binding with
            { Required = required, Transformations = [CompositionCall(afterAggregation ? "manifest-success" : "manifest-failure")] } : binding).ToArray()
        }, knowledge.ETag, default);
        if (afterAggregation || required)
        {
            var failure = await Assert.ThrowsAsync<KnowledgeProjectionException>(() => projections.StartAsync(id, new(), default));
            Assert.AreEqual("fixture_sequence_failed", failure.Code);
            Assert.AreEqual(baseline.Value.SnapshotName, (await snapshots.GetActiveAsync(id, default))!.Snapshot.Name);
            Assert.HasCount(1, await snapshots.ListAsync(id, default));
            var failed = (await projections.ListAsync(id, default)).Single(value => value.State == KnowledgeProjectionState.Failed);
            Assert.AreEqual("fixture_sequence_failed", failed.ErrorCode);
            Assert.IsNull(failed.SnapshotName);
        }
        else
        {
            var completed = await projections.StartAsync(id, new(), default);
            Assert.AreEqual(KnowledgeProjectionState.Succeeded, completed.Value.State);
            Assert.AreNotEqual(baseline.Value.SnapshotName, completed.Value.SnapshotName);
            Assert.AreEqual("first", completed.Value.Inputs.Single().BindingName);
            Assert.AreEqual("second", completed.Value.InputIssues.Single().BindingName);
            Assert.AreEqual("fixture_sequence_failed", completed.Value.InputIssues.Single().Code);
            Assert.HasCount(1, (await snapshots.GetActiveAsync(id, default))!.Snapshot.Artifacts);
        }
        Assert.AreEqual(originalSnapshot, JsonSerializer.Serialize((await snapshots.GetAsync(new(original.Name), default)).Snapshot));
    }

    [TestMethod]
    public async Task RecoveryKeepsAcceptedSequenceWhileAReorderedSourceForcesANewCompatibleAcquisition()
    {
        await using var factory = Factory();
        Fixture fixture;
        await using (var setupWorker = await AwpTestWorker.StartAsync(factory)) fixture = await PrepareAsync(factory.Services);
        var scopes = factory.Services.GetRequiredService<IRequestContextScopeFactory>();
        using var scope = scopes.PushInstance(fixture.Context);
        var schema = JsonSerializer.SerializeToElement(new { type = "object" });
        await PublishCompositionChildAsync(factory.Services, "sequence-a", schema, dataset: true);
        await PublishCompositionChildAsync(factory.Services, "sequence-b", schema, dataset: true);
        var sources = factory.Services.GetRequiredService<DataSourceManagementService>();
        var definition = Source("reconfigured-sequence", fixture, JsonSerializer.SerializeToElement(new { dataset = "pinned" }));
        var source = await sources.CreateAsync(definition with
        {
            Definition = definition.Definition with
            {
                Dataset = definition.Definition.Dataset! with
                {
                    Lifecycle = new() { TimeToLive = TimeSpan.FromHours(1) },
                    Transformations = [CompositionCall("sequence-a", "A"), CompositionCall("sequence-b", "B")]
                }
            }
        }, default);
        var acquisitions = factory.Services.GetRequiredService<DataSourceAcquisitionService>();
        var accepted = await acquisitions.StartAsync(source.Value.Namespace, source.Value.Name, Owner, Empty, null, "accepted", default);
        var pins = JsonSerializer.Serialize(accepted.Value.Dataset!.Composition!.Receipt);
        Assert.AreEqual(DataSourceAcquisitionState.Pending, accepted.Value.State);
        await PublishCompositionChildAsync(factory.Services, "sequence-a", schema, dataset: true, version: "2.0.0");
        source = await sources.PutAsync(source.Value.Namespace, source.Value.Name, Owner, source.Value.Definition with
        {
            Dataset = source.Value.Definition.Dataset! with
            { Transformations = [CompositionCall("sequence-b", "B-new"), CompositionCall("sequence-a", "A-new")] }
        }, source.ETag, default);
        using (scopes.PushSystem()) await acquisitions.RecoverPendingAsync(default);
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var recovered = await WaitForCompositionAcquisitionAsync(factory.Services, accepted.Value, DataSourceAcquisitionState.Succeeded);
        Assert.AreEqual(accepted.Value.Uid, recovered.Uid);
        Assert.AreEqual(accepted.Value.FlowRunId, recovered.FlowRunId);
        Assert.AreEqual(accepted.Value.DataSourceGeneration, recovered.DataSourceGeneration);
        Assert.AreEqual(pins, JsonSerializer.Serialize(recovered.Dataset!.Composition!.Receipt));
        var oldContent = await ReadCompositionDatasetAsync(factory.Services, recovered);
        Assert.AreEqual("B", oldContent.GetProperty("label").GetString());
        Assert.AreEqual("A", oldContent.GetProperty("previous").GetProperty("label").GetString());
        Assert.AreEqual("1.0.0", oldContent.GetProperty("previous").GetProperty("implementation").GetString());

        var knowledge = await factory.Services.GetRequiredService<KnowledgeSourceManagementService>().CreateAsync(
            CompositionKnowledge("reordered-knowledge", fixture, [new() { Name = "input", DataSource = new(source.Value.Name, Owner) }]), default);
        var projected = await factory.Services.GetRequiredService<KnowledgeProjectionService>().StartAsync(new(knowledge.Value.Name, ScopeRef: Owner), new(), default);
        var current = (await acquisitions.GetAsync(source.Value.Namespace, projected.Value.Inputs.Single().AcquisitionId, default)).Value;
        Assert.AreNotEqual(recovered.Name, current.Name);
        Assert.AreEqual(source.Value.Generation, current.DataSourceGeneration);
        var newContent = await ReadCompositionDatasetAsync(factory.Services, current);
        Assert.AreEqual("A-new", newContent.GetProperty("label").GetString());
        Assert.AreEqual("B-new", newContent.GetProperty("previous").GetProperty("label").GetString());
        Assert.AreEqual("2.0.0", newContent.GetProperty("implementation").GetString());
        var replay = await Assert.ThrowsAsync<DataSourceAcquisitionException>(() =>
            acquisitions.StartAsync(source.Value.Namespace, source.Value.Name, Owner, Empty, null, "accepted", default));
        Assert.AreEqual("data_source_acquisition_idempotency_conflict", replay.Code);
        var repository = factory.Services.GetRequiredService<IFlowRepository>();
        var run = (await repository.GetRunExactAsync(ResourceScopeRef.Workspace(fixture.Context.WorkspaceId), recovered.FlowRunId, default))!.Value;
        var children = (await repository.ListRunsAsync(run.Scope, null, null, 0, 100, default)).Items
            .Where(value => value.Value.RootFlowRunId == run.Id && value.Value.FlowId.Value == "sequence-a").ToArray();
        Assert.AreEqual("1.0.0", children.Single().Value.FlowVersion);
    }

    private static FlowCompositionCall CompositionCall(string name, string label = "identity") => new()
    { Flow = new(name, ScopeRef: Owner), Parameters = JsonSerializer.SerializeToElement(new { label }) };

    private static KnowledgeSourceResource CompositionKnowledge(string name, Fixture fixture,
        IReadOnlyList<KnowledgeDataSourceBinding> bindings, IReadOnlyList<FlowCompositionCall>? transformations = null) => new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = KnowledgeResourceKinds.KnowledgeSource,
            Metadata = new() { Name = name },
            ScopeRef = Owner,
            Definition = new()
            {
                DisplayName = name,
                Output = new(fixture.Storage, new(Write: true)),
                ProjectionFlow = new() { Name = "knowledge-projection-builtin", ScopeRef = Owner },
                DataSources = bindings,
                Transformations = transformations ?? []
            }
        };

    private static async Task PublishCompositionChildAsync(IServiceProvider services, string name, JsonElement valueSchema,
        bool dataset = false, bool fail = false, string version = "1.0.0")
    {
        var input = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            { value = valueSchema, parameters = new { type = "object" } },
            required = new[] { "value", "parameters" },
            additionalProperties = false
        });
        var graph = new FlowGraphDefinition
        {
            EntryStep = "input",
            InputSchema = input,
            OutputSchema = valueSchema,
            Steps = [new InputFlowStepDefinition { Name = "input", Schema = input },
                new OutputFlowStepDefinition { Name = "completed", Outcome = FlowOutputOutcome.Success,
                    OutputMapping = dataset ? JsonSerializer.SerializeToElement(new { dataset = "${input.value.dataset}",
                        label = "${input.parameters.label}", previous = "${input.value}", implementation = version })
                        : JsonSerializer.SerializeToElement("${input.value}") }],
            Transitions = [new("input-output", "input", "completed", "completed")]
        };
        if (fail)
            graph = graph with
            {
                Steps = [graph.Steps[0], new ConditionFlowStepDefinition { Name = "reject", Operator = "equals",
                    Left = "reject", Right = "reject" }, graph.Steps[1],
                    new OutputFlowStepDefinition { Name = "error", Outcome = FlowOutputOutcome.Error,
                        Code = "fixture_sequence_failed", Message = "Synthetic composition failure.",
                        OutputMapping = JsonSerializer.SerializeToElement("${input.value}") }],
                Transitions = [new("input-reject", "input", "completed", "reject"),
                    new("reject-error", "reject", "true", "error"), new("reject-success", "reject", "false", "completed")]
            };
        var flows = services.GetRequiredService<FlowService>();
        if (version == "1.0.0")
            _ = await flows.CreateExactAsync(Owner, new(name, null, version, true,
                new WorkflowFlowDefinition("input", [new("input", FlowNodeKind.Input), new("completed", FlowNodeKind.Output)],
                    [new("input", "completed")], ["completed"]), Graph: graph), ResourceNamespace.Default, default);
        else
        {
            var current = (await flows.GetExactAsync(Owner, new(name), default))!;
            _ = await flows.UpdateExactAsync(Owner, new(name), new(null, version, true, current.Value.Definition, Graph: graph), current.ETag, default);
        }
        _ = await flows.PublishVersionExactAsync(Owner, new(name), version, true, default);
    }

    private static async Task<DataSourceAcquisitionResource> WaitForCompositionAcquisitionAsync(IServiceProvider services,
        DataSourceAcquisitionResource accepted, DataSourceAcquisitionState expected)
    {
        var acquisitions = services.GetRequiredService<DataSourceAcquisitionService>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            var current = (await acquisitions.GetAsync(accepted.Namespace, accepted.Name, deadline.Token)).Value;
            if (current.State is DataSourceAcquisitionState.Succeeded or DataSourceAcquisitionState.Failed or DataSourceAcquisitionState.Cancelled or DataSourceAcquisitionState.TimedOut)
            { Assert.AreEqual(expected, current.State, current.ErrorMessage); return current; }
            await Task.Delay(20, deadline.Token);
        }
    }

    private static async Task<JsonElement> ReadCompositionDatasetAsync(IServiceProvider services, DataSourceAcquisitionResource acquisition)
    {
        var content = await services.GetRequiredService<DataSourceDatasetAcquisitionService>().ReadAsync(acquisition, 0, 65536, default);
        using var json = JsonDocument.Parse(Convert.FromBase64String(content.ContentBase64));
        return json.RootElement.Clone();
    }

    private static void ConfigureCompositionLifecycleClock(IServiceCollection services, NativeAccessClock clock)
    {
        services.AddSingleton(p => ActivatorUtilities.CreateInstance<LocalStorageObjectProvider>(p, clock));
        services.AddSingleton<IStorageContentGateway>(p => ActivatorUtilities.CreateInstance<StorageContentGateway>(p, clock));
        services.AddSingleton<IStoragePublicationGateway>(p => ActivatorUtilities.CreateInstance<LocalStoragePublicationGateway>(p, clock));
        services.AddSingleton(p => ActivatorUtilities.CreateInstance<DataSourceDatasetAcquisitionService>(p, clock));
        services.AddSingleton<IKnowledgeProjectionInputResolver>(p => ActivatorUtilities.CreateInstance<KnowledgeProjectionInputResolver>(p, clock));
        services.AddSingleton(p => ActivatorUtilities.CreateInstance<KnowledgeProjectionService>(p, clock));
        services.AddSingleton(p => ActivatorUtilities.CreateInstance<KnowledgeAccessService>(p, clock));
        services.AddSingleton(p => ActivatorUtilities.CreateInstance<SourceHistoryMaintenance>(p, clock));
    }
}
