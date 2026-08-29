using Agentstration.Web.Components.Pages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using System.Globalization;

namespace Agentstration.Web.Tests;

[TestClass]
[DoNotParallelize]
public sealed class LocalizationResidualTests
{
    [TestMethod]
    public void DynamicLabelsUseTheSelectedFrenchCulture()
    {
        var originalCulture = CultureInfo.CurrentCulture; var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR"); CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            using var services = new ServiceCollection().AddLogging().AddLocalization(options => options.ResourcesPath = "Resources").BuildServiceProvider();
            Assert.AreEqual("Fournisseur indisponible", L<AgentEditorStrings>(services)["Status.ProviderUnavailable"].Value);
            Assert.AreEqual("Actif", L<FlowsStrings>(services)["Status.Active"].Value);
            Assert.AreEqual("Intervention requise", L<HomeStrings>(services)["Status.Attention required"].Value);
            Assert.AreEqual("Administrateur de la plateforme", L<OrganizationStrings>(services)["PlatformAdministrator"].Value);
            Assert.AreEqual("UID", L<TriggerStrings>(services)["Uid"].Value);
        }
        finally { CultureInfo.CurrentCulture = originalCulture; CultureInfo.CurrentUICulture = originalUiCulture; }
    }

    private static IStringLocalizer<T> L<T>(IServiceProvider services) => services.GetRequiredService<IStringLocalizer<T>>();
}
