using System.Globalization;
using Agentstration.Web.Components.Pages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Agentstration.Web.Tests;

[TestClass]
[DoNotParallelize]
public sealed class RuntimeOperationsLocalizationTests
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
            using var services = new ServiceCollection()
                .AddLogging()
                .AddLocalization(options => options.ResourcesPath = "Resources")
                .BuildServiceProvider();

            Assert.AreEqual("Conteneur dédié", Localizer<DeploymentStrings>(services)["Hosting.DedicatedContainer"].Value);
            Assert.AreEqual("Délai dépassé", Localizer<AgentRunsStrings>(services)["RunState.TimedOut"].Value);
            Assert.AreEqual("Conserver pour cette exécution", Localizer<AgentRunnerStrings>(services)["ToolArguments.Retain"].Value);
            Assert.AreEqual("Température", Localizer<AgentRunnerInspectorStrings>(services)["Temperature"].Value);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    private static IStringLocalizer<T> Localizer<T>(IServiceProvider services) =>
        services.GetRequiredService<IStringLocalizer<T>>();
}
