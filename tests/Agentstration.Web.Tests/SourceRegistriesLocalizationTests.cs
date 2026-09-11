using System.Globalization;
using System.Xml.Linq;
using Agentstration.Web.Components.Pages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Agentstration.Web.Tests;

[TestClass]
[DoNotParallelize]
public sealed class SourceRegistriesLocalizationTests
{
    [TestMethod]
    public void FrenchCatalogExplainsIndependentRegistryEvidence()
    {
        using var culture = new TestCultureScope("fr-FR");
        var services = new ServiceCollection().AddLogging().AddLocalization(options => options.ResourcesPath = "Resources").BuildServiceProvider();
        var strings = services.GetRequiredService<IStringLocalizer<SourceRegistriesStrings>>();

        Assert.AreEqual("Registres de Sources", strings["Title"].Value);
        Assert.AreEqual("Confiance dans l’origine Registry", strings["RegistryOriginTrust"].Value);
        Assert.AreEqual("Preuve éditeur", strings["PublisherEvidence"].Value);
        Assert.AreEqual("Vérification de SourceVersion", strings["SourceVersionVerification"].Value);
        Assert.AreEqual("Vérification du Snapshot", strings["SnapshotVerification"].Value);
        Assert.AreEqual("La valeur du Secret reste en écriture seule.", strings["CredentialValueHidden"].Value);
    }

    [TestMethod]
    public void NeutralAndFrenchCatalogsHaveSymmetricKeys()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Agentstration.Web", "Resources", "Components", "Pages");
        var neutral = Keys(Path.Combine(root, "SourceRegistriesStrings.resx"));
        var french = Keys(Path.Combine(root, "SourceRegistriesStrings.fr-FR.resx"));
        CollectionAssert.AreEquivalent(neutral, french);
    }

    private static string[] Keys(string path)
    {
        return XDocument.Load(path).Root!.Elements("data").Select(value => value.Attribute("name")!.Value).Order(StringComparer.Ordinal).ToArray();
    }
}
