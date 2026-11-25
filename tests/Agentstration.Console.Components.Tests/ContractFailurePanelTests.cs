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
    [DataRow("en-US", "generation token limit")]
    [DataRow("fr-FR", "limite de tokens de génération")]
    public void ProjectionAndRunPanelsUseTheSameLocalizedModelFailure(string language, string message)
    {
        using var culture = new TestCultureScope(language);
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var rendered = context.Render<ContractFailurePanel>(parameters => parameters
            .Add(component => component.ErrorCode, "model_output_token_limit")
            .Add(component => component.Message, "The projection could not complete.")
            .Add(component => component.Compact, true));
        Assert.Contains(message, rendered.Find(".contract-failure-summary").TextContent);
        Assert.DoesNotContain("The projection could not complete.", rendered.Find(".contract-failure-summary").TextContent);
    }

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
        Assert.HasCount(1, rendered.FindAll(".contract-failure-summary"));
        Assert.HasCount(1, rendered.FindAll(".contract-failure-technical"));
        Assert.IsFalse(rendered.Find(".contract-failure-technical").HasAttribute("open"));
        Assert.DoesNotContain("Contract artifact-output failed", rendered.Markup);
        Assert.Contains("artifact-output", rendered.Markup);
        Assert.Contains("data_contract_json_invalid", rendered.Markup);
        Assert.AreEqual("/flow-runs/child-run?scopeRef=%2Finstance&step=anonymize", rendered.Find("a").GetAttribute("href"));
        rendered.Render(parameters => parameters.Add(component => component.Details, "{\"ordinary\":\"diagnostic\"}"));
        Assert.AreEqual(0, rendered.FindAll("[role='note']").Count);
    }
    [TestMethod]
    [DataRow("en-US", "permitted maximum", "UTF-8 bytes", "Server setting:", "Function input")]
    [DataRow("fr-FR", "maximum autorisé", "octets UTF-8", "Réglage du serveur", "Entrée de fonction")]
    public void OperatorLimitShowsTheMeasuredValueCeilingUnitSettingAndCorrection(string locale, string summary, string unit, string setting, string boundary)
    {
        using var culture = new TestCultureScope(locale);
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var failure = new DataContractFailure("function-input", "function-input", "$.value", "xml_utf8Bytes_limit", "bounded XML", "processing limit exceeded", "run", "convert", "transformation_input_invalid")
        { Limit = new("utf8Bytes", 120, Maximum: 100, Setting: "Agentstration:XmlContent:MaximumDocumentBytes") };
        var rendered = context.Render<ContractFailurePanel>(parameters => parameters
            .Add(value => value.Details, JsonSerializer.Serialize(new { contractFailure = failure })));
        Assert.Contains(locale == "fr-FR" ? "Limite" : "limit", rendered.Find(".contract-failure-title").TextContent);
        Assert.Contains(summary, rendered.Find(".contract-failure-summary").TextContent);
        Assert.Contains("120", rendered.Find(".contract-failure-summary").TextContent);
        Assert.Contains("100", rendered.Find(".contract-failure-summary").TextContent);
        Assert.Contains(unit, rendered.Find(".contract-failure-summary").TextContent);
        Assert.Contains(boundary, rendered.Find(".contract-boundary").TextContent);
        Assert.Contains(setting, rendered.Find(".contract-failure-advice").TextContent);
        Assert.Contains("Agentstration:XmlContent:MaximumDocumentBytes", rendered.Find(".contract-failure-advice code").TextContent);
        Assert.Contains("step=convert", rendered.Find("a").GetAttribute("href")!);
        rendered.Render(parameters => parameters.Add(value => value.Compact, true));
        Assert.Contains(summary, rendered.Find(".contract-failure-summary").TextContent);
        Assert.IsEmpty(rendered.FindAll(".contract-failure-advice"));
    }

    [TestMethod]
    [DataRow("en-US", "required minimum", "characters", "schema constraints")]
    [DataRow("fr-FR", "minimum requis", "caractères", "contraintes du schéma")]
    public void SchemaLengthLimitIsNotConfusedWithAnOperatorCeiling(string locale, string summary, string unit, string advice)
    {
        using var culture = new TestCultureScope(locale);
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var failure = new DataContractFailure("function-input", "function-input", "$.value", "json_schema_instance_length", "string", "string")
        { Limit = new("characters", 1, Minimum: 5) };
        var rendered = context.Render<ContractFailurePanel>(parameters => parameters
            .Add(value => value.Details, JsonSerializer.Serialize(failure)));
        Assert.Contains(summary, rendered.Find(".contract-failure-summary").TextContent);
        Assert.Contains(unit, rendered.Find(".contract-failure-summary").TextContent);
        Assert.Contains(advice, rendered.Find(".contract-failure-advice").TextContent);
        Assert.IsEmpty(rendered.FindAll(".contract-failure-advice code"));
        rendered.Render(parameters => parameters.Add(value => value.Details, JsonSerializer.Serialize(failure with { Limit = null })));
        Assert.Contains(locale == "fr-FR" ? "longueur du texte" : "text length", rendered.Find(".contract-failure-summary").TextContent);
    }

    [TestMethod]
    [DataRow("en-US", "well formed", "Line 3, column 7", "XML location", "order")]
    [DataRow("fr-FR", "bien formé", "Ligne 3, colonne 7", "Emplacement XML", "l’ordre")]
    public void XsdRejectionsExplainTheLocationExpectedStructureAndPinnedSchema(string locale, string summary, string position, string path, string advice)
    {
        using var culture = new TestCultureScope(locale);
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var failure = new DataContractFailure("function-input", "function-input", "$.value", "xml_contract_invalid", "XML matching the accepted XSD", "invalid XML content", "run", "convert", "transformation_input_invalid")
        {
            Xml = new("contract", "/order[1]/unexpected[1]", 3, 7, ExpectedStructure: "sequence(id, name)",
            Schema: new("order-xsd", "1.0.0", ResourceScopeRef.Instance, ResourceNamespace.Default, Guid.NewGuid(), 1, "digest", null, "digest"))
        };
        var rendered = context.Render<ContractFailurePanel>(parameters => parameters.Add(value => value.Details, JsonSerializer.Serialize(failure))
            .Add(value => value.Collapsible, true));
        Assert.AreEqual("true", rendered.Find("[role=note]").GetAttribute("data-interactive"));
        Assert.IsFalse(rendered.Find(".contract-failure-toggle").HasAttribute("disabled"));
        rendered.Find(".contract-failure-toggle").Click();
        Assert.Contains(summary, rendered.Find(".contract-failure-summary").TextContent);
        Assert.Contains(position, rendered.Find(".contract-xml-diagnostic").TextContent);
        Assert.Contains(path, rendered.Find(".contract-xml-diagnostic").TextContent);
        Assert.Contains("order-xsd", rendered.Find(".contract-xml-diagnostic").TextContent);
        Assert.Contains("1.0.0", rendered.Find(".contract-xml-diagnostic").TextContent);
        Assert.Contains("sequence(id, name)", rendered.Find(".contract-xml-diagnostic").TextContent);
        Assert.Contains(advice, rendered.Find(".contract-failure-advice").TextContent);
        Assert.Contains("step=convert", rendered.Find("a").GetAttribute("href")!);
        rendered.Render(parameters => parameters.Add(value => value.Details, null).Add(value => value.ErrorCode, "transformation_input_invalid"));
        Assert.Contains(locale == "fr-FR" ? "ne contient pas de diagnostic" : "contains no detailed", rendered.Find(".contract-failure-summary").TextContent);
        Assert.DoesNotContain("Runtime Worker", rendered.Markup);
    }

}
