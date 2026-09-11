using Agentstration.Management.Abstractions;

namespace Agentstration.Web.Components.Pages;

internal static class SourceConsoleLocalePresentation
{
    public static IReadOnlyList<string> AvailableLocales(
        IReadOnlyList<SourceCatalogView> catalogs,
        string requestedLocale)
    {
        var available = catalogs
            .SelectMany(catalog => catalog.BootstrapEntries)
            .SelectMany(entry => entry.Variants)
            .Select(variant => variant.Locale)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
        if (!available.Contains(requestedLocale, StringComparer.Ordinal))
            available.Insert(0, requestedLocale);
        return available;
    }

    public static bool IsAvailable(IReadOnlyList<SourceCatalogView> catalogs, string locale) =>
        catalogs
            .SelectMany(catalog => catalog.BootstrapEntries)
            .SelectMany(entry => entry.Variants)
            .Any(variant => variant.Locale == locale);
}
