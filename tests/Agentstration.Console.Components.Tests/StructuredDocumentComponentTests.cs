using Agentstration.Storage.Contracts;
using Agentstration.Web.Console;
using Agentstration.Web.Components.Pages;
using Bunit;
using Microsoft.AspNetCore.Components;
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

    [TestMethod]
    [DataRow("en-US", "Enter a display name")]
    [DataRow("fr-FR", "Saisissez un nom d’affichage")]
    public async Task CollectionIdentityReusesAutomaticNamingAndBlocksMissingOrInvalidNames(string culture, string error)
    {
        using var currentCulture = new TestCultureScope(culture);
        using var context = new BunitContext();
        context.Services.AddLocalization(o => o.ResourcesPath = "Resources");
        IReadOnlyList<StorageDocumentCollection> values = [];
        var valid = true;
        var page = context.Render<StorageCollectionsEditor>(p => p.Add(c => c.Value, values)
            .Add(c => c.Capabilities, new StorageDocumentCapabilities(true, true, true, false, false, []))
            .Add(c => c.ValueChanged, updated => values = updated)
            .Add(c => c.ValidationChanged, value => valid = value));
        page.Find("[data-testid=storage-collection-add]").Click();
        Assert.IsFalse(valid);
        Assert.Contains(error, page.Markup);
        Assert.AreEqual(string.Empty, values[0].Name);
        await page.Find("[data-testid=collection-0-display-name]").InputAsync(new ChangeEventArgs { Value = "Alertes à Nantes" });
        Assert.AreEqual("Alertes à Nantes", values[0].DisplayName);
        Assert.AreEqual("alertes-a-nantes", values[0].Name);
        Assert.IsTrue(valid);
        await page.Find("[data-testid=collection-0-display-name]").InputAsync(new ChangeEventArgs { Value = "Alertes actives" });
        Assert.AreEqual("alertes-actives", values[0].Name);
        await page.Find("[data-testid=collection-0-name]").InputAsync(new ChangeEventArgs { Value = "INVALID_NAME" });
        Assert.IsFalse(valid);
        await page.Find("[data-testid=collection-0-name]").InputAsync(new ChangeEventArgs { Value = "alerts" });
        await page.Find("[data-testid=collection-0-display-name]").InputAsync(new ChangeEventArgs { Value = "Renommées" });
        Assert.AreEqual("alerts", values[0].Name, "A manual override must survive display name changes.");
        Assert.IsTrue(valid);
        await page.Find("[data-testid=collection-0-display-name]").InputAsync(new ChangeEventArgs { Value = " " });
        Assert.IsFalse(valid);
        Assert.AreEqual(Guid.Empty, values[0].Uid);
    }

    [TestMethod]
    public async Task PersistedCollectionKeepsItsTechnicalNameAndServerIdentity()
    {
        using var context = new BunitContext();
        context.Services.AddLocalization(o => o.ResourcesPath = "Resources");
        var uid = Guid.NewGuid();
        IReadOnlyList<StorageDocumentCollection> values = [new() { Uid = uid, Name = "alerts", DisplayName = "Alerts" }];
        var page = context.Render<StorageCollectionsEditor>(p => p.Add(c => c.Value, values)
            .Add(c => c.Capabilities, new StorageDocumentCapabilities(true, true, true, false, false, []))
            .Add(c => c.ValueChanged, updated => values = updated));
        Assert.IsTrue(page.Find("[data-testid=collection-0-name]").HasAttribute("readonly"));
        Assert.IsTrue(page.Find("[data-testid=collection-0-name]").HasAttribute("disabled"));
        await page.Find("[data-testid=collection-0-display-name]").InputAsync(new ChangeEventArgs { Value = "Active alerts" });
        Assert.AreEqual("alerts", values[0].Name);
        Assert.AreEqual(uid, values[0].Uid);
    }

    [TestMethod]
    public async Task RemovingAnotherDraftPreservesTheRemainingCollectionsAutomaticNaming()
    {
        using var context = new BunitContext();
        context.Services.AddLocalization(o => o.ResourcesPath = "Resources");
        IReadOnlyList<StorageDocumentCollection> values = [];
        var page = context.Render<StorageCollectionsEditor>(p => p.Add(c => c.Value, values)
            .Add(c => c.Capabilities, new StorageDocumentCapabilities(true, true, true, false, false, []))
            .Add(c => c.ValueChanged, updated => values = updated));
        page.Find("[data-testid=storage-collection-add]").Click();
        page.Find("[data-testid=storage-collection-add]").Click();
        await page.Find("[data-testid=collection-1-display-name]").InputAsync(new ChangeEventArgs { Value = "Second draft" });
        page.FindAll(".button-danger")[0].Click();
        await page.Find("[data-testid=collection-0-display-name]").InputAsync(new ChangeEventArgs { Value = "Remaining draft" });
        Assert.HasCount(1, values);
        Assert.AreEqual("remaining-draft", values[0].Name);
        Assert.AreEqual(Guid.Empty, values[0].Uid);
    }

    [TestMethod]
    [DataRow("en-US", "Enter a display name for the Storage and each collection", "storage_metadata_invalid")]
    [DataRow("en-US", "The storage server refused the connection", "storage_documents_backend_connection_refused")]
    [DataRow("en-US", "The storage connection timed out", "storage_documents_backend_timeout")]
    [DataRow("en-US", "The storage backend is unavailable", "storage_documents_backend_unavailable")]
    [DataRow("fr-FR", "Renseignez le nom d’affichage du stockage et de chaque collection", "storage_metadata_invalid")]
    [DataRow("fr-FR", "Le serveur de stockage a refusé la connexion", "storage_documents_backend_connection_refused")]
    [DataRow("fr-FR", "La connexion au stockage a expiré", "storage_documents_backend_timeout")]
    [DataRow("fr-FR", "Le stockage est indisponible", "storage_documents_backend_unavailable")]
    public void StorageFailureHasAnActionableLocalizedError(string culture, string message, string code)
    {
        using var currentCulture = new TestCultureScope(culture);
        using var context = new BunitContext();
        context.Services.AddLocalization(o => o.ResourcesPath = "Resources");
        var error = new AgentstrationApiException("PRIVATE", "fixture-error", System.Net.HttpStatusCode.UnprocessableEntity,
            code);
        var page = context.Render<StorageError>(p => p.Add(c => c.Error, error));
        Assert.Contains(message, page.Markup);
        Assert.DoesNotContain("PRIVATE", page.Markup);
        Assert.DoesNotContain("Error.RefreshHelp", page.Markup);
    }

    [TestMethod]
    [DataRow("en-US", "A collection schema is invalid", "Only bounded, nonrecursive local references")]
    [DataRow("fr-FR", "Le schéma d’une collection est invalide", "Seules les références locales bornées et non récursives")]
    public void CollectionSchemaFailureIdentifiesTheFieldAndActionWithoutRefreshAdvice(string culture, string title, string action)
    {
        using var currentCulture = new TestCultureScope(culture);
        using var context = new BunitContext();
        context.Services.AddLocalization(o => o.ResourcesPath = "Resources");
        var error = new AgentstrationApiException("hidden diagnostic", "fixture-error", System.Net.HttpStatusCode.UnprocessableEntity,
            "storage_collection_schema_invalid")
        { ValidationIssues = [new("/definition/collections/0/schema/$defs/item/$ref", "unsupported_schema", "$ref")] };
        var page = context.Render<StorageError>(p => p.Add(c => c.Error, error));
        Assert.Contains(title, page.Markup);
        Assert.Contains(action, page.Markup);
        Assert.Contains("/definition/collections/0/schema/$defs/item/$ref", page.Markup);
        Assert.DoesNotContain("hidden diagnostic", page.Markup);
        Assert.DoesNotContain("Error.RefreshHelp", page.Markup);
    }

}
