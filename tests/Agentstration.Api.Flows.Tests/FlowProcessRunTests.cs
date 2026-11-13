using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Resources;

namespace Agentstration.Application.Tests;

public sealed partial class FlowTests
{
    [TestMethod]
    public async Task ProcessAdmissionReusesImmutableDefinitionClosesDynamicDiscoveryAndProjectsCausalityBeforeCompletion()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        var published = await CreatePublishedGraphAsync(fixture, "generic-process", ImplicitProcessGraph());
        var runs = Service(fixture, new TestFlowRunQueue());
        var parent = await runs.CreateAsync(published.Value.Id, "1.0.0", "local", FlowRunTrigger.Manual,
            "tester", "processing", JsonSerializer.SerializeToElement(new { }), TestScope, default);
        var coordinate = Guid.NewGuid();
        var first = await runs.CreateOrGetAssignedChildExactAsync(TestScope.ScopeRef, parent.Value.Id, "partition",
            JsonSerializer.SerializeToElement("one"), "process", 0, default, processUnit: new("fragment-A", 0), ownerStepExecutionId: coordinate);
        var second = await runs.CreateOrGetAssignedChildExactAsync(TestScope.ScopeRef, parent.Value.Id, "partition",
            JsonSerializer.SerializeToElement("two"), "process", 1, default, processUnit: new("fragment-B", 1), ownerStepExecutionId: coordinate);
        Assert.AreNotEqual(first.Value.Id, second.Value.Id);
        Assert.AreEqual(first.Value.ProcessOrigin!.Definition, second.Value.ProcessOrigin!.Definition);
        Assert.AreEqual(coordinate, first.Value.ProcessOrigin.OwnerStepExecutionId);
        Assert.AreEqual(parent.Value.DefinitionSnapshotId, first.Value.ProcessOrigin.Definition.SourceSnapshotId);
        Assert.AreEqual(parent.Value.DefinitionHash, first.Value.ProcessOrigin.Definition.SourceDefinitionHash);
        Assert.AreEqual("process_input", first.Value.DefinitionSnapshot.Graph!.EntryStep);
        Assert.IsNull(first.Value.ArtifactProcessing);
        var causal = await runs.GetCausalityAsync(parent.Value.Id, 0, 100, TestScope, default);
        Assert.HasCount(3, causal.Items);
        var admission = causal.Items[0].Processes["partition"];
        Assert.IsFalse(admission.Closed);
        Assert.IsNull(admission.TotalUnits);
        Assert.HasCount(2, admission.Units);
        Assert.AreEqual("partition", causal.Items[1].ParentStepName);
        await runs.CloseProcessAdmissionExactAsync(TestScope.ScopeRef, parent.Value.Id, "partition", default);
        var closed = (await runs.GetAsync(parent.Value.Id, TestScope, default))!.Value.Steps.Single(step => step.StepName == "partition").Process!;
        Assert.IsTrue(closed.Closed);
        Assert.AreEqual(2, closed.TotalUnits);
        var replay = await runs.CreateOrGetAssignedChildExactAsync(TestScope.ScopeRef, parent.Value.Id, "partition",
            JsonSerializer.SerializeToElement("one"), "process", 0, default, processUnit: new("fragment-A", 0), ownerStepExecutionId: Guid.NewGuid());
        Assert.AreEqual(first.Value.Id, replay.Value.Id);
        Assert.AreEqual(coordinate, replay.Value.ProcessOrigin!.OwnerStepExecutionId);
        var refused = await Assert.ThrowsExactlyAsync<FlowValidationException>(() => runs.CreateOrGetAssignedChildExactAsync(TestScope.ScopeRef,
            parent.Value.Id, "partition", JsonSerializer.SerializeToElement("new"), "process", 2, default, processUnit: new("fragment-C", 2)));
        Assert.AreEqual("process_admission_closed", refused.Code);
        var changed = await Assert.ThrowsExactlyAsync<FlowValidationException>(() => runs.CreateOrGetAssignedChildExactAsync(TestScope.ScopeRef,
            parent.Value.Id, "partition", JsonSerializer.SerializeToElement("changed"), "process", 0, default, processUnit: new("fragment-A", 0)));
        Assert.AreEqual("process_child_conflict", changed.Code);
        Assert.IsNull(await runs.GetAsync(first.Value.Id, TestScope with { WorkspaceId = WorkspaceId.New() }, default));
    }

    [TestMethod]
    public async Task ConcurrentProcessAdmissionCannotOversubscribeTheRootDescendantBudget()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        var published = await CreatePublishedGraphAsync(fixture, "bounded-process", ImplicitProcessGraph());
        var runs = Service(fixture, new TestFlowRunQueue(), new() { MaximumDescendantRuns = 2 });
        var parent = await runs.CreateAsync(published.Value.Id, "1.0.0", "local", FlowRunTrigger.Manual, "tester", null,
            JsonSerializer.SerializeToElement(new { }), TestScope, default);
        var accepted = 0;
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async index =>
        {
            try
            {
                await runs.CreateOrGetAssignedChildExactAsync(TestScope.ScopeRef, parent.Value.Id, "partition", JsonSerializer.SerializeToElement(index),
                    "process", index, default, processUnit: new($"fragment-{index}", index));
                Interlocked.Increment(ref accepted);
            }
            catch (FlowValidationException exception) { Assert.AreEqual("flow_descendant_limit_exceeded", exception.Code); }
        }));
        Assert.AreEqual(2, accepted);
        var updated = (await runs.GetAsync(parent.Value.Id, TestScope, default))!.Value;
        Assert.HasCount(2, updated.ReservedDescendantRunIds!);
    }

    [TestMethod]
    public async Task OnlyExplicitProcessCapabilityCanAdmitUnitsAndMalformedKeysCannotSelectADifferentUnit()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        var published = await CreatePublishedGraphAsync(fixture, "guarded-process", ImplicitProcessGraph());
        var runs = Service(fixture, new TestFlowRunQueue());
        var parent = await runs.CreateAsync(published.Value.Id, "1.0.0", "local", FlowRunTrigger.Manual, "tester", null,
            JsonSerializer.SerializeToElement(new { }), TestScope, default);
        var denied = await Assert.ThrowsExactlyAsync<FlowValidationException>(() => runs.CreateOrGetAssignedChildExactAsync(TestScope.ScopeRef,
            parent.Value.Id, "body", JsonSerializer.SerializeToElement(1), "process", 0, default));
        Assert.AreEqual("child_flow_step_invalid", denied.Code);
        var malformed = await Assert.ThrowsExactlyAsync<FlowValidationException>(() => runs.CreateOrGetAssignedChildExactAsync(TestScope.ScopeRef,
            parent.Value.Id, "partition", JsonSerializer.SerializeToElement(1), "process", 0, default, processUnit: new("unit", 1)));
        Assert.AreEqual("process_unit_invalid", malformed.Code);
        await runs.CloseProcessAdmissionExactAsync(TestScope.ScopeRef, parent.Value.Id, "partition", default);
        var admission = (await runs.GetAsync(parent.Value.Id, TestScope, default))!.Value.Steps.Single(step => step.StepName == "partition").Process!;
        Assert.IsTrue(admission.Closed);
        Assert.AreEqual(0, admission.TotalUnits);
    }

    [TestMethod]
    public async Task NamedChildWithinAnArtifactProcessInheritsAccessWithoutInheritingDefinitionOrigin()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        await CreatePublishedGraphAsync(fixture, "named-child", ChildGraph());
        var graph = ImplicitProcessGraph();
        graph = graph with
        {
            Steps = graph.Steps.Select(step => step.Name == "partition" ? (FlowStepDefinition)new ArtifactTransformFlowStepDefinition
                { Name = "partition", Mode = ArtifactProcessingMode.Access } : step.Name == "body" ? new FlowCallStepDefinition
                { Name = "body", Flow = new("named-child", FlowCallVersionStrategy.Exact, "1.0.0") } : step).ToArray(),
            Transitions = graph.Transitions.Select(edge => edge.FromStep == "body" ? edge with { Event = "completed" } : edge).ToArray()
        };
        var published = await CreatePublishedGraphAsync(fixture, "artifact-parent", graph);
        var runs = Service(fixture, new TestFlowRunQueue());
        var input = JsonSerializer.SerializeToElement(new { article = "fixture" });
        var parent = await runs.CreateAsync(published.Value.Id, "1.0.0", "local", FlowRunTrigger.Manual, "tester", null, input, TestScope, default);
        var unit = await runs.CreateOrGetAssignedChildExactAsync(TestScope.ScopeRef, parent.Value.Id, "partition", input, "artifactProcessing", 0, default);
        var named = await runs.CreateOrGetAssignedChildExactAsync(TestScope.ScopeRef, unit.Value.Id, "body", input, "flowCall", null, default);
        Assert.IsNotNull(unit.Value.ProcessOrigin);
        Assert.IsNotNull(named.Value.ArtifactProcessing);
        Assert.AreEqual(unit.Value.ArtifactProcessing, named.Value.ArtifactProcessing);
        Assert.IsNull(named.Value.ProcessOrigin);
        Assert.AreEqual("named-child", named.Value.FlowId.Value);
        Assert.AreEqual(unit.Value.Id, named.Value.ParentFlowRunId);
        var causal = await runs.GetCausalityAsync(parent.Value.Id, 0, 100, TestScope, default);
        Assert.HasCount(3, causal.Items);
        Assert.HasCount(1, causal.Items.Where(node => node.ProcessOrigin is not null).ToArray());
    }

    private static FlowGraphDefinition ImplicitProcessGraph() => new()
    {
        EntryStep = "input",
        Steps = [new InputFlowStepDefinition { Name = "input" },
            new TransformFlowStepDefinition { Name = "partition", Processing = new() { MaximumUnits = 16, MaximumConcurrency = 4 } },
            new TransformFlowStepDefinition { Name = "body", Mapping = JsonSerializer.SerializeToElement("${input}") },
            new TransformFlowStepDefinition { Name = "correct", Mapping = JsonSerializer.SerializeToElement("${transition.output}") },
            new OutputFlowStepDefinition { Name = "completed", Outcome = FlowOutputOutcome.Success }],
        Transitions = [new("start", "input", "completed", "partition"), new("process", "partition", "process", "body"),
            new("correct", "body", "completed", "correct"), new("return", "correct", "completed", "partition"),
            new("done", "partition", "completed", "completed")]
    };
}
