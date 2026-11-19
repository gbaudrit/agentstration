using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Schemas;
using Agentstration.Flows;
using Agentstration.Flows.Contracts;
using Agentstration.Web.Console;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class OrchestrationEditorModelTests
{
    [TestMethod]
    public void EditorCreatesEveryProviderNeutralOrchestrationPattern()
    {
        foreach (var strategy in Enum.GetValues<FlowOrchestrationStrategy>())
        {
            var model = CreateModel(strategy);
            var definition = model.CreateDefinition();

            Assert.AreEqual(strategy, definition.Strategy);
            CollectionAssert.AreEqual(new[] { "agent-a", "agent-b" }, definition.Participants.Select(participant => participant.Id).ToArray());
        }
    }

    [TestMethod]
    public void EditorRoundTripsPublishedDefinitionWithoutRuntimeTypes()
    {
        var original = CreateModel(FlowOrchestrationStrategy.Handoff);
        var now = DateTimeOffset.UtcNow;
        var response = new FlowResponse("review", "review", "Review", "1.0.0", true, null, original.CreateDefinition(), new Dictionary<string, string>(), now, now);

        var restored = OrchestrationEditorModel.From(response);
        var definition = restored.CreateDefinition();

        Assert.AreEqual(FlowOrchestrationStrategy.Handoff, restored.Strategy);
        Assert.IsInstanceOfType<HandoffOrchestrationPattern>(definition.Pattern);
        Assert.AreEqual("agent-a", ((HandoffOrchestrationPattern)definition.Pattern).InitialParticipant);
    }

    [TestMethod]
    public void EditorRejectsInsufficientParticipantsAndMagenticManagerOverlap()
    {
        var model = CreateModel(FlowOrchestrationStrategy.Sequential);
        model.ParticipantIds.RemoveAt(1);
        Assert.Throws<InvalidOperationException>(() => model.CreateDefinition());

        model = CreateModel(FlowOrchestrationStrategy.Magentic);
        model.ManagerId = "agent-a";
        Assert.Throws<InvalidOperationException>(() => model.CreateDefinition());
    }

    [TestMethod]
    public void MetadataEditingPreservesEverySavedParticipantAndManagerReferenceCoordinate()
    {
        var owner = ResourceScopeRef.Parse("/instance");
        var participant = new FlowTargetReference(FlowTargetKind.Agent, "agent-a", "1.2.3", ResourceNamespace.Parse("team-a"), owner, Guid.NewGuid(), Guid.NewGuid(), "participant-hash")
        {
            InputSchema = JsonSerializer.SerializeToElement(new { type = "object", required = new[] { "query" } }),
            OutputSchema = JsonSerializer.SerializeToElement(new { type = "string" }),
            OutputContract = new() { Mode = DataContractMode.Dynamic, Source = "input", MediaType = "application/json" }
        };
        var relative = new FlowTargetReference(FlowTargetKind.Agent, "agent-b");
        var manager = new FlowTargetReference(FlowTargetKind.Agent, "manager", "2.0.0", ResourceNamespace.Parse("team-b"), owner, Guid.NewGuid(), Guid.NewGuid(), "manager-hash");
        var original = new OrchestrationFlowDefinition([participant, relative], new MagenticOrchestrationPattern(manager, 12, 4, 3));
        var now = DateTimeOffset.MinValue;
        var response = new FlowResponse("review", "review", "Review", "1.0.0", true, null, original, new Dictionary<string, string>(), now, now);

        var restored = OrchestrationEditorModel.From(response);
        restored.Description = "Only metadata changes";

        Assert.AreEqual(JsonSerializer.Serialize(original), JsonSerializer.Serialize(restored.CreateDefinition()));
    }

    [TestMethod]
    [DataRow(FlowOrchestrationStrategy.Handoff)]
    [DataRow(FlowOrchestrationStrategy.Magentic)]
    public void AnIncompleteStrategyCanBePreviewedWhileItsFinalDefinitionRemainsInvalid(FlowOrchestrationStrategy strategy)
    {
        var model = new OrchestrationEditorModel { Strategy = strategy };
        Assert.AreEqual(strategy, model.CreatePreviewDefinition().Strategy);
        Assert.Throws<InvalidOperationException>(() => model.CreateDefinition());
        model.ParticipantIds.AddRange(["agent-a", "agent-b"]);
        Assert.Throws<InvalidOperationException>(() => model.CreateDefinition());
    }

    private static OrchestrationEditorModel CreateModel(FlowOrchestrationStrategy strategy)
    {
        var model = new OrchestrationEditorModel { Strategy = strategy, ManagerId = "manager" };
        model.ParticipantIds.AddRange(["agent-a", "agent-b"]);
        model.EnsureStrategyDefaults();
        return model;
    }
}
