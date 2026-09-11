using Agentstration.Flow;
using Agentstration.Web.Components.Flows;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using FlowDesignerPage = Agentstration.Web.Components.Pages.FlowDesigner;
using FlowOrchestrationEditorPage = Agentstration.Web.Components.Pages.FlowOrchestrationEditor;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class OrchestrationConsoleComponentTests
{
    [TestMethod]
    public void PreviewShowsMagenticManagerAndParticipantsWithoutRuntimeVocabulary()
    {
        using var context = CreateContext();
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
        Assert.IsTrue(rendered.Find(".topology-canvas").ClassList.Contains("fit"));
        Assert.IsFalse(rendered.Find(".orchestration-preview-layout").ClassList.Contains("has-details"));
        Assert.IsFalse(rendered.Markup.Contains("Microsoft.Agents", StringComparison.Ordinal));
    }

    [TestMethod]
    public void PreviewShowsDeclaredHandoffRoutes()
    {
        using var context = CreateContext();
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
        using var context = CreateContext();
        var strings = context.Services.GetRequiredService<IStringLocalizer<Agentstration.Web.Components.Pages.FlowOrchestrationEditorStrings>>();
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
        Assert.IsTrue(rendered.Find(".orchestration-preview-layout").ClassList.Contains("has-details"));
        StringAssert.Contains(details.TextContent, strings["InitialParticipant"].Value);
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
        using var context = CreateContext();
        var strings = context.Services.GetRequiredService<IStringLocalizer<Agentstration.Web.Components.Pages.FlowOrchestrationEditorStrings>>();
        var model = new OrchestrationEditorModel { Strategy = FlowOrchestrationStrategy.Sequential };
        model.ParticipantIds.AddRange(["agent-a", "agent-b"]);

        var rendered = context.Render<OrchestrationDefinitionEditor>(parameters => parameters
            .Add(component => component.Model, model)
            .Add(component => component.IsReadOnly, true));

        var configuration = rendered.Find("fieldset.orchestration-configuration");
        Assert.IsTrue(configuration.HasAttribute("disabled"));
        Assert.IsTrue(configuration.QuerySelectorAll("input, select, button").All(element => element.HasAttribute("disabled") || element.Closest("fieldset[disabled]") is not null));
        StringAssert.Contains(rendered.Markup, strings["ExecutionPreview"].Value);
    }

    [TestMethod]
    public void ParticipantPickerHighlightsSelectionAndUsesDisplayNameInExecutionOrder()
    {
        using var context = CreateContext();
        var strings = context.Services.GetRequiredService<IStringLocalizer<Agentstration.Web.Components.Pages.FlowOrchestrationEditorStrings>>();
        var model = new OrchestrationEditorModel { Strategy = FlowOrchestrationStrategy.Sequential };
        model.ParticipantIds.Add("agent-a");
        var agents = new[]
        {
            new AgentSummary("agent-a", "Agent Alpha", "assistant", "1", "Ready", [], "local", DateTimeOffset.MinValue, "default"),
            new AgentSummary("agent-b", "Agent Beta", "assistant", "1", "Ready", [], "local", DateTimeOffset.MinValue, "default")
        };

        var rendered = context.Render<OrchestrationDefinitionEditor>(parameters => parameters
            .Add(component => component.Model, model)
            .Add(component => component.Agents, agents));

        var configurationSections = rendered.FindAll(".orchestration-control-column > section");
        Assert.IsTrue(configurationSections[0].ClassList.Contains("orchestration-participants-panel"));
        Assert.IsTrue(configurationSections[1].ClassList.Contains("orchestration-strategy-panel"));
        Assert.HasCount(1, rendered.FindAll(".agent-option.is-selected"));
        Assert.AreEqual("Agent Alpha", rendered.Find(".participant-order .order-copy strong").TextContent);
        Assert.AreEqual(strings["SearchAgents"].Value, rendered.Find(".agent-search input").GetAttribute("placeholder"));
        Assert.AreEqual("search", rendered.Find(".agent-search input").GetAttribute("type"));
        Assert.IsFalse(rendered.Markup.Contains(strings["LivePreviewEyebrow"].Value, StringComparison.Ordinal));

        rendered.Find(".agent-search input").Input("Beta");

        var filteredAgent = rendered.FindAll(".agent-option");
        Assert.HasCount(1, filteredAgent);
        StringAssert.Contains(filteredAgent[0].TextContent, "Agent Beta");
    }

    private static BunitContext CreateContext()
    {
        var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        return context;
    }
}
