using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Schemas;
using Agentstration.Web.Components;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class ContractFailurePanelTests
{
    [TestMethod]
    [DataRow("en-US", "valid JSON")]
    [DataRow("fr-FR", "JSON valide")]
    public void ContractFailureShowsTheCausalBoundaryAndLinksTheExactStep(string language, string explanation)
    {
        using var culture = new TestCultureScope(language);
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var failure = new DataContractFailure("artifact-output", "agent:fixture/output", "$", "json_syntax", "valid JSON", "invalid JSON text", "child-run", "anonymize", "data_contract_json_invalid");
        var rendered = context.Render<ContractFailurePanel>(parameters => parameters
            .Add(component => component.Details, JsonSerializer.Serialize(new { contractFailure = failure, details = "custom boundary" }))
            .Add(component => component.ScopeRef, ResourceScopeRef.Instance));
        Assert.Contains(explanation, rendered.Markup);
        Assert.Contains("artifact-output", rendered.Markup);
        Assert.Contains("data_contract_json_invalid", rendered.Markup);
        Assert.AreEqual("/flow-runs/child-run?scopeRef=%2Finstance&step=anonymize", rendered.Find("a").GetAttribute("href"));
        rendered.Render(parameters => parameters.Add(component => component.Details, "{\"ordinary\":\"diagnostic\"}"));
        Assert.AreEqual(0, rendered.FindAll("[role='note']").Count);
    }
}
