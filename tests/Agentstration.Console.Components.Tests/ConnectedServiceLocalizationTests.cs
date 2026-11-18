using Agentstration.Web.Components.Pages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Agentstration.Web.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ConnectedServiceLocalizationTests
{
    [TestMethod]
    public void CatalogUsesSelectedFrenchCulture()
    {
        using var culture = new TestCultureScope("fr-FR");
        var services = new ServiceCollection().AddLogging().AddLocalization(options => options.ResourcesPath = "Resources").BuildServiceProvider();
        var strings = services.GetRequiredService<IStringLocalizer<ConnectedServiceStrings>>();

        Assert.AreEqual("Services connectés", strings["ConnectedServices"].Value);
        Assert.AreEqual("Liaison manuelle", strings["ManualBinding"].Value);
        Assert.AreEqual("Réconcilier", strings["Reconcile"].Value);
    }
}
