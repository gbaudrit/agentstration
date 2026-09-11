using System.Globalization;
using Agentstration.Web.Components.Pages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Agentstration.Web.Tests;

[TestClass]
[DoNotParallelize]
public sealed class SecretLocalizationTests
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
            var strings = services.GetRequiredService<IStringLocalizer<SecretStrings>>();

            Assert.AreEqual("Créer un coffre", strings["CreateVault"].Value);
            Assert.AreEqual("Coffre indisponible", strings["Status.VaultUnavailable"].Value);
            Assert.AreEqual("Supprimer le secret « api-key » et sa valeur stockée ?", strings["DeleteSecretMessage", "api-key"].Value);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }
}
