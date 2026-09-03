using System.Globalization;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Components.Tests;

[TestClass]
[DoNotParallelize]
public sealed class StateComparisonTests
{
    [TestMethod]
    public void ComparisonUsesTheSelectedFrenchCulture()
    {
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            using var context = new BunitContext();
            context.Services.AddLocalization(options => options.ResourcesPath = "Resources");

            var rendered = context.Render<StateComparison>(parameters => parameters
                .Add(value => value.DesiredGeneration, 1)
                .Add(value => value.DesiredStatus, "Définition enregistrée")
                .Add(value => value.ActiveStatus, "La génération 1 de l’agent n’existe pas.")
                .Add(value => value.Summary, "La génération actuelle n’est pas active."));

            Assert.IsTrue(rendered.Markup.Contains("L’état souhaité diffère du déploiement actif", StringComparison.Ordinal));
            Assert.IsTrue(rendered.Markup.Contains("État souhaité", StringComparison.Ordinal));
            Assert.IsTrue(rendered.Markup.Contains("Déploiement actif", StringComparison.Ordinal));
            Assert.IsTrue(rendered.Markup.Contains("Génération 1", StringComparison.Ordinal));
            Assert.IsTrue(rendered.Markup.Contains("Aucune génération active", StringComparison.Ordinal));
            Assert.IsFalse(rendered.Markup.Contains("Desired state", StringComparison.Ordinal));
            Assert.IsFalse(rendered.Markup.Contains("No active generation", StringComparison.Ordinal));
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }
}
