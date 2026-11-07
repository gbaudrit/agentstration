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
    public async Task OutputInspectorShowsCanonicalMappingAndInheritedSchemaAndEditsErrorDetails()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        var schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { result = new { type = "string" } } });
        var definition = new FlowGraphDefinition
        {
            EntryStep = "input",
            Steps =
            [
                new InputFlowStepDefinition { Name = "input", Schema = schema },
                new OutputFlowStepDefinition { Name = "result", Outcome = FlowOutputOutcome.Error, OutputMapping = JsonSerializer.SerializeToElement("${transition.output}") }
            ],
            Transitions = [new("input-result", "input", "completed", "result")]
        };
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub(readOnly: false, definition));
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub());
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true).SetResult(new Rectangle(0, 0, 1024, 768));
        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters.Add(component => component.ResourceId, "parent"));
        await rendered.InvokeAsync(() => rendered.FindComponent<FlowCanvas>().Instance.StepSettingsRequested.InvokeAsync("result"));
        rendered.Find("[data-testid='step-settings-tab-output']").Click();

        StringAssert.Contains(rendered.Find("[data-testid='terminal-output-contract']").TextContent, "Same as input contract");
        var mapping = rendered.Find("[data-testid='output-mapping']");
        Assert.IsEmpty(mapping.QuerySelectorAll("input, textarea"));
        Assert.AreEqual("${transition.output}", mapping.QuerySelector("code")!.TextContent);
        StringAssert.Contains(mapping.TextContent, "Incoming transition output");
        Assert.AreEqual("object", rendered.FindComponent<SchemaDocumentEditor>().Instance.Value.GetProperty("type").GetString());
        rendered.Find("[data-testid='step-settings-tab-configuration']").Click();
        rendered.FindAll(".flow-inspector input").Single(element => element.ParentElement!.TextContent.Contains("Error details expression", StringComparison.Ordinal)).Change("${transition.output.details}");

        var output = context.Services.GetRequiredService<FlowEditorStore>().State.Resource!.Definition.Steps.OfType<OutputFlowStepDefinition>().Single();
        Assert.AreEqual(JsonValueKind.String, output.OutputMapping?.ValueKind);
        Assert.AreEqual("${transition.output}", output.OutputMapping?.GetString());
        Assert.IsNull(output.Schema);
        Assert.IsNull(output.SchemaBinding);
        Assert.AreEqual("${transition.output.details}", output.DetailsExpression);
    }

    [TestMethod]
    public async Task TransformInspectorPinsAnExactPublishedSharedOutputSchemaVersion()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        var definition = new FlowGraphDefinition
        {
            EntryStep = "input",
            Steps =
            [
                new InputFlowStepDefinition { Name = "input" },
                new TransformFlowStepDefinition { Name = "transform", Mapping = JsonSerializer.SerializeToElement("${transition.output}") },
                new OutputFlowStepDefinition { Name = "result" }
            ],
            Transitions =
            [
                new("input-transform", "input", "completed", "transform"),
                new("transform-result", "transform", "completed", "result")
            ]
        };
        var scope = ResourceScopeRef.Workspace(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub(readOnly: false, definition));
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub
        {
            SchemaVersions =
            [
                new("task-result", "Task result", ResourceNamespace.Default, scope, "1.2.0", "json-schema",
                    "https://json-schema.org/draft/2020-12/schema",
                    JsonSerializer.SerializeToElement(new { type = "object", properties = new { answer = new { type = "string" } } }))
            ]
        });
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true).SetResult(new Rectangle(0, 0, 1024, 768));
        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters.Add(component => component.ResourceId, "parent"));
        await rendered.InvokeAsync(() => rendered.FindComponent<FlowCanvas>().Instance.StepSettingsRequested.InvokeAsync("transform"));
        rendered.Find("[data-testid='step-settings-tab-output']").Click();

        rendered.Find("[data-testid='transform-output-schema-start-shared']").Click();

        var transform = context.Services.GetRequiredService<FlowEditorStore>().State.Resource!.Definition.Steps
            .OfType<TransformFlowStepDefinition>().Single();
        Assert.IsNull(transform.Schema);
        Assert.AreEqual("task-result", transform.SchemaBinding?.Reference?.Name);
        Assert.AreEqual("1.2.0", transform.SchemaBinding?.Reference?.Version);
        Assert.AreEqual(scope, transform.SchemaBinding?.Reference?.ScopeRef);

        rendered.Find("[data-testid='transform-output-schema-remove']").Click();

        transform = context.Services.GetRequiredService<FlowEditorStore>().State.Resource!.Definition.Steps
            .OfType<TransformFlowStepDefinition>().Single();
        Assert.IsNull(transform.Schema);
        Assert.IsNull(transform.SchemaBinding);
        StringAssert.Contains(rendered.Find("[data-testid='transform-output-schema-empty']").TextContent, "No contract is configured");
    }

    [TestMethod]
    public async Task InputInspectorCanRemoveALegacyRootContractAndCreateANewInlineContract()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        var schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { request = new { type = "string" } } });
        var definition = new FlowGraphDefinition
        {
            EntryStep = "input",
            InputSchema = schema,
            Steps = [new InputFlowStepDefinition { Name = "input" }],
            Transitions = []
        };
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub(readOnly: false, definition));
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub());
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true).SetResult(new Rectangle(0, 0, 1024, 768));
        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters.Add(component => component.ResourceId, "parent"));

        await rendered.InvokeAsync(() => rendered.FindComponent<FlowCanvas>().Instance.StepSettingsRequested.InvokeAsync("input"));
        rendered.Find("[data-testid='step-settings-tab-input']").Click();
        Assert.HasCount(0, rendered.FindAll(".step-contract-source"));
        rendered.Find("[data-testid='flow-input-schema-remove']").Click();

        var updated = context.Services.GetRequiredService<FlowEditorStore>().State.Resource!.Definition;
        var input = updated.Steps.OfType<InputFlowStepDefinition>().Single();
        Assert.IsNull(updated.InputSchema);
        Assert.IsNull(updated.InputSchemaBinding);
        Assert.IsNull(input.Schema);
        Assert.IsNull(input.SchemaBinding);
        StringAssert.Contains(rendered.Find("[data-testid='flow-input-schema-empty']").TextContent, "No contract is configured");

        rendered.Find("[data-testid='flow-input-schema-start-inline']").Click();

        updated = context.Services.GetRequiredService<FlowEditorStore>().State.Resource!.Definition;
        input = updated.Steps.OfType<InputFlowStepDefinition>().Single();
        Assert.IsNull(updated.InputSchema);
        Assert.IsNotNull(input.SchemaBinding?.Inline);
        Assert.AreEqual("object", input.SchemaBinding.Inline.Content.GetProperty("type").GetString());
    }

    [TestMethod]
    public async Task ConditionEditsFollowTheSelectedStepAcrossSettingsReopens()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        var definition = new FlowGraphDefinition
        {
            EntryStep = "first",
            Steps =
            [
                new ConditionFlowStepDefinition { Name = "first", Left = "first-left", Operator = "equals", Right = "first-right" },
                new ConditionFlowStepDefinition { Name = "second", Left = "second-left", Operator = "equals", Right = "second-right" }
            ]
        };
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub(readOnly: false, definition));
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub());
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true)
            .SetResult(new Rectangle(0, 0, 1024, 768));
        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters.Add(component => component.ResourceId, "parent"));
        var store = context.Services.GetRequiredService<FlowEditorStore>();

        await rendered.InvokeAsync(() => rendered.FindComponent<FlowCanvas>().Instance.StepSettingsRequested.InvokeAsync("first"));
        rendered.Find("[data-testid='step-settings-tab-configuration']").Click();
        rendered.FindAll(".step-settings-form input")[0].Change("updated-left");
        rendered.FindAll(".step-settings-form input")[1].Change("contains");
        rendered.FindAll(".step-settings-form input")[2].Change("updated-right");

        var first = store.State.Resource!.Definition.Steps.OfType<ConditionFlowStepDefinition>().Single(step => step.Name == "first");
        Assert.AreEqual("updated-left", first.Left);
        Assert.AreEqual("contains", first.Operator);
        Assert.AreEqual("updated-right", first.Right);

        rendered.Find(".step-settings-close").Click();
        await rendered.InvokeAsync(() => rendered.FindComponent<FlowCanvas>().Instance.StepSettingsRequested.InvokeAsync("second"));
        rendered.Find("[data-testid='step-settings-tab-configuration']").Click();
        Assert.AreEqual("second-left", rendered.FindAll(".step-settings-form input")[0].GetAttribute("value"));
        rendered.FindAll(".step-settings-form input")[2].Change("second-updated");

        var conditions = store.State.Resource!.Definition.Steps.OfType<ConditionFlowStepDefinition>().ToArray();
        Assert.AreEqual(first, conditions.Single(step => step.Name == "first"));
        Assert.AreEqual("second-updated", conditions.Single(step => step.Name == "second").Right);
        rendered.Render(parameters => parameters.Add(component => component.IsReadOnly, true));
        Assert.IsTrue(rendered.FindAll(".step-settings-form input").All(input => input.HasAttribute("disabled")));
    }
}
