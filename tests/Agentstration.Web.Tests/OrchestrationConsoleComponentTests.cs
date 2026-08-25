using Agentstration.Flow;
using Agentstration.Web.Components.Flows;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.AspNetCore.Components;
using FlowDesignerPage = Agentstration.Web.Components.Pages.FlowDesigner;
using FlowOrchestrationEditorPage = Agentstration.Web.Components.Pages.FlowOrchestrationEditor;

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

        Assert.HasCount(1, rendered.FindAll(".topology-node.initial"));
        Assert.HasCount(1, rendered.FindAll(".topology-edge.conditional"));
        StringAssert.Contains(rendered.Markup, "agent-a");
        StringAssert.Contains(rendered.Markup, "agent-b");
    }

    [TestMethod]
    public void PreviewOpensNodeDetailsWhenParticipantIsSelected()
    {
        using var context = new BunitContext();
        var model = new OrchestrationEditorModel
        {
            Strategy = FlowOrchestrationStrategy.Handoff,
            InitialParticipant = "agent-a"
        };
        model.ParticipantIds.AddRange(["agent-a", "agent-b"]);
        model.Handoffs.Add(new FlowHandoffEditorRoute("agent-a", "agent-b"));
        var agents = new[]
        {
            new AgentSummary("agent-a", "Agent A", "assistant", "1", "Ready", ["calendar"], "local", DateTimeOffset.MinValue, "default")
        };

        var rendered = context.Render<OrchestrationPreview>(parameters => parameters
            .Add(component => component.Model, model)
            .Add(component => component.Agents, agents));

        Assert.HasCount(0, rendered.FindAll(".orchestration-node-details"));
        rendered.FindAll(".topology-node").Single(node => node.GetAttribute("aria-label")!.StartsWith("agent-a", StringComparison.Ordinal)).Click();

        var details = rendered.Find(".orchestration-node-details");
        StringAssert.Contains(details.TextContent, "Initial participant");
        StringAssert.Contains(details.TextContent, "default");
        StringAssert.Contains(details.TextContent, "calendar");
    }

    [TestMethod]
    public void NamespacedDesignerAndOrchestrationRoutesAreDeclared()
    {
        var designerRoutes = typeof(FlowDesignerPage).GetCustomAttributes(typeof(RouteAttribute), true).Cast<RouteAttribute>().Select(value => value.Template).ToArray();
        var orchestrationRoutes = typeof(FlowOrchestrationEditorPage).GetCustomAttributes(typeof(RouteAttribute), true).Cast<RouteAttribute>().Select(value => value.Template).ToArray();

        CollectionAssert.Contains(designerRoutes, "/namespaces/{FlowNamespace}/flows/{FlowId}/designer");
        CollectionAssert.Contains(orchestrationRoutes, "/namespaces/{FlowNamespace}/flows/{FlowId}/orchestration");
    }

    [TestMethod]
    public void ReadOnlyOrchestrationEditorDisablesAllConfigurationControls()
    {
        using var context = new BunitContext();
        var model = new OrchestrationEditorModel { Strategy = FlowOrchestrationStrategy.Sequential };
        model.ParticipantIds.AddRange(["agent-a", "agent-b"]);

        var rendered = context.Render<OrchestrationDefinitionEditor>(parameters => parameters
            .Add(component => component.Model, model)
            .Add(component => component.IsReadOnly, true));

        var configuration = rendered.Find("fieldset.orchestration-configuration");
        Assert.IsTrue(configuration.HasAttribute("disabled"));
        Assert.IsTrue(configuration.QuerySelectorAll("input, select, button").All(element => element.HasAttribute("disabled") || element.Closest("fieldset[disabled]") is not null));
        StringAssert.Contains(rendered.Markup, "Execution preview");
    }
}
