using System.Globalization;
using Agentstration.Web.Components.Pages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Agentstration.Web.Tests;

[TestClass]
[DoNotParallelize]
public sealed class SourcesLocalizationTests
{
    [TestMethod]
    public void CatalogKeepsConsoleAndContentLanguageConceptsDistinctInFrench()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            var services = new ServiceCollection()
                .AddLogging()
                .AddLocalization(options => options.ResourcesPath = "Resources")
                .BuildServiceProvider();
            var strings = services.GetRequiredService<IStringLocalizer<SourcesStrings>>();

            Assert.AreEqual("Sources", strings["Title"].Value);
            Assert.AreEqual("Langue du contenu", strings["ContentLanguage"].Value);
            Assert.AreEqual("Périmètre de propriété", strings["OwnershipScope"].Value);
            Assert.AreEqual("Sélectionnée : fr-FR", strings["SelectedLocale", "fr-FR"].Value);
            Assert.AreEqual("repli depuis fr-FR", strings["FallbackFrom", "fr-FR"].Value);
            Assert.AreEqual("Aucun fournisseur de Sources n’est configuré pour cette instance.", strings["NoConfiguredProviders"].Value);
            Assert.AreEqual("Configurer un fournisseur de Sources", strings["ConfigureProvider"].Value);
            Assert.AreEqual("Détails de la Source", strings["SourceDetails"].Value);
            Assert.AreEqual("Vue d’ensemble", strings["Tab.Overview"].Value);
            Assert.AreEqual("Versions", strings["Tab.Versions"].Value);
            Assert.AreEqual("Bindings", strings["Tab.Bindings"].Value);
            Assert.AreEqual("Contenu", strings["Tab.Content"].Value);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }
}
