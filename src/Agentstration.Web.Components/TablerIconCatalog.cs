using System.Collections.Frozen;
using System.Reflection;

namespace Agentstration.Web.Components;

public static class TablerIconCatalog
{
    private const string ResourceName = "Agentstration.Web.Components.TablerIcons.tabler-icons.txt";
    private const string SpritePath = "_content/Agentstration.Web.Components/tabler/tabler-sprite.svg";
    private static readonly string[] names = LoadNames();
    private static readonly FrozenSet<string> nameSet = names.ToFrozenSet(StringComparer.Ordinal);
    private static readonly string[] featuredNames =
    [
        "sparkles", "message-chatbot", "robot", "wand", "bulb", "search", "home", "layout-grid",
        "clipboard-check", "file-text", "forms", "list-check", "calendar", "clock", "bell", "mail",
        "user", "users", "briefcase", "building", "school", "heart", "star", "bookmark",
        "plane", "car", "map", "world", "shopping-cart", "credit-card", "wallet", "chart-bar",
        "database", "server", "cloud", "code", "terminal", "tool", "settings", "shield-check",
        "lock", "key", "folder", "package", "puzzle", "rocket", "bolt", "flame"
    ];

    public static IReadOnlyList<string> All => names;

    public static IconSearchResult Search(string? query, int maximumResults = 60)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumResults, 1);
        var terms = (query ?? string.Empty)
            .Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        IEnumerable<string> matches = terms.Length == 0
            ? featuredNames.Where(nameSet.Contains)
            : names.Where(name => terms.All(term => name.Contains(term, StringComparison.OrdinalIgnoreCase)));
        var materialized = matches.Take(maximumResults + 1).ToArray();
        return new IconSearchResult(materialized.Take(maximumResults).ToArray(), materialized.Length > maximumResults);
    }

    public static bool TryNormalize(string? value, out string name)
    {
        var candidate = value?.Trim() ?? string.Empty;
        if (candidate.StartsWith("tabler:", StringComparison.OrdinalIgnoreCase)) candidate = candidate[7..];
        if (nameSet.Contains(candidate))
        {
            name = candidate;
            return true;
        }

        name = string.Empty;
        return false;
    }

    public static string SpriteHref(string name)
    {
        if (!TryNormalize(name, out var normalized)) throw new ArgumentException("Unknown Tabler icon name.", nameof(name));
        return $"{SpritePath}#tabler-{normalized}";
    }

    private static string[] LoadNames()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded Tabler icon catalog '{ResourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd()
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}

public sealed record IconSearchResult(IReadOnlyList<string> Names, bool HasMore);
