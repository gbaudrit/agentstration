using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using System.Globalization;

namespace Agentstration.Web.Tests;

[TestClass]
[DoNotParallelize]
public sealed class OrganizationLocalizationTests
{
    [TestMethod]
    public void CatalogAndNavigationUseTheSelectedFrenchCulture()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            using var context = new BunitContext();
            context.Services.AddLocalization(options => options.ResourcesPath = "Resources");

            var strings = context.Services.GetRequiredService<IStringLocalizer<OrganizationStrings>>();
            var rendered = context.Render<OrganizationSettingsNav>();

            Assert.AreEqual("Créer un espace de travail", strings["CreateWorkspace"].Value);
            Assert.AreEqual("Désactivé", strings["Status.Disabled"].Value);
            Assert.AreEqual("Compte local créé", strings["AuditAction.local-account.created"].Value);
            Assert.AreEqual("Compte local alice créé.", strings["AccountCreated", "alice"].Value);
            StringAssert.Contains(rendered.Markup, "Paramètres de l’organisation");
            StringAssert.Contains(rendered.Markup, "Contrôle d’accès");
            StringAssert.Contains(rendered.Markup, "Audit de sécurité");
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }
}
