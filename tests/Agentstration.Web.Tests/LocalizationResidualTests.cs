using System.Globalization;
using System.Xml.Linq;
using Agentstration.Web.Components.Pages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Agentstration.Web.Tests;

[TestClass]
[DoNotParallelize]
public sealed class LocalizationResidualTests
{
    [TestMethod]
    public void EveryUiResourceCatalogHasACompleteFrenchTranslation()
    {
        var sourceRoot = Path.Combine(FindRepositoryRoot(), "src");
        var neutralCatalogs = Directory
            .EnumerateFiles(sourceRoot, "*.resx", SearchOption.AllDirectories)
            .Where(path => !Path.GetFileNameWithoutExtension(path).EndsWith(".fr-FR", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.IsNotEmpty(neutralCatalogs);
        foreach (var neutralCatalog in neutralCatalogs)
        {
            var frenchCatalog = Path.Combine(
                Path.GetDirectoryName(neutralCatalog)!,
                $"{Path.GetFileNameWithoutExtension(neutralCatalog)}.fr-FR.resx");
            var relativeCatalog = Path.GetRelativePath(sourceRoot, neutralCatalog);

            Assert.IsTrue(File.Exists(frenchCatalog), $"Missing fr-FR catalog for {relativeCatalog}.");
            var neutralEntries = ReadEntries(neutralCatalog, relativeCatalog);
            var frenchEntries = ReadEntries(frenchCatalog, Path.GetRelativePath(sourceRoot, frenchCatalog));
            Assert.AreEqual(
                string.Join('\n', neutralEntries.Keys.Order(StringComparer.Ordinal)),
                string.Join('\n', frenchEntries.Keys.Order(StringComparer.Ordinal)),
                $"Resource keys differ between the neutral and fr-FR catalogs for {relativeCatalog}.");
        }
    }

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
            Assert.AreEqual("Profils de bootstrap", L<BootstrapProfilesStrings>(services)["Title"].Value);
            Assert.AreEqual("Sélectionner une ressource compatible", L<BootstrapProfilesStrings>(services)["SelectCompatibleResource"].Value);
            Assert.AreEqual("Profil de modèle", L<BootstrapProfilesStrings>(services)["BindingTargetKind.ModelProfile"].Value);
        }
        finally { CultureInfo.CurrentCulture = originalCulture; CultureInfo.CurrentUICulture = originalUiCulture; }
    }

    private static IStringLocalizer<T> L<T>(IServiceProvider services) => services.GetRequiredService<IStringLocalizer<T>>();

    private static IReadOnlyDictionary<string, string> ReadEntries(string path, string relativeCatalog)
    {
        var entries = XDocument.Load(path)
            .Root!
            .Elements("data")
            .Select(element => new
            {
                Name = (string?)element.Attribute("name"),
                Value = (string?)element.Element("value")
            })
            .ToArray();

        Assert.IsTrue(entries.All(entry => !string.IsNullOrWhiteSpace(entry.Name)), $"Unnamed resource in {relativeCatalog}.");
        Assert.IsTrue(entries.All(entry => !string.IsNullOrWhiteSpace(entry.Value)), $"Empty resource value in {relativeCatalog}.");
        var duplicate = entries
            .GroupBy(entry => entry.Name!, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        Assert.IsNull(duplicate, $"Duplicate resource key '{duplicate?.Key}' in {relativeCatalog}.");
        return entries.ToDictionary(entry => entry.Name!, entry => entry.Value!, StringComparer.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Agentstration.slnx"))) return directory.FullName;
        }

        throw new InvalidOperationException("Unable to locate the repository root.");
    }
}
