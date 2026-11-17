using System.Text.Json;
using Agentstration.Schemas.Contracts;
using Agentstration.Resources;
using Agentstration.Web.FlowDesigner.Backend;
using Agentstration.Web.FlowDesigner.Components;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.FlowDesigner.Tests;

public sealed partial class FlowDesignerReadOnlyTests
{
    [TestMethod]
    [DataRow("en-US", "Sub-schema path")]
    [DataRow("fr-FR", "Chemin du sous-schéma")]
    public void SharedSchemaSelectionPinsTheParentVersionAndRejectsMissingPaths(string culture, string label)
    {
        using var currentCulture = new CultureScope(culture);
        using var context = CreateContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var parent = JsonSerializer.Deserialize<JsonElement>("""
            {"type":"object","properties":{"results":{"type":"array","items":{"type":"object","properties":{"name":{"type":"string"}}}}}}
            """);
        var version = new FlowDesignerSchemaVersion("response", "Response", ResourceNamespace.Default,
            TestScope, "A", "json-schema", null, parent);
        SchemaBindingEditorValue? value = null;
        var component = context.Render<SchemaBindingEditor>(parameters => parameters
            .Add(c => c.Title, "Contract").Add(c => c.TestId, "selection")
            .Add(c => c.SchemaVersions, new[] { version, version with { Version = "B" } })
            .Add(c => c.Binding, new SchemaBinding { Reference = new() { Name = version.Name, Version = "A", ScopeRef = TestScope } })
            .Add(c => c.ValueChanged, selected => value = selected));
        Assert.Contains(label, component.Markup);
        component.Find("[data-testid=selection-path]").Change("/properties/results/items");
        Assert.AreEqual("A", value!.Binding!.Reference!.Version);
        Assert.AreEqual("/properties/results/items", value.Binding.Reference.SchemaPath);
        Assert.AreEqual("object", value.EffectiveContent!.Value.GetProperty("type").GetString());
        component.Find("[data-testid=selection-path]").Change("/properties/missing");
        Assert.IsNull(value.EffectiveContent);
        Assert.AreEqual("/properties/missing", value.Binding!.Reference!.SchemaPath);
        Assert.HasCount(1, component.FindAll("[role=alert]"));
        component.Find("[data-testid=selection-path]").Change("/properties/results/items");
        component.Find("[data-testid=selection-version]").Change("B");
        Assert.AreEqual("B", value.Binding!.Reference!.Version);
        Assert.AreEqual("/properties/results/items", value.Binding.Reference.SchemaPath);
    }
}
