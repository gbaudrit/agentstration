using Agentstration.Storage.Contracts;
using Agentstration.Web.Components.Pages;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
[DoNotParallelize]
public sealed class StructuredDocumentComponentTests
{
    [TestMethod]
    [DataRow("en-US", "Structured collections", "Enter a valid JSON object.")]
    [DataRow("fr-FR", "Collections structurées", "Saisissez un objet JSON valide.")]
    public void CollectionEditorRetainsInvalidSchemaDraftAndNeverInventsCollectionIdentity(string culture, string title, string error)
    {
        using var currentCulture = new TestCultureScope(culture);
        using var context = new BunitContext();
        context.Services.AddLocalization(o => o.ResourcesPath = "Resources");
        IReadOnlyList<StorageDocumentCollection> values = [new() { Uid = Guid.NewGuid(), Name = "original", DisplayName = "Original" }];
        var uid = values[0].Uid;
        var valid = true;
        var page = context.Render<StorageCollectionsEditor>(p => p.Add(c => c.Value, values)
            .Add(c => c.Capabilities, new StorageDocumentCapabilities(true, true, true, true, true, [new("fixture.other", "2", "text+json")]))
            .Add(c => c.ValueChanged, updated => values = updated)
            .Add(c => c.ValidationChanged, value => valid = value));
        Assert.Contains(title, page.Markup);
        Assert.IsFalse(page.Find("fieldset").HasAttribute("disabled"));
        Assert.Contains("fixture.other/2", page.Markup);
        page.Find("textarea").Change("{");
        Assert.IsFalse(valid);
        Assert.Contains(error, page.Markup);
        Assert.AreEqual(uid, values[0].Uid);
        Assert.IsTrue(values[0].Schema.TryGetProperty("type", out _));
        page.Find("textarea").Change("""{"type":"object","properties":{"status":{"type":"string"}}}""");
        Assert.IsTrue(valid);
        Assert.IsTrue(values[0].Schema.TryGetProperty("properties", out _));
        page.Find("[data-testid=storage-collection-add]").Click();
        Assert.AreEqual(Guid.Empty, values[^1].Uid, "Only the server allocates a new collection identity.");
    }

}
