using System.Globalization;
using Agentstration.Web.Components.Pages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Agentstration.Web.Tests;

[TestClass]
[DoNotParallelize]
public sealed class SourcesLocalizationTests
{
    [TestMethod]
    public void CatalogKeepsConsoleAndContentLanguageConceptsDistinctInFrench()
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
            var strings = services.GetRequiredService<IStringLocalizer<SourcesStrings>>();

            Assert.AreEqual("Sources", strings["Title"].Value);
            Assert.AreEqual("Langue du contenu", strings["ContentLanguage"].Value);
            Assert.AreEqual("Sélectionner un Channel", strings["SelectChannel"].Value);
            Assert.AreEqual("Périmètre de propriété", strings["OwnershipScope"].Value);
            Assert.AreEqual("Sélectionnée : fr-FR", strings["SelectedLocale", "fr-FR"].Value);
            Assert.AreEqual("repli depuis fr-FR", strings["FallbackFrom", "fr-FR"].Value);
            Assert.AreEqual("Aucun fournisseur de Sources n’est configuré pour cette instance.", strings["NoConfiguredProviders"].Value);
            Assert.AreEqual("Configurer un fournisseur de Sources", strings["ConfigureProvider"].Value);
            Assert.AreEqual("Détails de la Source", strings["SourceDetails"].Value);
            Assert.AreEqual("Vue d’ensemble", strings["Tab.Overview"].Value);
            Assert.AreEqual("Versions", strings["Tab.Versions"].Value);
            Assert.AreEqual("Bindings", strings["Tab.Bindings"].Value);
            Assert.AreEqual("Contenu", strings["Tab.Content"].Value);
            Assert.AreEqual("Pack issu d’une Source épinglée", strings["PinnedSourcePack"].Value);
            Assert.AreEqual(
                "Source Version 1 (sha256:abc), Channel stable, Snapshot 123, entrée de catalogue say-hello. Cette sélection exacte reste épinglée jusqu’à l’installation.",
                strings["PinnedSourceSelectionMessage", "1", "sha256:abc", "stable", "123", "say-hello"].Value);
            Assert.AreEqual("Confirmer l’installation", strings["ConfirmInstallPack"].Value);
            Assert.AreEqual("Réinstaller le Pack", strings["ReinstallPack"].Value);
            Assert.AreEqual("Confirmer la réinstallation", strings["ConfirmReinstallPack"].Value);
            Assert.AreEqual("Description publiée", strings["PublishedPackDescription"].Value);
            Assert.AreEqual("Ressources requises", strings["PackBindingsTitle"].Value);
            Assert.AreEqual("Profil de modèle", strings["BindingTargetKind.ModelProfile"].Value);
            Assert.AreEqual("Profil d’exécution", strings["BindingTargetKind.RuntimeProfile"].Value);
            Assert.AreEqual("Sélectionner un profil de modèle", strings["SelectPackBindingTarget", "profil de modèle"].Value);
            Assert.AreEqual("1 ressource", strings["PackResourceCount.One", 1].Value);
            Assert.AreEqual("2 ressources", strings["PackResourceCount.Many", 2].Value);
            Assert.AreEqual("Ajouter", strings["Change.Add"].Value);
            Assert.AreEqual("Catalogue indisponible", strings["CatalogUnavailable"].Value);
            Assert.AreEqual("Supprimer la Source", strings["DeleteSourceTitle"].Value);
            Assert.AreEqual("Les Packs déjà installés sont conservés.", strings["DeleteSourceRetainsPacks"].Value);
            Assert.AreEqual("État de l’import", strings["ImportStatus"].Value);
            Assert.AreEqual("Dernier import réussi", strings["LastSuccessfulImport"].Value);
            Assert.AreEqual("Fournisseur de Sources", strings["Provider"].Value);
            Assert.AreEqual("Révision résolue", strings["ResolvedRevision"].Value);
            Assert.AreEqual("Deutsch (Deutschland) · de-DE (repli)", strings["UnavailableLocaleFallback", "Deutsch (Deutschland)", "de-DE"].Value);
            Assert.AreEqual(
                "Git Source Provider — git (configuré à l’enregistrement)",
                strings["DiscoveredProvider", "Git Source Provider", "git"].Value);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }
}
