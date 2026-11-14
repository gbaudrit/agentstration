using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Functions.Contracts;
using Agentstration.Web.FlowDesigner.Backend;
using Agentstration.Web.FlowDesigner.Components;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.FlowDesigner.Tests;

public sealed partial class FlowDesignerReadOnlyTests
{
    [TestMethod]
    [DataRow("en-US", "Select a JSON value", "Remove duplicate array elements")]
    [DataRow("fr-FR", "Extrait une valeur JSON", "Supprime les doublons du tableau")]
    public async Task TransformSelectionDisplaysTheCurrentLocalizedFunctionDescription(string locale, string selectText, string distinctText)
    {
        using var culture = new CultureScope(locale);
        using var context = CreateContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var schema = JsonSerializer.SerializeToElement(new { });
        FlowDesignerFunction Choice(string id) => new(id, new(new(id, "1.0.0", TestScope), new("native", "1.0.0", TestScope), id, "1.0.0", schema, schema)) { IsBuiltIn = true };
        var select = Choice("json.select");
        var distinct = Choice("collection.distinct");
        TransformFlowStepDefinition step = new() { Name = "transform", Function = select.Function.Reference };
        var rendered = context.Render<FlowTransformInspector>(parameters => parameters
            .Add(value => value.Step, step).Add(value => value.Functions, new[] { select, distinct })
            .Add(value => value.ActiveTab, FlowStepSettingsTab.Configuration)
            .Add(value => value.Changed, value => step = (TransformFlowStepDefinition)value));
        Assert.Contains(selectText, rendered.Find("[data-testid=function-description]").TextContent);
        var reference = distinct.Function.Reference;
        await rendered.Find("[data-testid=transform-function]").ChangeAsync($"{reference.ScopeRef}|{reference.Namespace}|{reference.Name}|{reference.Version}");
        rendered.Render(parameters => parameters.Add(value => value.Step, step));
        Assert.Contains(distinctText, rendered.Find("[data-testid=function-description]").TextContent);
        await rendered.Find("[data-testid=transform-function]").ChangeAsync("");
        rendered.Render(parameters => parameters.Add(value => value.Step, step));
        Assert.IsEmpty(rendered.FindAll("[data-testid=function-description]"));
    }
}
