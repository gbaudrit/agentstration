using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Web.FlowDesigner.Components;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.FlowDesigner.Tests;

public sealed partial class FlowDesignerReadOnlyTests
{
    [TestMethod]
    [DataRow("en-US", "Provide input as")]
    [DataRow("fr-FR", "Transmettre l’entrée sous forme de")]
    public void ArtifactProcessingInspectorKeepsInvocationAdaptationOnTheStep(string language, string label)
    {
        using var culture = new CultureScope(language);
        using var context = CreateContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        ArtifactTransformFlowStepDefinition? changed = null;
        var step = new ArtifactTransformFlowStepDefinition { Name = "anonymize" };
        var rendered = context.Render<FlowArtifactTransformInspector>(parameters => parameters
            .Add(component => component.Step, step)
            .Add(component => component.ActiveTab, FlowStepSettingsTab.Configuration)
            .Add(component => component.Changed, value => changed = (ArtifactTransformFlowStepDefinition)value));
        StringAssert.Contains(rendered.Markup, label);
        rendered.Find("[data-testid='artifact-processing-mode']").Change("Access");
        Assert.AreEqual(ArtifactProcessingMode.Access, changed?.Mode);
        rendered.Render(parameters => parameters.Add(component => component.ActiveTab, FlowStepSettingsTab.Input));
        rendered.Find("[data-testid='artifact-input-mapping']").Change("{\"text\":\"${input.content}\",\"language\":\"fr\"}");
        Assert.AreEqual("${input.content}", changed?.ProcessingInputMapping?.GetProperty("text").GetString());
        rendered.Find("[data-testid='artifact-input-mapping']").Change("{");
        Assert.IsNotNull(rendered.Find("[role='alert']"));
        rendered.Render(parameters => parameters.Add(component => component.ReadOnly, true));
        Assert.IsTrue(rendered.Find("[data-testid='artifact-input-mapping']").HasAttribute("disabled"));
    }
}
