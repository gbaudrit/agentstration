using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Resources;

namespace Agentstration.Application.Tests;

public sealed partial class FlowTests
{
    [TestMethod]
    public async Task TransformationDraftCreatesAndPublishesTheTypedValueEnvelopeInItsOwnerScope()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        var drafts = new FlowDraftService(fixture.Repository, fixture.Service, new AlwaysValidFlowValidator(), TimeProvider.System);
        var value = JsonSerializer.SerializeToElement(new { type = "object", properties = new { artifacts = new { type = "array" } }, required = new[] { "artifacts" }, additionalProperties = false });
        var draft = await drafts.CreateExactAsync(TestScope.WorkspaceId, ResourceScopeRef.Instance,
            new CreateFlowDraftCommand("created-transformation", "Created transformation", null, null, "EmptyFlow")
            { Transformation = new(value, value) }, default);
        Assert.AreEqual(ResourceScopeRef.Instance, draft.Value.ScopeRef);
        var graph = draft.Value.Definition;
        Assert.IsTrue(JsonElement.DeepEquals(value, graph.InputSchema!.Value.GetProperty("properties").GetProperty("value")));
        Assert.IsTrue(JsonElement.DeepEquals(value, graph.OutputSchema!.Value));
        Assert.AreEqual("object", graph.InputSchema.Value.GetProperty("properties").GetProperty("parameters").GetProperty("type").GetString());
        Assert.AreEqual("${input.value}", graph.Steps.OfType<TransformFlowStepDefinition>().Single().Mapping?.GetString());
        Assert.HasCount(0, graph.Steps.OfType<AgentFlowStepDefinition>().ToArray());
        Assert.IsNull(await fixture.Repository.GetDraftExactAsync(TestScope.WorkspaceId, TestScope.ScopeRef, new("created-transformation"), default));
        var published = await drafts.PublishExactAsync(TestScope.WorkspaceId, ResourceScopeRef.Instance, new("created-transformation"), "1.0.0", null, true, default);
        var publishedGraph = published.Value.Graph!;
        Assert.IsTrue(JsonElement.DeepEquals(graph.InputSchema.Value, publishedGraph.ResolveInputSchema()!.Value));
        Assert.AreEqual("${steps.transform.output}", publishedGraph.Steps.OfType<OutputFlowStepDefinition>().Single(step => step.Outcome == FlowOutputOutcome.Success).OutputMapping?.GetString());
    }

    [TestMethod]
    [DataRow("{\"type\":\"object\",\"oneOf\":[]}")]
    [DataRow("{}")]
    public async Task InvalidTransformationContractsCreateNeitherResourceNorDraft(string schema)
    {
        await using var fixture = await FlowFixture.CreateAsync();
        var drafts = new FlowDraftService(fixture.Repository, fixture.Service, new AlwaysValidFlowValidator(), TimeProvider.System);
        using var invalid = JsonDocument.Parse(schema);
        var value = JsonSerializer.SerializeToElement(new { type = "object" });
        var error = await Assert.ThrowsExactlyAsync<FlowValidationException>(() => drafts.CreateAsync(TestScope.WorkspaceId,
            new CreateFlowDraftCommand("invalid-transformation", "Invalid transformation", null, null, "EmptyFlow")
            { Transformation = new(value, invalid.RootElement) }, default));
        Assert.AreEqual("flow_transformation_contract_invalid", error.Code);
        Assert.IsNull(await fixture.Repository.GetExactAsync(TestScope.ScopeRef, new("invalid-transformation"), default));
        Assert.IsNull(await fixture.Repository.GetDraftExactAsync(TestScope.WorkspaceId, TestScope.ScopeRef, new("invalid-transformation"), default));
    }
}
