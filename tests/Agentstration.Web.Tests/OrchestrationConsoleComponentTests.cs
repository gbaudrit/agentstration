using Agentstration.Flow;
using Agentstration.Web.Components.Flows;
using Agentstration.Web.Console;
using Bunit;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class OrchestrationConsoleComponentTests
{
    [TestMethod]
    public void PreviewShowsMagenticManagerAndParticipantsWithoutRuntimeVocabulary()
    {
        using var context = new BunitContext();
        var model = new OrchestrationEditorModel
        {
            Strategy = FlowOrchestrationStrategy.Magentic,
            ManagerId = "manager"
        };
        model.ParticipantIds.AddRange(["researcher", "reviewer"]);

        var rendered = context.Render<OrchestrationPreview>(parameters => parameters.Add(component => component.Model, model));

        StringAssert.Contains(rendered.Markup, "manager");
        StringAssert.Contains(rendered.Markup, "researcher");
        StringAssert.Contains(rendered.Markup, "reviewer");
        Assert.IsFalse(rendered.Markup.Contains("Microsoft.Agents", StringComparison.Ordinal));
    }

    [TestMethod]
    public void PreviewShowsDeclaredHandoffRoutes()
    {
        using var context = new BunitContext();
        var model = new OrchestrationEditorModel
        {
            Strategy = FlowOrchestrationStrategy.Handoff,
            InitialParticipant = "agent-a"
        };
        model.ParticipantIds.AddRange(["agent-a", "agent-b"]);
        model.Handoffs.Add(new FlowHandoffEditorRoute("agent-a", "agent-b"));

        var rendered = context.Render<OrchestrationPreview>(parameters => parameters.Add(component => component.Model, model));

        Assert.HasCount(1, rendered.FindAll(".preview-node.initial"));
        StringAssert.Contains(rendered.Markup, "agent-a");
        StringAssert.Contains(rendered.Markup, "agent-b");
    }
}
