using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Schemas.Contracts;
using Agentstration.Web.FlowDesigner.Backend;
using Agentstration.Web.FlowDesigner.Components;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.FlowDesigner.Tests;

public sealed partial class FlowDesignerReadOnlyTests
{
    [TestMethod]
    [DataRow("en-US", "Optional.")]
    [DataRow("fr-FR", "Facultatif.")]
    public void FunctionSchemaParametersUseTheSharedEditorOutsideTheRuntimeMapping(string culture, string help)
    {
        using var currentCulture = new CultureScope(culture);
        using var context = CreateContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var input = JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"value":{},"itemSchema":{"type":"object","x-agentstration-schema-binding":true}}}""");
        var mapping = JsonSerializer.Deserialize<JsonElement>("""{"value":"${input}","path":""}""");
        var parent = JsonSerializer.Deserialize<JsonElement>("""{"type":"array","items":{"type":"string"}}""");
        var schema = new FlowDesignerSchemaVersion("items", "Items", Agentstration.Resources.ResourceNamespace.Default,
            TestScope, "A", "json-schema", null, parent);
        TransformFlowStepDefinition step = new() { Name = "explode", Function = new("explode", "2.0.0", TestScope),
            FunctionInputSchema = input, Mapping = mapping };
        var component = context.Render<FlowTransformInspector>(parameters => parameters
            .Add(c => c.Step, step).Add(c => c.ActiveTab, FlowStepSettingsTab.Configuration)
            .Add(c => c.SchemaVersions, new[] { schema }).Add(c => c.Changed, value => step = (TransformFlowStepDefinition)value));
        const string id = "transform-function-schema-itemSchema";
        Assert.Contains(help, component.Find($"[data-testid={id}]").TextContent);
        Assert.IsNull(step.FunctionSchemas);
        component.Find($"[data-testid={id}-start-shared]").Click();
        component.Render(parameters => parameters.Add(c => c.Step, step));
        component.Find($"[data-testid={id}-path]").Change("/items");
        Assert.AreEqual("A", step.FunctionSchemas!["itemSchema"].Reference!.Version);
        Assert.AreEqual("/items", step.FunctionSchemas["itemSchema"].Reference!.SchemaPath);
        Assert.IsTrue(JsonElement.DeepEquals(mapping, step.Mapping!.Value));
        component.Render(parameters => parameters.Add(c => c.Step, step).Add(c => c.ReadOnly, true));
        Assert.IsTrue(component.Find($"[data-testid={id}-path]").HasAttribute("disabled"));
        component.Render(parameters => parameters.Add(c => c.ReadOnly, false));
        component.Find($"[data-testid={id}-remove]").Click();
        Assert.IsNull(step.FunctionSchemas);
        component.Render(parameters => parameters.Add(c => c.Step, step));
        component.Find($"[data-testid={id}-start-inline]").Click();
        Assert.IsNotNull(step.FunctionSchemas!["itemSchema"].Inline);
        Assert.IsNull(step.FunctionSchemaSnapshots);
    }
}
