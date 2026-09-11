using Agentstration.Flow;
using Agentstration.Flow.Contracts;
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

    private static OrchestrationEditorModel CreateModel(FlowOrchestrationStrategy strategy)
    {
        var model = new OrchestrationEditorModel { Strategy = strategy, ManagerId = "manager" };
        model.ParticipantIds.AddRange(["agent-a", "agent-b"]);
        model.EnsureStrategyDefaults();
        return model;
    }
}
