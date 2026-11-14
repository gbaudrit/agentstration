using System.Text.Json;
using Agentstration.DataSources;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Identity.Contracts;
using Agentstration.Knowledge;
using Agentstration.Knowledge.Contracts;
using Agentstration.Resources;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

public sealed partial class DataSourceDatasetTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NativeKnowledgeWithoutTransformationsPreservesContentBeyondTheParameterBudget(bool orchestrate)
    {
        await using var factory = Factory();
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var fixture = await PrepareAsync(factory.Services);
        using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(fixture.Context);
        var payload = new string('x', 20_000);
        var source = await factory.Services.GetRequiredService<DataSourceManagementService>().CreateAsync(
            Source("untransformed-input", fixture, JsonSerializer.SerializeToElement(new { dataset = payload })), default);
        using var client = factory.CreateClient();
        SelectConsoleContext(client, Owner);
        _ = await AcquireAsync(client, source.Value.Name);
        var knowledge = await factory.Services.GetRequiredService<KnowledgeSourceManagementService>().CreateAsync(new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = KnowledgeResourceKinds.KnowledgeSource,
            Metadata = new() { Name = "untransformed-knowledge" },
            ScopeRef = Owner,
            Definition = new()
            {
                DisplayName = "Untransformed knowledge",
                Output = new(fixture.Storage, new(Write: true)),
                ProjectionFlow = new() { Name = "knowledge-projection-builtin", ScopeRef = Owner },
                Orchestration = orchestrate ? new("knowledge-orchestration-builtin", ScopeRef: Owner) : null,
                DataSources = [new() { Name = "input", DataSource = new(source.Value.Name, Owner) }]
            }
        }, default);
        var projected = await factory.Services.GetRequiredService<KnowledgeProjectionService>().StartAsync(
            new(knowledge.Value.Name, ScopeRef: Owner), new(), default);
        var repository = factory.Services.GetRequiredService<IFlowRepository>();
        var run = (await repository.GetRunExactAsync(ResourceScopeRef.Workspace(fixture.Context.WorkspaceId),
            projected.Value.ProjectionFlowRunId, default))!.Value;
        Assert.IsTrue(run.Input.GetRawText().Length > 65_536, "The retained content is repeated in the projection evidence and input manifest.");
        Assert.AreEqual(KnowledgeProjectionState.Succeeded, projected.Value.State, projected.Value.ErrorMessage);
        Assert.IsNotNull(projected.Value.SnapshotName);
        var outputArtifact = run.Output!.Value.GetProperty("artifacts")[0];
        using var content = JsonDocument.Parse(Convert.FromBase64String(outputArtifact.GetProperty("contentBase64").GetString()!));
        Assert.AreEqual(payload, content.RootElement.GetProperty("dataset").GetString());
        if (orchestrate)
        {
            Assert.HasCount(0, run.Composition!.Bindings["postAggregate"]);
            var child = (await repository.ListRunsAsync(run.Scope, null, null, 0, 100, default)).Items
                .Single(candidate => candidate.Value.ParentFlowRunId == run.Id).Value;
            Assert.AreEqual("knowledge-projection-builtin", child.FlowId.Value);
            Assert.IsTrue(JsonElement.DeepEquals(run.Input, child.Input));
            Assert.IsTrue(JsonElement.DeepEquals(run.Output.Value, child.Output!.Value));
        }
    }

    [TestMethod]
    public async Task NativeDatasetAndKnowledgeExecutePinnedOrderedSequencesThroughGovernedChildren()
    {
        await using var factory = Factory();
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var fixture = await PrepareAsync(factory.Services);
        using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(fixture.Context);
        var service = factory.Services.GetRequiredService<FlowService>();
        var value = JsonSerializer.SerializeToElement(new { type = "object" });
        var input = JsonSerializer.SerializeToElement(new { type = "object", properties = new { value = new { type = "object" }, parameters = new { type = "object" } }, required = new[] { "value", "parameters" }, additionalProperties = false });
        var graph = new FlowGraphDefinition
        {
            EntryStep = "input",
            InputSchema = input,
            OutputSchema = value,
            Steps = [new InputFlowStepDefinition { Name = "input", Schema = input }, new OutputFlowStepDefinition { Name = "output", Schema = value, OutputMapping = JsonSerializer.SerializeToElement(new { stage = "${input.parameters.ordinal}", previous = "${input.value}" }) }],
            Transitions = [new("next", "input", "completed", "output")]
        };
        await service.CreateExactAsync(Owner, new("sequence-identity", null, "1.0.0", true,
            new WorkflowFlowDefinition("input", [new("input", FlowNodeKind.Input), new("output", FlowNodeKind.Output)], [new("input", "output")], ["output"]), Graph: graph), ResourceNamespace.Default, default);
        await service.PublishVersionExactAsync(Owner, new("sequence-identity"), "1.0.0", true, default);
        var calls = new FlowCompositionCall[]
        {
            new() { Flow = new("sequence-identity", ScopeRef: Owner), Parameters = JsonSerializer.SerializeToElement(new { ordinal = 1, literal = "${input.mustRemainLiteral}" }) },
            new() { Flow = new("sequence-identity", ScopeRef: Owner), Parameters = JsonSerializer.SerializeToElement(new { ordinal = 2 }) }
        };
        var definition = Source("composed-native", fixture, JsonSerializer.SerializeToElement(new { dataset = "composed-content" }));
        var source = await factory.Services.GetRequiredService<DataSourceManagementService>().CreateAsync(definition with
        { Definition = definition.Definition with { Dataset = definition.Definition.Dataset! with { Transformations = calls } } }, default);
        using var client = factory.CreateClient();
        SelectConsoleContext(client, Owner);
        var acquisition = await AcquireAsync(client, source.Value.Name);
        Assert.IsNotNull(acquisition.Content);
        var acquiredContent = await factory.Services.GetRequiredService<DataSourceDatasetAcquisitionService>().ReadAsync(acquisition, 0, 65536, default);
        using var acquiredJson = JsonDocument.Parse(Convert.FromBase64String(acquiredContent.ContentBase64));
        Assert.AreEqual(2, acquiredJson.RootElement.GetProperty("stage").GetInt32());
        Assert.AreEqual(1, acquiredJson.RootElement.GetProperty("previous").GetProperty("stage").GetInt32());
        Assert.IsNotNull(acquisition.Dataset!.Composition?.Receipt);
        Assert.HasCount(2, acquisition.Dataset.Composition.Receipt.Bindings["transformations"]);
        var repository = factory.Services.GetRequiredService<IFlowRepository>();
        var acquiredRun = await repository.GetRunExactAsync(ResourceScopeRef.Workspace(fixture.Context.WorkspaceId), acquisition.FlowRunId, default);
        Assert.AreEqual("data-source-orchestration-builtin", acquiredRun!.Value.FlowId.Value);
        Assert.IsNotNull(acquiredRun.Value.Composition);
        var acquisitionChildren = (await repository.ListRunsAsync(acquiredRun.Value.Scope, null, null, 0, 100, default)).Items
            .Where(run => run.Value.RootFlowRunId == acquisition.FlowRunId).ToArray();
        Assert.HasCount(3, acquisitionChildren);
        Assert.IsTrue(acquisitionChildren.Any(run => run.Value.Input.TryGetProperty("parameters", out var parameters)
            && parameters.TryGetProperty("literal", out var literal) && literal.GetString() == "${input.mustRemainLiteral}"));
        var secondDefinition = Source("composed-second", fixture, JsonSerializer.SerializeToElement(new { dataset = "second-content" }));
        var second = await factory.Services.GetRequiredService<DataSourceManagementService>().CreateAsync(secondDefinition, default);
        _ = await AcquireAsync(client, second.Value.Name);
        var manifestSchema = (await service.GetVersionExactAsync(Owner, new("knowledge-projection-builtin"), "1.0.0", default))!.Value.Graph!.OutputSchema!.Value;
        var manifestInput = JsonSerializer.SerializeToElement(new { type = "object", properties = new { value = manifestSchema, parameters = new { type = "object" } }, required = new[] { "value", "parameters" }, additionalProperties = false });
        var manifestGraph = graph with
        {
            InputSchema = manifestInput,
            OutputSchema = manifestSchema,
            Steps = [new InputFlowStepDefinition { Name = "input", Schema = manifestInput }, new OutputFlowStepDefinition { Name = "output", Schema = manifestSchema, OutputMapping = JsonSerializer.SerializeToElement("${input.value}") }]
        };
        await service.CreateExactAsync(Owner, new("manifest-identity", null, "1.0.0", true,
            new WorkflowFlowDefinition("input", [new("input", FlowNodeKind.Input), new("output", FlowNodeKind.Output)], [new("input", "output")], ["output"]), Graph: manifestGraph), ResourceNamespace.Default, default);
        await service.PublishVersionExactAsync(Owner, new("manifest-identity"), "1.0.0", true, default);
        calls = calls.Select(call => call with { Flow = new("manifest-identity", ScopeRef: Owner) }).ToArray();
        var knowledge = await factory.Services.GetRequiredService<KnowledgeSourceManagementService>().CreateAsync(new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = KnowledgeResourceKinds.KnowledgeSource,
            Metadata = new() { Name = "composed-knowledge" },
            ScopeRef = Owner,
            Definition = new()
            {
                DisplayName = "Composed knowledge",
                Output = new(fixture.Storage, new(Write: true)),
                ProjectionFlow = new() { Name = "knowledge-projection-builtin", ScopeRef = Owner },
                Transformations = calls,
                DataSources = [new() { Name = "first", DataSource = new(source.Value.Name, Owner), Transformations = calls },
                    new() { Name = "second", DataSource = new(second.Value.Name, Owner), Transformations = calls }]
            }
        }, default);
        var projected = await factory.Services.GetRequiredService<KnowledgeProjectionService>().StartAsync(new(knowledge.Value.Name, ScopeRef: Owner), new(), default);
        Assert.AreEqual(KnowledgeProjectionState.Succeeded, projected.Value.State, projected.Value.ErrorMessage);
        Assert.HasCount(2, projected.Value.Inputs);
        Assert.IsTrue(projected.Value.Inputs.All(evidence => evidence.TransformationComposition?.Receipt?.Bindings["transformations"].Count == 2));
        Assert.IsNotNull(projected.Value.ProjectionComposition);
        var projectedRun = await repository.GetRunExactAsync(ResourceScopeRef.Workspace(fixture.Context.WorkspaceId), projected.Value.ProjectionFlowRunId, default);
        Assert.AreEqual("knowledge-orchestration-builtin", projectedRun!.Value.FlowId.Value);
        Assert.IsNotNull(projectedRun.Value.Composition);
    }
}
