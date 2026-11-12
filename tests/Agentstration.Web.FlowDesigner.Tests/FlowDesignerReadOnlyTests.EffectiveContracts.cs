using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Web.Components.Schemas;
using Agentstration.Web.FlowDesigner.Backend;
using Agentstration.Web.FlowDesigner.Components;
using Agentstration.Web.FlowDesigner.State;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.FlowDesigner.Tests;

public sealed partial class FlowDesignerReadOnlyTests
{
    [TestMethod]
    public void AgentInspectorShowsTheEffectiveContractAndPinnedVersionWinsInReadOnlyMode()
    {
        using var context = CreateContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var pinned = JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"pinned":{"type":"string"}}}""");
        var current = JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"current":{"type":"string"}}}""");
        var step = new AgentFlowStepDefinition { Name = "agent", Agent = new("redactor") { InputSchema = pinned, OutputSchema = pinned } };
        var rendered = context.Render<FlowAgentInspector>(parameters => parameters.Add(value => value.Step, step)
            .Add(value => value.ActiveTab, FlowStepSettingsTab.Input).Add(value => value.ReadOnly, true)
            .Add(value => value.SelectedAgent, new("redactor", "Redactor", TestScope) { InputSchema = current }));
        Assert.Contains("pinned", rendered.FindComponent<SchemaDocumentEditor>().Instance.Value.GetRawText());
        Assert.IsTrue(rendered.FindComponent<SchemaDocumentEditor>().Instance.ReadOnly);
        Assert.IsTrue(rendered.Find("[data-testid='agent-input-mapping']").HasAttribute("disabled"));
        rendered.Render(parameters => parameters.Add(value => value.ActiveTab, FlowStepSettingsTab.Output));
        Assert.Contains("pinned", rendered.FindComponent<SchemaDocumentEditor>().Instance.Value.GetRawText());
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, true)]
    public void ConnectionChecksHonorRequiredFieldsAndTheEffectiveMapping(bool required, bool mismatch)
    {
        using var context = CreateContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true).SetResult(new(0, 0, 1024, 768));
        var output = JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"utc":{"type":"string"}},"required":["utc"],"additionalProperties":false}""");
        var input = JsonSerializer.SerializeToElement(new { type = "object", properties = new { field1 = new { type = "string" } }, required = required ? new[] { "field1" } : Array.Empty<string>(), additionalProperties = true });
        var graph = new FlowGraphDefinition { EntryStep = "input", Steps = [new InputFlowStepDefinition { Name = "input" },
            new ToolFlowStepDefinition { Name = "clock", Tool = new("datetime.get") { OutputSchema = output } },
            new AgentFlowStepDefinition { Name = "agent", Agent = new("redactor") { InputSchema = input }, InputMapping = JsonSerializer.SerializeToElement("${transition.output}") }],
            Transitions = [new("clock-agent", "clock", "completed", "agent")] };
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub(false, graph));
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub());
        context.Services.AddSingleton<FlowEditorStore>();
        var rendered = context.Render<Agentstration.Web.FlowDesigner.Components.FlowDesigner>(parameters => parameters.Add(value => value.ResourceId, "parent"));
        Assert.AreEqual(mismatch ? 1 : 0, rendered.FindAll("[data-testid='flow-connection-contract-warning']").Count);
        // An explicit mapping satisfies field1 even though the original tool output has another shape.
        var store = context.Services.GetRequiredService<FlowEditorStore>();
        var mapped = (AgentFlowStepDefinition)graph.Steps[2];
        rendered.InvokeAsync(async () => await store.DispatchAsync(new UpdateStepCommand(mapped with { InputMapping = JsonSerializer.Deserialize<JsonElement>("""{"field1":"${transition.output.utc}"}""") }), default)).GetAwaiter().GetResult();
        rendered.WaitForAssertion(() => Assert.HasCount(0, rendered.FindAll("[data-testid='flow-connection-contract-warning']")));
    }
}
