using Agentstration.Management.Abstractions;
using Agentstration.Web.Components.Pages;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class SourceConsoleLocalePresentationTests
{
    [TestMethod]
    public void AvailableLocalesUsePublishedVariantsInsteadOfAFixedLanguageList()
    {
        var catalogs = new SourceCatalogView[]
        {
            new(
                Provenance(),
                "Bootstrap catalog",
                null,
                [new("starter", "en-US", [new("en-US", "profiles/starter/en-US"), new("de-DE", "profiles/starter/de-DE")], "en-US", "profiles/starter/en-US")],
                [])
        };

        var locales = SourceConsoleLocalePresentation.AvailableLocales(catalogs, "fr-FR");

        CollectionAssert.AreEqual(new[] { "fr-FR", "de-DE", "en-US" }, locales.ToArray());
        Assert.IsFalse(SourceConsoleLocalePresentation.IsAvailable(catalogs, "fr-FR"));
        Assert.IsTrue(SourceConsoleLocalePresentation.IsAvailable(catalogs, "de-DE"));
    }

    [TestMethod]
    public void AvailableLocalesKeepTheRequestedExactCultureWhenPublished()
    {
        var catalogs = new SourceCatalogView[]
        {
            new(
                Provenance(),
                "Bootstrap catalog",
                null,
                [new("starter", "en-US", [new("en-US", "profiles/starter/en-US"), new("fr-FR", "profiles/starter/fr-FR")], "fr-FR", "profiles/starter/fr-FR")],
                [])
        };

        var locales = SourceConsoleLocalePresentation.AvailableLocales(catalogs, "fr-FR");

        CollectionAssert.AreEqual(new[] { "en-US", "fr-FR" }, locales.ToArray());
        Assert.IsTrue(SourceConsoleLocalePresentation.IsAvailable(catalogs, "fr-FR"));
    }

    private static SourceCatalogProvenance Provenance() => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        "1",
        "stable",
        Guid.NewGuid(),
        "sha256:snapshot",
        "BootstrapCatalog",
        "starter",
        "catalogs/bootstrap.yaml");
}
