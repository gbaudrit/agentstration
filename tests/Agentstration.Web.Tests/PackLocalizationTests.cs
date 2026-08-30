using System.Globalization;
using Agentstration.Web.Components.Pages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Agentstration.Web.Tests;

[TestClass]
[DoNotParallelize]
public sealed class PackLocalizationTests
{
    [TestMethod]
    public void CatalogUsesTheSelectedFrenchCulture()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            var services = new ServiceCollection().AddLogging().AddLocalization(options => options.ResourcesPath = "Resources").BuildServiceProvider();
            var strings = services.GetRequiredService<IStringLocalizer<PackStrings>>();

            Assert.AreEqual("Créer un projet de Pack", strings["CreatePackProject"].Value);
            Assert.AreEqual("Fournisseur de modèles", strings["Binding.ModelProvider"].Value);
            Assert.AreEqual("Retirer agent du Pack", strings["RemoveFromPackLabel", "agent"].Value);
            Assert.AreEqual("Le build 1.0.0 est prêt pour cet espace de travail.", strings["BuildReadyForWorkspace", "1.0.0"].Value);
            Assert.AreEqual("Désinstaller le Pack ?", strings["UninstallPackQuestion"].Value);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }
}
