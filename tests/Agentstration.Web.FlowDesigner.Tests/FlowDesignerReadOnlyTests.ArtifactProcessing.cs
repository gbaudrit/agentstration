using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Schemas;
using Agentstration.Web.FlowDesigner.Components;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.FlowDesigner.Tests;

public sealed partial class FlowDesignerReadOnlyTests
{
    [TestMethod]
    public async Task PortInspectionRefreshesItsParentWithoutMutatingAPublishedDefinition()
    {
        using var context = CreateContext();
        var backend = new BackendStub();
        context.Services.AddSingleton<Agentstration.Web.FlowDesigner.Backend.IFlowDesignerBackend>(backend);
        context.Services.AddSingleton<Agentstration.Web.FlowDesigner.Backend.IFlowDesignerResourceProvider>(new ResourceProviderStub());
        context.Services.AddSingleton<Agentstration.Web.FlowDesigner.State.FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true)
            .SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1024, 768));
        var rendered = context.Render<Agentstration.Web.FlowDesigner.Components.FlowDesigner>(parameters => parameters
            .Add(component => component.ResourceId, "sample"));
        var canvas = rendered.FindComponent<FlowCanvas>().Instance;
        await rendered.InvokeAsync(() => canvas.InspectPortAsync!("input", "input"));
        rendered.WaitForAssertion(() => Assert.IsNotNull(rendered.Find("#port-contract-title")));
        Assert.AreEqual(0, backend.SaveCount);
    }

    [TestMethod]
    [DataRow("en-US", "Explicit dynamic contract")]
    [DataRow("fr-FR", "Contrat dynamique explicite")]
    public void ArtifactOutputContractModeIsExplicitAndReadOnlyDefinitionCannotChangeIt(string language, string label)
    {
        using var culture = new CultureScope(language);
        using var context = CreateContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        ArtifactTransformFlowStepDefinition? changed = null;
        var step = new ArtifactTransformFlowStepDefinition { Name = "anonymize" };
        var rendered = context.Render<FlowArtifactTransformInspector>(parameters => parameters
            .Add(component => component.Step, step)
            .Add(component => component.ActiveTab, FlowStepSettingsTab.Output)
            .Add(component => component.Changed, value => changed = (ArtifactTransformFlowStepDefinition)value));
        Assert.Contains(label, rendered.Markup);
        rendered.Find("select").Change("Dynamic");
        Assert.AreEqual(DataContractMode.Dynamic, changed?.OutputContract?.Mode);
        Assert.IsNull(changed?.OutputContract?.Schema);
        rendered.Render(parameters => parameters.Add(component => component.Step, changed!).Add(component => component.ReadOnly, true));
        Assert.IsTrue(rendered.FindAll("select").All(select => select.HasAttribute("disabled")));
    }

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
    [TestMethod]
    [DataRow("en-US", "enter your own")]
    [DataRow("fr-FR", "valeur libre")]
    public void ArtifactMediaTypeOffersSuggestionsAndPreservesCustomValues(string language, string help)
    {
        using var culture = new CultureScope(language);
        using var context = CreateContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        ArtifactTransformFlowStepDefinition? changed = null;
        var rendered = context.Render<FlowArtifactTransformInspector>(parameters => parameters
            .Add(component => component.Step, new ArtifactTransformFlowStepDefinition { Name = "transform" })
            .Add(component => component.ActiveTab, FlowStepSettingsTab.Configuration)
            .Add(component => component.Changed, value => changed = (ArtifactTransformFlowStepDefinition)value));
        var input = rendered.Find("[data-testid='artifact-output-media-type']");
        var list = rendered.Find("datalist");
        Assert.AreEqual(list.Id, input.GetAttribute("list"));
        Assert.IsTrue(list.QuerySelectorAll("option").Any(option => option.GetAttribute("value") == "text/plain"));
        Assert.IsTrue(list.QuerySelectorAll("option").Any(option => option.GetAttribute("value") == "application/json"));
        Assert.Contains(help, rendered.Markup);
        input.Change("application/vnd.example.redacted+json");
        Assert.AreEqual("application/vnd.example.redacted+json", changed?.OutputMediaType);
        input.Change("");
        Assert.IsNull(changed?.OutputMediaType);
        rendered.Render(parameters => parameters.Add(component => component.ReadOnly, true));
        Assert.IsTrue(rendered.Find("[data-testid='artifact-output-media-type']").HasAttribute("disabled"));
    }
}
