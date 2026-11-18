using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Application;

namespace Agentstration.Application.Tests;

public sealed partial class FlowTests
{
    [TestMethod]
    public async Task ArtifactProcessingCreatesAnExactPrivateChildAndKeepsThePublishedGraphUnchanged()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        var graph = new FlowGraphDefinition
        {
            EntryStep = "input",
            Steps = [new InputFlowStepDefinition { Name = "input" },
                new ForeachArtifactsFlowStepDefinition { Name = "artifacts", Mode = ArtifactProcessingMode.Access },
                new TransformFlowStepDefinition { Name = "body", Mapping = JsonSerializer.SerializeToElement("${input}") },
                new OutputFlowStepDefinition { Name = "completed", Outcome = FlowOutputOutcome.Success },
                new OutputFlowStepDefinition { Name = "error", Outcome = FlowOutputOutcome.Error }],
            Transitions = [new("start", "input", "completed", "artifacts"), new("process", "artifacts", "process", "body"),
                new("return", "body", "completed", "artifacts"), new("done", "artifacts", "completed", "completed"),
                new("error", "artifacts", "error", "error")]
        };
        var published = await CreatePublishedGraphAsync(fixture, "artifact-processor", graph);
        var runs = Service(fixture, new TestFlowRunQueue());
        var input = JsonSerializer.SerializeToElement(new { artifact = "first" });
        var parent = await runs.CreateAsync(published.Value.Id, "1.0.0", "local", FlowRunTrigger.Manual,
            "tester", "processing", JsonSerializer.SerializeToElement(new { }), TestScope, default);
        var child = await runs.CreateOrGetAssignedChildExactAsync(TestScope.ScopeRef, parent.Value.Id,
            "artifacts", input, "artifactProcessing", 0, default);
        Assert.AreEqual(parent.Value.Id, child.Value.ParentFlowRunId);
        Assert.AreEqual(parent.Value.Id, child.Value.RootFlowRunId);
        Assert.AreEqual("artifact_processing_input", child.Value.DefinitionSnapshot.Graph!.EntryStep);
        Assert.AreEqual(1, child.Value.NestingDepth);
        Assert.IsNotNull(child.Value.ProcessOrigin);
        Assert.AreEqual("process", child.Value.ProcessOrigin.Definition.Region);
        Assert.AreEqual("unit-0", child.Value.ProcessOrigin.UnitKey);
        Assert.IsNotNull(child.Value.ArtifactProcessing);
        Assert.IsTrue(child.Value.ArtifactProcessing.ContentAccess);
        Assert.AreEqual(0, child.Value.ArtifactProcessing.Index);
        Assert.AreEqual(parent.Value.Id, child.Value.ArtifactProcessing.OwnerRunId);
        Assert.AreNotEqual(parent.Value.DefinitionHash, child.Value.DefinitionHash);
        // Existing private children retain their historical evidence and can resume without being relabeled.
        await fixture.Repository.UpdateRunAsync(child.Value with { ProcessOrigin = null }, child.ETag, default);
        var replay = await runs.CreateOrGetAssignedChildExactAsync(TestScope.ScopeRef, parent.Value.Id,
            "artifacts", input, "artifactProcessing", 0, default);
        Assert.AreEqual(child.Value.Id, replay.Value.Id);
        Assert.IsNull(replay.Value.ProcessOrigin);
        var other = await runs.CreateOrGetAssignedChildExactAsync(TestScope.ScopeRef, parent.Value.Id,
            "artifacts", input, "artifactProcessing", 1, default);
        Assert.AreNotEqual(child.Value.Id, other.Value.Id);
        var conflict = await Assert.ThrowsExactlyAsync<FlowValidationException>(() => runs.CreateOrGetAssignedChildExactAsync(
            TestScope.ScopeRef, parent.Value.Id, "artifacts", JsonSerializer.SerializeToElement(new { artifact = "changed" }), "artifactProcessing", 0, default));
        Assert.AreEqual("artifact_processing_child_conflict", conflict.Code);
        var version = (await fixture.Service.GetVersionAsync(TestScope.WorkspaceId, published.Value.Id, "1.0.0", default))!.Value;
        Assert.AreEqual(FlowDefinitionHash.Compute(graph), FlowDefinitionHash.Compute(version.Graph!));
    }
}
