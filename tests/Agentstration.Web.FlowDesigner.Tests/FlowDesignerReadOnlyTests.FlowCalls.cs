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
    public void EditablePaletteUsesOneGenericFlowCardAndShowsTheSelectedContract()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        var definition = new FlowGraphDefinition
        {
            EntryStep = "input",
            Steps =
            [
                new InputFlowStepDefinition { Name = "input" },
                new OutputFlowStepDefinition { Name = "error", Outcome = FlowOutputOutcome.Error }
            ]
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
        IElement[] cards = [];
        rendered.WaitForAssertion(() =>
        {
            cards = rendered.FindAll(".step-palette button").Where(button => button.TextContent.Trim().EndsWith("Flow", StringComparison.Ordinal)).ToArray();
            Assert.HasCount(1, cards);
        });
        cards[0].Click();
        var flowSummaryLink = rendered.Find(".inspector-resource-summary-link");
        StringAssert.Contains(flowSummaryLink.TextContent, "News analysis");
        Assert.AreEqual($"/namespaces/pack.news/flows/analysis?scopeRef={Uri.EscapeDataString(TestScope.Value)}", flowSummaryLink.GetAttribute("href"));
        Assert.AreEqual("_blank", flowSummaryLink.GetAttribute("target"));
        rendered.Find("[data-testid='configure-selected-step']").Click();
        rendered.Find("[data-testid='step-settings-tab-configuration']").Click();
        rendered.WaitForAssertion(() =>
        {
            StringAssert.Contains(rendered.Markup, "Called Flow");
            StringAssert.Contains(rendered.Markup, "summary");
        });
        Assert.AreEqual($"/namespaces/pack.news/flows/analysis?scopeRef={Uri.EscapeDataString(TestScope.Value)}", rendered.Find(".inspector-resource-link").GetAttribute("href"));
        Assert.AreEqual("_blank", rendered.Find(".inspector-resource-link").GetAttribute("target"));
        Assert.HasCount(1, rendered.FindAll(".inspector-resource-heading .inspector-resource-link"));
        StringAssert.Contains(rendered.Find(".inspector-resource-id").TextContent, "Flow ID");

        var call = Assert.IsInstanceOfType<FlowCallStepDefinition>(context.Services.GetRequiredService<FlowEditorStore>()
            .State.Resource!.Definition.Steps.Single(step => step.Type() == "flow"));
        Assert.AreEqual(JsonValueKind.String, call.InputMapping?.ValueKind);
        Assert.AreEqual("${transition.output}", call.InputMapping?.GetString());
        rendered.Find("[data-testid='step-settings-tab-input']").Click();
        Assert.IsTrue(rendered.Find("[data-testid='flow-pass-transition-output']").HasAttribute("checked"));
        StringAssert.Contains(rendered.Markup, "Pass incoming transition output");
        var errorTransition = context.Services.GetRequiredService<FlowEditorStore>().State.Resource!.Definition.Transitions
            .Single(transition => transition.FromStep == call.Name && transition.ToStep == "error");
        Assert.AreEqual("rejected", errorTransition.Event);

        rendered.Find("[data-testid='flow-pass-transition-output']").Change(false);
        call = Assert.IsInstanceOfType<FlowCallStepDefinition>(context.Services.GetRequiredService<FlowEditorStore>()
            .State.Resource!.Definition.Steps.Single(step => step.Type() == "flow"));
        Assert.AreEqual(JsonValueKind.Object, call.InputMapping?.ValueKind);
        rendered.WaitForAssertion(() => StringAssert.Contains(rendered.Markup, "article"));
    }

    [TestMethod]
    public async Task ExistingFlowCardLoadsItsContractWhenSelectedAndPersistsPassthrough()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        var definition = new FlowGraphDefinition
        {
            EntryStep = "input",
            Steps =
            [
                new InputFlowStepDefinition { Name = "input" },
                new AgentFlowStepDefinition { Name = "analyze", DisplayName = "Analyze news", Agent = new("news-agent") },
                new FlowCallStepDefinition
                {
                    Name = "deliver",
                    Flow = new("analysis", Namespace: new("pack.news")),
                    InputMapping = JsonSerializer.SerializeToElement(new { })
                }
            ],
            Transitions =
            [
                new("input-analyze", "input", "completed", "analyze"),
                new("analyze-deliver", "analyze", "success", "deliver")
            ]
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
        var canvas = rendered.FindComponent<FlowCanvas>();
        await rendered.InvokeAsync(() => canvas.Instance.StepSettingsRequested.InvokeAsync("deliver"));
        rendered.Find("[data-testid='step-settings-tab-input']").Click();

        rendered.WaitForAssertion(() => Assert.HasCount(1, rendered.FindAll("[data-testid='flow-pass-transition-output']")));
        rendered.Find("[data-testid='flow-pass-transition-output']").Change(true);
        var call = Assert.IsInstanceOfType<FlowCallStepDefinition>(context.Services.GetRequiredService<FlowEditorStore>()
            .State.Resource!.Definition.Steps.Single(step => step.Name == "deliver"));
        Assert.AreEqual("${transition.output}", call.InputMapping?.GetString());
    }

    [TestMethod]
    public void FlowCallNamedPortsAreResolvedAtInitialLoadWithoutSelectingTheNode()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        var definition = new FlowGraphDefinition
        {
            EntryStep = "input",
            Steps =
            [
                new InputFlowStepDefinition { Name = "input" },
                new FlowCallStepDefinition { Name = "deliver", Flow = new("analysis", Namespace: new("pack.news")) },
                new OutputFlowStepDefinition { Name = "done" },
                new OutputFlowStepDefinition { Name = "error", Outcome = FlowOutputOutcome.Error }
            ],
            Transitions =
            [
                new("input-deliver", "input", "completed", "deliver"),
                new("old-success-id", "deliver", "completed", "done"),
                new("deliver-approved-done", "deliver", "approved", "done"),
                new("unexpected-id", "deliver", "obsolete-error", "error"),
                new("deliver-rejected-error", "deliver", "rejected", "error")
            ]
        };
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub(readOnly: false, definition));
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub());
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true).SetResult(new Rectangle(0, 0, 1024, 768));

        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters.Add(component => component.ResourceId, "parent"));
        var store = context.Services.GetRequiredService<FlowEditorStore>();

        rendered.WaitForAssertion(() =>
        {
            var node = store.State.Diagram.Nodes.Single(item => item.Name == "deliver");
            CollectionAssert.AreEqual(new[] { "approved", "rejected" }, node.OutputEvents.ToArray());
            Assert.AreEqual(FlowOutputOutcome.Error, node.OutputOutcomes["rejected"]);
            Assert.IsFalse(store.State.Resource!.Definition.Transitions.Any(transition => transition.Event == "obsolete-error"));
            Assert.IsFalse(store.State.Resource.Definition.Transitions.Any(transition => transition.Event == "completed" && transition.FromStep == "deliver"));
            Assert.HasCount(2, store.State.Resource.Definition.Transitions.Where(transition => transition.FromStep == "deliver"));
            var renderedNode = GetDiagram(rendered.FindComponent<FlowCanvas>().Instance).Nodes
                .OfType<FlowDiagramNode>()
                .Single(node => node.Source.Name == "deliver");
            Assert.HasCount(2, renderedNode.Outputs);
        });

        rendered.Find("[data-testid='flow-designer-validation-run']").Click();
        rendered.WaitForAssertion(() =>
        {
            var backend = context.Services.GetRequiredService<IFlowDesignerBackend>() as BackendStub;
            Assert.IsNotNull(backend);
            Assert.AreEqual(1, backend.ValidationCount);
            Assert.IsFalse(backend.LastSavedDefinition!.Transitions.Any(transition => transition.Event == "obsolete-error"));
            Assert.IsFalse(backend.LastSavedDefinition.Transitions.Any(transition => transition.Event == "completed" && transition.FromStep == "deliver"));
        });
    }

    [TestMethod]
    public async Task ChangingExactFlowVersionReplacesTheNamedPorts()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        var definition = new FlowGraphDefinition
        {
            EntryStep = "child",
            Steps = [new FlowCallStepDefinition { Name = "child", Flow = new("analysis", FlowCallVersionStrategy.Exact, "1.0.0", new("pack.news")) }, new OutputFlowStepDefinition { Name = "done" }]
        };
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub(readOnly: false, definition));
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub
        {
            FlowVersions = new Dictionary<string, IReadOnlyList<FlowDesignerFlowVersion>>(StringComparer.Ordinal)
            {
                ["pack.news|analysis"] =
                [
                    new("1.0.0", null, null, [new("accepted", null, FlowOutputOutcome.Success, null)]),
                    new("2.0.0", null, null, [new("completed", null, FlowOutputOutcome.Success, null), new("failed", null, FlowOutputOutcome.Error, null)])
                ]
            }
        });
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true).SetResult(new Rectangle(0, 0, 1024, 768));
        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters.Add(component => component.ResourceId, "parent"));
        var store = context.Services.GetRequiredService<FlowEditorStore>();
        rendered.WaitForAssertion(() => CollectionAssert.AreEqual(new[] { "accepted" }, store.State.Diagram.Nodes.Single(node => node.Name == "child").OutputEvents.ToArray()));
        await rendered.InvokeAsync(() => rendered.FindComponent<FlowCanvas>().Instance.StepSettingsRequested.InvokeAsync("child"));
        rendered.Find("[data-testid='step-settings-tab-configuration']").Click();

        rendered.FindAll(".flow-inspector label").Single(element => element.TextContent.StartsWith("Exact version", StringComparison.Ordinal)).QuerySelector("select")!.Change("2.0.0");

        rendered.WaitForAssertion(() => CollectionAssert.AreEqual(new[] { "completed", "failed" }, store.State.Diagram.Nodes.Single(node => node.Name == "child").OutputEvents.ToArray()));
    }

    [TestMethod]
    public async Task ChangingCalledFlowRemovesAnAutomaticErrorRouteWhenNoErrorOutcomeRemains()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        var definition = new FlowGraphDefinition
        {
            EntryStep = "child",
            Steps =
            [
                new FlowCallStepDefinition { Name = "child", Flow = new("analysis", FlowCallVersionStrategy.Exact, "1.0.0", new("pack.news")) },
                new OutputFlowStepDefinition { Name = "done" },
                new OutputFlowStepDefinition { Name = "error", Outcome = FlowOutputOutcome.Error }
            ],
            Transitions =
            [
                new("child-completed", "child", "completed", "done"),
                new("child-rejected", "child", "rejected", "error")
            ]
        };
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub(readOnly: false, definition));
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub
        {
            Flows =
            [
                new("analysis", "Analysis", new("pack.news"), TestScope, "1.0.0"),
                new("summary", "Summary", new("pack.news"), TestScope, "1.0.0")
            ],
            FlowVersions = new Dictionary<string, IReadOnlyList<FlowDesignerFlowVersion>>(StringComparer.Ordinal)
            {
                ["pack.news|analysis"] = [new("1.0.0", null, null, [new("completed", null, FlowOutputOutcome.Success, null), new("rejected", null, FlowOutputOutcome.Error, null)])],
                ["pack.news|summary"] = [new("1.0.0", null, null, [new("completed", null, FlowOutputOutcome.Success, null)])]
            }
        });
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true).SetResult(new Rectangle(0, 0, 1024, 768));
        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters.Add(component => component.ResourceId, "parent"));
        var store = context.Services.GetRequiredService<FlowEditorStore>();
        rendered.WaitForAssertion(() => Assert.IsTrue(store.State.Resource!.Definition.Transitions.Any(transition => transition.Event == "rejected")));
        await rendered.InvokeAsync(() => rendered.FindComponent<FlowCanvas>().Instance.StepSettingsRequested.InvokeAsync("child"));
        rendered.Find("[data-testid='step-settings-tab-configuration']").Click();

        rendered.Find("[data-testid='flow-call-reference']").Change($"{TestScope.Value}|pack.news|summary");

        rendered.WaitForAssertion(() =>
        {
            var transitions = store.State.Resource!.Definition.Transitions.Where(transition => transition.FromStep == "child").ToArray();
            Assert.HasCount(1, transitions);
            Assert.AreEqual("completed", transitions[0].Event);
            CollectionAssert.AreEqual(new[] { "completed" }, store.State.Diagram.Nodes.Single(node => node.Name == "child").OutputEvents.ToArray());
        });
    }

    [TestMethod]
    public async Task FlowCallInspectorShowsPublishedInputAndNamedOutputContracts()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        var inputSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { request = new { type = "string" } } });
        var outputSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { answer = new { type = "string" } } });
        var definition = new FlowGraphDefinition
        {
            EntryStep = "child",
            Steps =
            [
                new FlowCallStepDefinition { Name = "child", Flow = new("analysis", FlowCallVersionStrategy.Exact, "1.0.0", new("pack.news")) },
                new OutputFlowStepDefinition { Name = "result" }
            ],
            Transitions = [new("child-completed", "child", "completed", "result")]
        };
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub(readOnly: false, definition));
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub
        {
            FlowVersions = new Dictionary<string, IReadOnlyList<FlowDesignerFlowVersion>>(StringComparer.Ordinal)
            {
                ["pack.news|analysis"] =
                [
                    new("1.0.0", inputSchema, null,
                        [new("completed", "Completed", FlowOutputOutcome.Success, outputSchema)])
                ]
            }
        });
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true).SetResult(new Rectangle(0, 0, 1024, 768));
        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters.Add(component => component.ResourceId, "parent"));

        await rendered.InvokeAsync(() => rendered.FindComponent<FlowCanvas>().Instance.StepSettingsRequested.InvokeAsync("child"));
        var flowLink = rendered.Find(".inspector-resource-summary-link");
        StringAssert.Contains(flowLink.TextContent, "News analysis");
        Assert.AreEqual($"/namespaces/pack.news/flows/analysis?scopeRef={Uri.EscapeDataString(TestScope.Value)}", flowLink.GetAttribute("href"));
        Assert.AreEqual("_blank", flowLink.GetAttribute("target"));
        rendered.Find("[data-testid='step-settings-tab-input']").Click();
        Assert.AreEqual("string", rendered.FindComponent<SchemaDocumentEditor>().Instance.Value
            .GetProperty("properties").GetProperty("request").GetProperty("type").GetString());

        rendered.Find("[data-testid='step-settings-tab-output']").Click();
        var namedOutput = rendered.Find("[data-testid='called-flow-output-contract']");
        StringAssert.Contains(namedOutput.TextContent, "Completed");
        Assert.AreEqual("string", rendered.FindComponent<SchemaDocumentEditor>().Instance.Value
            .GetProperty("properties").GetProperty("answer").GetProperty("type").GetString());

        await rendered.InvokeAsync(() => rendered.FindComponent<FlowCanvas>().Instance.StepSettingsRequested.InvokeAsync("result"));
        rendered.Find("[data-testid='step-settings-tab-output']").Click();
        Assert.AreEqual("string", rendered.FindComponent<SchemaDocumentEditor>().Instance.Value
            .GetProperty("properties").GetProperty("answer").GetProperty("type").GetString());
    }
}
