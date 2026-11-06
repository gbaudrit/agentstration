using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Contracts;
using Agentstration.Resources;
using Agentstration.Schemas;
using Agentstration.Schemas.Contracts;
using Agentstration.Web.Components.JsonSchema;
using Agentstration.Web.Components.Schemas;
using Agentstration.Web.Components.State;
using Agentstration.Web.FlowDesigner.Backend;
using Agentstration.Web.FlowDesigner.Components;
using Agentstration.Web.FlowDesigner.Diagramming;
using Agentstration.Web.FlowDesigner.State;
using AngleSharp.Dom;
using Blazor.Diagrams;
using Blazor.Diagrams.Core.Anchors;
using Blazor.Diagrams.Core.Behaviors;
using Blazor.Diagrams.Core.Geometry;
using BlazorMonaco.Editor;
using Bunit;
using Bunit.JSInterop;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using FlowDesignerComponent = Agentstration.Web.FlowDesigner.Components.FlowDesigner;

namespace Agentstration.Web.FlowDesigner.Tests;

public sealed partial class FlowDesignerReadOnlyTests
{
    [TestMethod]
    public void NewAgentAndOutputStepsMapTheIncomingTransitionOutput()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub(readOnly: false));
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub());
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true)
            .SetResult(new Rectangle(0, 0, 1024, 768));

        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters
            .Add(component => component.ResourceId, "parent"));
        rendered.FindAll(".step-palette button").Single(button => button.TextContent.Trim() == "Agent").Click();
        rendered.FindAll(".step-palette button").Single(button => button.TextContent.Trim() == "Output").Click();

        var steps = context.Services.GetRequiredService<FlowEditorStore>().State.Resource!.Definition.Steps;
        Assert.AreEqual("${transition.output}", steps.OfType<AgentFlowStepDefinition>().Single().InputMapping?.GetString());
        Assert.AreEqual("${transition.output}", steps.OfType<OutputFlowStepDefinition>().Last().OutputMapping?.GetString());
    }

    [TestMethod]
    public void AddingAgentStepConnectsItsErrorOutputToTheFirstErrorTerminal()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        var definition = new FlowGraphDefinition
        {
            EntryStep = "input",
            Steps =
            [
                new InputFlowStepDefinition { Name = "input" },
                new OutputFlowStepDefinition { Name = "completed", Outcome = FlowOutputOutcome.Success },
                new OutputFlowStepDefinition { Name = "error", Outcome = FlowOutputOutcome.Error }
            ],
            Transitions = [new("input-completed", "input", "completed", "completed")]
        };
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub(readOnly: false, definition));
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub());
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true)
            .SetResult(new Rectangle(0, 0, 1024, 768));

        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters
            .Add(component => component.ResourceId, "parent"));
        rendered.FindAll(".step-palette button").Single(button => button.TextContent.Trim() == "Agent").Click();

        var updated = context.Services.GetRequiredService<FlowEditorStore>().State.Resource!.Definition;
        var agent = updated.Steps.OfType<AgentFlowStepDefinition>().Single();
        var transition = updated.Transitions.Single(item => item.Event == "error");
        Assert.AreEqual(agent.Name, transition.FromStep);
        Assert.AreEqual("error", transition.ToStep);
        Assert.AreEqual($"{agent.Name}-error-error", transition.Id);
        Assert.IsFalse(rendered.FindAll(".step-palette button").Any(button => button.TextContent.Trim() == "Failure"));
    }

    [TestMethod]
    public void AddingAndSelectingAgentUsesTechnicalReferenceAndResourceDisplayName()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub(readOnly: false));
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub
        {
            Agents =
            [
                new("welcome-agent", "Welcome Agent", TestScope),
                new("packed-agent", "Pack Agent", TestScope) { Namespace = new("pack.demo") }
            ]
        });
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true)
            .SetResult(new Rectangle(0, 0, 1024, 768));

        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters
            .Add(component => component.ResourceId, "parent"));
        rendered.WaitForAssertion(() => Assert.HasCount(1, rendered.FindAll(".step-palette button").Where(button => button.TextContent.Trim() == "Agent")));
        rendered.FindAll(".step-palette button").Single(button => button.TextContent.Trim() == "Agent").Click();
        rendered.Find("[data-testid='configure-selected-step']").Click();

        var store = context.Services.GetRequiredService<FlowEditorStore>();
        rendered.WaitForAssertion(() => Assert.AreEqual("Welcome Agent", store.State.Resource!.Definition.Steps.OfType<AgentFlowStepDefinition>().Single().DisplayName));
        var step = store.State.Resource!.Definition.Steps.OfType<AgentFlowStepDefinition>().Single();
        Assert.AreEqual("welcome-agent", step.Agent.ResourceId);
        StringAssert.Contains(rendered.Find(".inspector-details-group").TextContent, "Step ID");
        rendered.Find("[data-testid='step-settings-tab-configuration']").Click();
        Assert.AreEqual($"/agents/welcome-agent?scopeRef={Uri.EscapeDataString(TestScope.Value)}", rendered.Find(".inspector-resource-link").GetAttribute("href"));
        Assert.AreEqual("_blank", rendered.Find(".inspector-resource-link").GetAttribute("target"));
        Assert.HasCount(1, rendered.FindAll(".inspector-resource-heading .inspector-resource-link"));
        StringAssert.Contains(rendered.Find(".inspector-resource-id").TextContent, "Agent ID");

        rendered.Find(".flow-inspector select").Change($"{TestScope.Value}|pack.demo|packed-agent");
        rendered.WaitForAssertion(() => Assert.AreEqual("Pack Agent", store.State.Resource!.Definition.Steps.OfType<AgentFlowStepDefinition>().Single().DisplayName));
        step = store.State.Resource!.Definition.Steps.OfType<AgentFlowStepDefinition>().Single();
        Assert.AreEqual("packed-agent", step.Agent.ResourceId);
        Assert.AreEqual(new ResourceNamespace("pack.demo"), step.Agent.Namespace);
        Assert.AreEqual($"/namespaces/pack.demo/agents/packed-agent?scopeRef={Uri.EscapeDataString(TestScope.Value)}", rendered.Find(".inspector-resource-link").GetAttribute("href"));
    }

    [TestMethod]
    public void DesignerUsesTheLoadedFlowOwnerScopeForEveryCatalog()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        var resources = new ResourceProviderStub();
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub(readOnly: false));
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(resources);
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true)
            .SetResult(new Rectangle(0, 0, 1024, 768));

        _ = context.Render<FlowDesignerComponent>(parameters => parameters
            .Add(component => component.ResourceId, "sample"));

        Assert.AreEqual(TestScope, resources.AgentContextScope);
        Assert.AreEqual(TestScope, resources.FlowContextScope);
        Assert.AreEqual(TestScope, resources.ToolContextScope);
    }

    [TestMethod]
    public void SelectingAnAgentWithADuplicateNamePersistsItsExactScope()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub(readOnly: false));
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub
        {
            Agents =
            [
                new("shared-agent", "Instance agent", ResourceScopeRef.Instance),
                new("shared-agent", "Workspace agent", TestScope)
            ]
        });
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true)
            .SetResult(new Rectangle(0, 0, 1024, 768));

        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters
            .Add(component => component.ResourceId, "sample"));
        rendered.FindAll(".step-palette button").Single(button => button.TextContent.Trim() == "Agent").Click();
        rendered.Find("[data-testid='configure-selected-step']").Click();
        rendered.Find("[data-testid='step-settings-tab-configuration']").Click();
        rendered.Find("#flow-agent-reference").Change($"{TestScope.Value}|default|shared-agent");

        var reference = context.Services.GetRequiredService<FlowEditorStore>().State.Resource!.Definition.Steps
            .OfType<AgentFlowStepDefinition>().Single().Agent;
        Assert.AreEqual("shared-agent", reference.ResourceId);
        Assert.AreEqual(TestScope, reference.ScopeRef);
    }

    [TestMethod]
    public void AgentStepWithoutAnAccessibleTargetShowsAnExplicitEmptySelection()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub(readOnly: false));
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub());
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true)
            .SetResult(new Rectangle(0, 0, 1024, 768));

        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters
            .Add(component => component.ResourceId, "sample"));
        rendered.FindAll(".step-palette button").Single(button => button.TextContent.Trim() == "Agent").Click();
        rendered.Find("[data-testid='configure-selected-step']").Click();
        rendered.Find("[data-testid='step-settings-tab-configuration']").Click();

        var options = rendered.Find("#flow-agent-reference").QuerySelectorAll("option");
        Assert.IsTrue(options.Any(option => option.TextContent.Trim() == "No agent selected"));
        Assert.IsFalse(options.Any(option => option.TextContent.Contains("unconfigured", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ExistingAgentReferenceThatIsNoLongerDiscoverableIsPreservedAsUnavailable()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        var definition = new FlowGraphDefinition
        {
            EntryStep = "input",
            Steps =
            [
                new InputFlowStepDefinition { Name = "input" },
                new AgentFlowStepDefinition
                {
                    Name = "agent",
                    DisplayName = "Welcome Agent",
                    Agent = new("welcome-agent") { ScopeRef = ResourceScopeRef.Instance }
                }
            ],
            Transitions = []
        };
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub(readOnly: false, definition));
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub());
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true)
            .SetResult(new Rectangle(0, 0, 1024, 768));

        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters
            .Add(component => component.ResourceId, "sample"));
        rendered.FindAll("button.flow-node-settings")
            .Single(button => button.GetAttribute("aria-label")!.Contains("Welcome Agent", StringComparison.Ordinal))
            .Click();
        rendered.Find("[data-testid='step-settings-tab-configuration']").Click();

        var options = rendered.Find("#flow-agent-reference").QuerySelectorAll("option");
        var unavailable = options.Single(option => option.TextContent.Contains("not available in this scope", StringComparison.Ordinal));
        StringAssert.Contains(unavailable.TextContent, "welcome-agent");
        Assert.IsFalse(options.Any(option => option.TextContent.Contains("Welcome Agent", StringComparison.Ordinal)));
        var reference = context.Services.GetRequiredService<FlowEditorStore>().State.Resource!.Definition.Steps
            .OfType<AgentFlowStepDefinition>().Single().Agent;
        Assert.AreEqual("welcome-agent", reference.ResourceId);
        Assert.AreEqual(ResourceScopeRef.Instance, reference.ScopeRef);
    }

    [TestMethod]
    public void SelectingARouterCandidateWithADuplicateNamePersistsItsExactScope()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub(readOnly: false));
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub
        {
            Agents =
            [
                new("shared-agent", "Instance agent", ResourceScopeRef.Instance),
                new("shared-agent", "Workspace agent", TestScope)
            ]
        });
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true)
            .SetResult(new Rectangle(0, 0, 1024, 768));

        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters
            .Add(component => component.ResourceId, "sample"));
        rendered.Find("[data-testid='flow-palette-router']").Click();
        rendered.Find("[data-testid='configure-selected-step']").Click();
        rendered.Find("[data-testid='step-settings-tab-configuration']").Click();
        rendered.FindAll("button").Single(button => button.TextContent.Contains("Add route", StringComparison.Ordinal)).Click();
        rendered.Find(".route-card select").Change($"{TestScope.Value}|default|shared-agent");

        var reference = context.Services.GetRequiredService<FlowEditorStore>().State.Resource!.Definition.Steps
            .OfType<RouterFlowStepDefinition>().Single().Candidates.Single().Agent;
        Assert.AreEqual("shared-agent", reference.ResourceId);
        Assert.AreEqual(TestScope, reference.ScopeRef);
    }

    [TestMethod]
    public async Task InspectorAlwaysShowsDisplayAndTechnicalResourceNames()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        var definition = new FlowGraphDefinition
        {
            EntryStep = "input",
            Steps =
            [
                new InputFlowStepDefinition { Name = "input" },
                new FlowCallStepDefinition
                {
                    Name = "child",
                    Flow = new("same-flow", FlowCallVersionStrategy.Exact, "1.0.0")
                },
                new AgentFlowStepDefinition
                {
                    Name = "agent",
                    Agent = new("same-agent")
                }
            ]
        };
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub(readOnly: false, definition));
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub
        {
            Agents = [new("same-agent", "same-agent", TestScope)],
            Flows = [new("same-flow", "same-flow", ResourceNamespace.Default, TestScope, "1.0.0")]
        });
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true)
            .SetResult(new Rectangle(0, 0, 1024, 768));

        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters
            .Add(component => component.ResourceId, "parent"));
        var canvas = rendered.FindComponent<FlowCanvas>();

        await rendered.InvokeAsync(() => canvas.Instance.SelectedStepChanged.InvokeAsync("child"));
        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual("same-flow", rendered.Find(".inspector-resource-summary-link strong").TextContent);
            Assert.AreEqual("same-flow", rendered.Find(".inspector-step-summary-copy code").TextContent);
        });

        await rendered.InvokeAsync(() => canvas.Instance.SelectedStepChanged.InvokeAsync("agent"));
        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual("same-agent", rendered.Find(".inspector-resource-summary-link strong").TextContent);
            Assert.AreEqual("same-agent", rendered.Find(".inspector-step-summary-copy code").TextContent);
        });
    }

    [TestMethod]
    public async Task SelectingLegacyGenericAgentLabelUsesCurrentResourceName()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        var definition = new FlowGraphDefinition
        {
            EntryStep = "input",
            Steps =
            [
                new InputFlowStepDefinition { Name = "input" },
                new AgentFlowStepDefinition { Name = "agent", DisplayName = "Agent", Agent = new("welcome-agent", ScopeRef: TestScope) }
            ],
            Transitions = [new("input-agent", "input", "completed", "agent")]
        };
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub(readOnly: false, definition));
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub
        {
            Agents = [new("welcome-agent", "Welcome Agent", TestScope)]
        });
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true)
            .SetResult(new Rectangle(0, 0, 1024, 768));

        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters
            .Add(component => component.ResourceId, "parent"));
        rendered.WaitForAssertion(() => Assert.HasCount(2, context.Services.GetRequiredService<FlowEditorStore>().State.Resource!.Definition.Steps));
        await rendered.InvokeAsync(() => rendered.FindComponent<FlowCanvas>().Instance.SelectedStepChanged.InvokeAsync("agent"));

        var step = context.Services.GetRequiredService<FlowEditorStore>().State.Resource!.Definition.Steps.OfType<AgentFlowStepDefinition>().Single();
        Assert.AreEqual("Welcome Agent", step.DisplayName);
        Assert.AreEqual("welcome-agent", step.Agent.ResourceId);
    }

    [TestMethod]
    public void EditablePaletteUsesOneGenericToolCardAndShowsTheSelectedSchema()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub(readOnly: false));
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub());
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true)
            .SetResult(new Rectangle(0, 0, 1024, 768));

        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters
            .Add(component => component.ResourceId, "parent"));
        IElement[] cards = [];
        rendered.WaitForAssertion(() =>
        {
            cards = rendered.FindAll(".step-palette button").Where(button => button.TextContent.Trim().EndsWith("Tool", StringComparison.Ordinal)).ToArray();
            Assert.HasCount(1, cards);
        });
        cards[0].Click();
        var toolLink = rendered.Find(".inspector-resource-summary-link");
        StringAssert.Contains(toolLink.TextContent, "Send notification");
        Assert.AreEqual($"/tools/notification.send?scopeRef={Uri.EscapeDataString(TestScope.Value)}", toolLink.GetAttribute("href"));
        Assert.AreEqual("_blank", toolLink.GetAttribute("target"));
        rendered.Find("[data-testid='configure-selected-step']").Click();
        rendered.Find("[data-testid='step-settings-tab-configuration']").Click();
        rendered.WaitForAssertion(() =>
        {
            StringAssert.Contains(rendered.Markup, "Called Tool");
        });
        Assert.AreEqual("Send notification", rendered.Find(".tool-picker-trigger span").TextContent);
        Assert.HasCount(2, rendered.FindAll(".tool-picker-option"));
        Assert.AreEqual("notification.send", rendered.Find(".inspector-resource-id code").TextContent);

        rendered.Find("[data-testid='step-settings-tab-input']").Click();
        StringAssert.Contains(rendered.Markup, "message");
        rendered.Find("[data-testid='tool-pass-transition-output']").Change(false);
        rendered.Find(".flow-mapping-editor input:not([type='checkbox'])").Change("hello");
        var store = context.Services.GetRequiredService<FlowEditorStore>();
        var toolStep = store.State.Resource!.Definition.Steps.OfType<ToolFlowStepDefinition>().Single();
        Assert.AreEqual("hello", toolStep.ArgumentsMapping?.GetProperty("message").GetString());
        rendered.Find("[data-testid='step-settings-tab-output']").Click();
        StringAssert.Contains(rendered.Markup, "deliveryId");
        rendered.Find("[data-testid='step-settings-tab-configuration']").Click();
        rendered.FindAll(".tool-picker-option").Single(option =>
            string.Equals(option.GetAttribute("aria-pressed"), "true", StringComparison.OrdinalIgnoreCase)).Click();
        toolStep = store.State.Resource!.Definition.Steps.OfType<ToolFlowStepDefinition>().Single();
        Assert.AreEqual("hello", toolStep.ArgumentsMapping?.GetProperty("message").GetString());

        rendered.Find(".tool-picker-search input").Input("warehouse");
        Assert.HasCount(1, rendered.FindAll(".tool-picker-option"));
        rendered.Find(".tool-picker-option").Click();
        rendered.Find("[data-testid='step-settings-tab-input']").Click();
        rendered.WaitForAssertion(() => StringAssert.Contains(rendered.Markup, "requestId"));
        toolStep = store.State.Resource!.Definition.Steps.OfType<ToolFlowStepDefinition>().Single();
        Assert.AreEqual("warehouse.lookup", toolStep.Tool.ResourceId);
        Assert.AreEqual(new ResourceNamespace("pack.tools"), toolStep.Tool.Namespace);
        rendered.Find("[data-testid='step-settings-tab-configuration']").Click();
        Assert.AreEqual("warehouse.lookup", rendered.Find(".inspector-resource-id code").TextContent);
    }

    [TestMethod]
    public async Task PublishedToolStepShowsItsSelectionWithoutEditableOptions()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        var definition = new FlowGraphDefinition
        {
            EntryStep = "input",
            Steps =
            [
                new InputFlowStepDefinition { Name = "input" },
                new ToolFlowStepDefinition
                {
                    Name = "tool",
                    Tool = new("notification.send", ResourceNamespace.Default),
                    ArgumentsMapping = JsonSerializer.SerializeToElement(new { })
                }
            ],
            Transitions = [new("input-tool", "input", "completed", "tool")]
        };
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub(readOnly: true, definition));
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub());
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true)
            .SetResult(new Rectangle(0, 0, 1024, 768));

        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters
            .Add(component => component.ResourceId, "sample")
            .Add(component => component.Namespace, new ResourceNamespace("pack.sample")));
        await rendered.InvokeAsync(() => rendered.FindComponent<FlowCanvas>().Instance.StepSettingsRequested.InvokeAsync("tool"));
        rendered.Find("[data-testid='step-settings-tab-configuration']").Click();

        Assert.AreEqual("Send notification", rendered.Find(".tool-picker-trigger.is-readonly span").TextContent);
        Assert.HasCount(0, rendered.FindAll(".tool-picker-option"));
    }
}
