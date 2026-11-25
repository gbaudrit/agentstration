using System.Text.Json;
using Agentstration.Web.Components.Pages;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class SourceValueInspectorTests
{
    [TestMethod]
    [DataRow("en-US", "Property", "Show more")]
    [DataRow("fr-FR", "Propriété", "Afficher la suite")]
    public void NestedGridSupportsExpansionAndBoundedProgressiveLoading(string locale, string property, string more)
    {
        using var culture = new TestCultureScope(locale);
        using var context = new BunitContext();
        context.Services.AddLocalization(o => o.ResourcesPath = "Resources");
        var json = JsonSerializer.SerializeToElement(Enumerable.Range(0, 150).Select(i => new { id = i, metadata = new { active = true, nullable = (string?)null, tags = new[] { "A", "B" } } }));
        var rendered = context.Render<SourceValueInspector>(p => p.Add(c => c.Value, json));
        Assert.Contains(property, rendered.Find("thead").TextContent);
        Assert.HasCount(1, rendered.FindAll("tbody tr"));
        rendered.Find("tbody button").Click();
        Assert.HasCount(22, rendered.FindAll("tbody tr"));
        Assert.Contains(more, rendered.Markup);
        rendered.FindAll("tbody button")[1].Click();
        Assert.Contains("metadata", rendered.Markup);
        rendered.FindAll("tbody button").First(b => b.TextContent.Contains("metadata", StringComparison.Ordinal)).Click();
        Assert.Contains("nullable", rendered.Markup);
        Assert.Contains("null", rendered.Markup);
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("42")]
    [DataRow("true")]
    [DataRow("\"text\"")]
    [DataRow("[]")]
    [DataRow("{}")]
    public void ScalarAndEmptyRootsRemainVisible(string json)
    {
        using var context = new BunitContext();
        context.Services.AddLocalization(o => o.ResourcesPath = "Resources");
        var rendered = context.Render<SourceValueInspector>(p => p.Add(c => c.Value, JsonSerializer.Deserialize<JsonElement>(json)));
        Assert.HasCount(1, rendered.FindAll("tbody tr"));
        Assert.HasCount(3, rendered.FindAll("tbody td"));
        Assert.Contains("$", rendered.Find("tbody").TextContent);
        rendered.FindAll(".form-actions button")[1].Click();
        using var source = JsonDocument.Parse(rendered.Find("[data-testid=preview-json-source]").TextContent);
        Assert.IsTrue(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(json), source.RootElement));
    }

    [TestMethod]
    [DataRow("application/xml")]
    [DataRow("application/rss+xml")]
    [DataRow("application/atom+xml")]
    public void NativeXmlSourceDoesNotDisplayJsonStringEscapes(string mediaType)
    {
        using var context = new BunitContext();
        context.Services.AddLocalization(o => o.ResourcesPath = "Resources");
        var rendered = context.Render<SourceValueInspector>(p => p.Add(c => c.Value, JsonSerializer.SerializeToElement("<root/>"))
            .Add(c => c.MediaType, mediaType));
        Assert.AreEqual("<root/>", rendered.Find("[data-testid=preview-json-source]").TextContent);
    }
}
