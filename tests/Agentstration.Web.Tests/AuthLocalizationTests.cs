using Agentstration.Web.Pages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using System.Globalization;

namespace Agentstration.Web.Tests;

[TestClass]
[DoNotParallelize]
public sealed class AuthLocalizationTests
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
            using var services = new ServiceCollection().AddLogging().AddLocalization(options => options.ResourcesPath = "Resources").BuildServiceProvider();
            var strings = services.GetRequiredService<IStringLocalizer<AuthStrings>>();

            Assert.AreEqual("Se connecter", strings["SignIn"].Value);
            Assert.AreEqual("Accès refusé", strings["AccessDenied"].Value);
            Assert.AreEqual("Sécurité du compte", strings["AccountSecurity"].Value);
            Assert.AreEqual("Révoqué", strings["TokenStatus.Revoked"].Value);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }
}
