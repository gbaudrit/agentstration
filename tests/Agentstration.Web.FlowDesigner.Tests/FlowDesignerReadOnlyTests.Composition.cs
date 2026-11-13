using Agentstration.Flows;
using Agentstration.Web.FlowDesigner.Components;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.FlowDesigner.Tests;

public sealed partial class FlowDesignerReadOnlyTests
{
    [TestMethod]
    [DataRow("en-US", "Point name")]
    [DataRow("fr-FR", "Nom du point")]
    public void CompositionInspectorEditsThePointAndIdentityPolicyInBothLanguages(string language, string label)
    {
        using var culture = new CultureScope(language);
        using var context = CreateContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        FlowCompositionPointStepDefinition? changed = null;
        var step = new FlowCompositionPointStepDefinition { Name = "extension", Point = "prepare" };
        var rendered = context.Render<FlowCompositionInspector>(parameters => parameters
            .Add(component => component.Step, step)
            .Add(component => component.ActiveTab, FlowStepSettingsTab.Configuration)
            .Add(component => component.Changed, value => changed = (FlowCompositionPointStepDefinition)value));
        StringAssert.Contains(rendered.Markup, label);
        rendered.Find("[data-testid='composition-point-name']").Change("after-anonymization");
        Assert.AreEqual("after-anonymization", changed?.Point);
        rendered.Find("input[type='checkbox']").Change(false);
        Assert.IsFalse(changed!.AllowEmpty);
        rendered.Render(parameters => parameters.Add(component => component.ReadOnly, true));
        Assert.IsTrue(rendered.Find("[data-testid='composition-point-name']").HasAttribute("disabled"));
    }
}
