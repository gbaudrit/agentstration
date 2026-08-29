using Agentstration.Web.Components.Pages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using System.Globalization;

namespace Agentstration.Web.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ManagementLocalizationTests
{
    [TestMethod]
    public void CatalogUsesTheSelectedFrenchCulture()
    {
        var originalCulture = CultureInfo.CurrentCulture; var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR"); CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            using var services = new ServiceCollection().AddLogging().AddLocalization(options => options.ResourcesPath = "Resources").BuildServiceProvider();
            var strings = services.GetRequiredService<IStringLocalizer<ManagementStrings>>();
            Assert.AreEqual("Plan de gestion", strings["Title"].Value);
            Assert.AreEqual("Ressources de gouvernance", strings["GovernanceResources"].Value);
            Assert.AreEqual("État souhaité", strings["DesiredState"].Value);
        }
        finally { CultureInfo.CurrentCulture = originalCulture; CultureInfo.CurrentUICulture = originalUiCulture; }
    }
}
