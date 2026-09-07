using System.Globalization;
using Agentstration.Web.Components.Pages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Agentstration.Web.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ResourceScopesLocalizationTests
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
            var services = new ServiceCollection()
                .AddLogging()
                .AddLocalization(options => options.ResourcesPath = "Resources")
                .BuildServiceProvider();
            var strings = services.GetRequiredService<IStringLocalizer<ResourceScopesStrings>>();

            Assert.AreEqual("Périmètres des ressources", strings["Title"].Value);
            Assert.AreEqual("Périmètre sélectionné", strings["SelectedScopeResources"].Value);
            Assert.AreEqual("Ressources : 1", strings["ResourceCount", 1].Value);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }
}
