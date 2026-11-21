using Agentstration.Flows;
using Agentstration.Resources;
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

    [TestMethod]
    public void HomonymousParticipantsKeepTheirSelectedNamespaceAndOwner()
    {
        using var context = CreateContext();
        var model = new OrchestrationEditorModel();
        var agents = new[]
        {
            new AgentSummary("same-name", "Team A assistant", "assistant", "1", "Ready", [], "local", DateTimeOffset.MinValue)
                { Namespace = ResourceNamespace.Parse("team-a"), ScopeRef = ResourceScopeRef.Parse("/instance") },
            new AgentSummary("same-name", "Team B assistant", "assistant", "1", "Ready", [], "local", DateTimeOffset.MinValue)
                { Namespace = ResourceNamespace.Parse("team-b"), ScopeRef = ResourceScopeRef.Parse("/instance") }
        };
        var rendered = context.Render<OrchestrationDefinitionEditor>(parameters => parameters
            .Add(component => component.Model, model).Add(component => component.Agents, agents));

        rendered.FindAll("[data-testid=flow-participant]")[1].Change(true);

        Assert.HasCount(1, rendered.FindAll(".agent-option.is-selected"));
        var selected = model.CreatePreviewDefinition().Participants.Single();
        Assert.AreEqual(agents[1].Namespace, selected.Namespace);
        Assert.AreEqual(agents[1].ScopeRef, selected.ScopeRef);
        Assert.IsTrue(rendered.FindAll("[data-testid=flow-participant]")[0].HasAttribute("disabled"));
        StringAssert.Contains(rendered.FindAll(".agent-option")[0].TextContent, "team-a/same-name");
        StringAssert.Contains(rendered.FindAll(".agent-option")[1].TextContent, "team-b/same-name");
    }

    [TestMethod]
    public void MagenticManagerAndPreviewResolveTheChosenHomonymWithoutReplacingItsPinnedReference()
    {
        using var context = CreateContext();
        var owner = ResourceScopeRef.Parse("/instance");
        var model = new OrchestrationEditorModel { Strategy = FlowOrchestrationStrategy.Magentic };
        model.ParticipantIds.AddRange(["worker-a", "worker-b"]);
        var pinned = new FlowTargetReference(FlowTargetKind.Agent, "manager", "2.0.0", ResourceNamespace.Parse("team-b"), owner, Guid.NewGuid(), Guid.NewGuid(), "saved-hash");
        model.SelectManager(pinned);
        var agents = new[]
        {
            new AgentSummary("manager", "Team A manager", "assistant", "1", "Ready", [], "local", DateTimeOffset.MinValue)
                { Namespace = ResourceNamespace.Parse("team-a"), ScopeRef = owner },
            new AgentSummary("manager", "Team B manager", "assistant", "1", "Ready", [], "local", DateTimeOffset.MinValue)
                { Namespace = ResourceNamespace.Parse("team-b"), ScopeRef = owner }
        };
        var rendered = context.Render<OrchestrationDefinitionEditor>(parameters => parameters
            .Add(component => component.Model, model).Add(component => component.Agents, agents));
        Assert.AreEqual("/instance|team-b|manager", rendered.Find(".strategy-options select").GetAttribute("value"));
        Assert.AreSame(pinned, ((MagenticOrchestrationPattern)model.CreateDefinition().Pattern).Manager);
        rendered.FindAll(".topology-node").Single(node => node.GetAttribute("aria-label")!.StartsWith("manager", StringComparison.Ordinal)).Click();
        StringAssert.Contains(rendered.Find(".orchestration-node-details").TextContent, "team-b/manager");

        rendered.Find(".strategy-options select").Change("/instance|team-a|manager");

        var changed = ((MagenticOrchestrationPattern)model.CreateDefinition().Pattern).Manager;
        Assert.AreEqual(agents[0].Namespace, changed.Namespace);
        Assert.AreEqual(owner, changed.ScopeRef);
        Assert.IsNull(changed.Version);
        StringAssert.Contains(rendered.Find(".orchestration-node-details").TextContent, "team-a/manager");
    }

    [TestMethod]
    public void SwitchingToMagenticBeforeChoosingItsManagerKeepsTheEditorUsable()
    {
        using var context = CreateContext();
        var model = new OrchestrationEditorModel();
        var rendered = context.Render<OrchestrationDefinitionEditor>(parameters => parameters.Add(component => component.Model, model));

        rendered.Find("[data-testid=flow-strategy]").Change("Magentic");

        Assert.AreEqual(FlowOrchestrationStrategy.Magentic, model.Strategy);
        Assert.IsNotNull(rendered.Find(".strategy-options select"));
        Assert.HasCount(0, rendered.FindAll(".topology-canvas"));
        Assert.Throws<InvalidOperationException>(() => model.CreateDefinition());
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void AnUnchangedRelativeReferenceCanInspectItsUniqueInheritedAgentWithoutBeingRewritten(bool recordedOwner)
    {
        using var context = CreateContext();
        var model = new OrchestrationEditorModel();
        model.ParticipantIds.Add("inherited");
        var agents = new[] { new AgentSummary("inherited", "Inherited assistant", "assistant", "1", "Ready", [], "local", DateTimeOffset.MinValue)
            { ScopeRef = recordedOwner ? ResourceScopeRef.Instance : null } };
        var rendered = context.Render<OrchestrationDefinitionEditor>(parameters => parameters
            .Add(component => component.Model, model).Add(component => component.Agents, agents)
            .Add(component => component.OwnerScope, ResourceScopeRef.Workspace(Guid.NewGuid())));

        Assert.IsTrue(rendered.Find("[data-testid=flow-participant]").HasAttribute("checked"));
        StringAssert.Contains(rendered.Find(".participant-order").TextContent, "Inherited assistant");
        var saved = model.CreatePreviewDefinition().Participants.Single();
        Assert.IsNull(saved.Namespace);
        Assert.IsNull(saved.ScopeRef);
    }

    [TestMethod]
    public void AnUnresolvedRelativeParticipantDoesNotSelectAnotherWorkspaceAndCanBeExplicitlyReplaced()
    {
        using var context = CreateContext();
        var model = new OrchestrationEditorModel();
        model.ParticipantIds.Add("same-name");
        var other = ResourceScopeRef.Workspace(Guid.NewGuid());
        var agents = new[] { new AgentSummary("same-name", "Another workspace assistant", "assistant", "1", "Ready", [], "local", DateTimeOffset.MinValue) { ScopeRef = other } };
        var rendered = context.Render<OrchestrationDefinitionEditor>(parameters => parameters
            .Add(component => component.Model, model).Add(component => component.Agents, agents)
            .Add(component => component.OwnerScope, ResourceScopeRef.Workspace(Guid.NewGuid())));
        Assert.HasCount(0, rendered.FindAll(".agent-option.is-selected"));
        Assert.IsTrue(rendered.Find("[data-testid=flow-participant]").HasAttribute("disabled"));
        Assert.IsNull(model.CreatePreviewDefinition().Participants.Single().ScopeRef);

        rendered.Find(".participant-order .button-danger").Click();
        Assert.IsEmpty(model.ParticipantIds);
        Assert.IsFalse(rendered.Find("[data-testid=flow-participant]").HasAttribute("disabled"));
        rendered.Find("[data-testid=flow-participant]").Change(true);

        Assert.AreEqual(other, model.CreatePreviewDefinition().Participants.Single().ScopeRef);
    }

    private static BunitContext CreateContext()
    {
        var context = new BunitContext().WithCollectionPresentation();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        return context;
    }
}
