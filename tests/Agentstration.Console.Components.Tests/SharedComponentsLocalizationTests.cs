using System.Globalization;
using Agentstration.Web.Components;
using Agentstration.Web.Components.ModelProfiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Agentstration.Web.Tests;

[TestClass]
[DoNotParallelize]
public sealed class SharedComponentsLocalizationTests
{
    [TestMethod]
    public void CatalogsUseTheSelectedFrenchCulture()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            using var services = new ServiceCollection().AddLogging().AddLocalization(options => options.ResourcesPath = "Resources").BuildServiceProvider();

            var shared = services.GetRequiredService<IStringLocalizer<SharedUiStrings>>();
            var profiles = services.GetRequiredService<IStringLocalizer<ModelProfilePickerStrings>>();
            Assert.AreEqual("Page introuvable", shared["PageNotFound"].Value);
            Assert.AreEqual("Derniers événements d’exécution", shared["LatestRunEvents"].Value);
            Assert.AreEqual("Profil de modèle sélectionné", profiles["SelectedModelProfile"].Value);
            Assert.AreEqual("Fournisseur indisponible", profiles["Status.providerUnavailable"].Value);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }
}
