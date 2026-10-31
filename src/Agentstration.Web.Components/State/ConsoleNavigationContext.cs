using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;

namespace Agentstration.Web.Components.State;

public interface IConsoleNavigationContext
{
    string? ScopeRef { get; }
    string AddToUri(string uri);
    void SetSelectedScopeRef(string? scopeRef) { }
}

internal sealed class ConsoleNavigationContext(NavigationManager navigation) : IConsoleNavigationContext
{
    private string? selectedScopeRef;

    public string? ScopeRef
    {
        get
        {
            try { return GetScopeRef(navigation.Uri) ?? selectedScopeRef; }
            catch (InvalidOperationException) { return selectedScopeRef; }
        }
    }

    public string AddToUri(string uri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        var scopeRef = ScopeRef;
        if (string.IsNullOrWhiteSpace(scopeRef)) return uri;
        var target = navigation.ToAbsoluteUri(uri);
        var application = new Uri(navigation.BaseUri);
        if (!string.Equals(target.Scheme, application.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(target.Authority, application.Authority, StringComparison.OrdinalIgnoreCase)
            || IsTechnicalRoute(target.AbsolutePath)
            || GetScopeRef(target.AbsoluteUri) is not null)
            return uri;
        return QueryHelpers.AddQueryString(uri, "context", scopeRef);
    }

    public void SetSelectedScopeRef(string? scopeRef) => selectedScopeRef = scopeRef;

    internal static string? GetScopeRef(string uri)
    {
        var query = QueryHelpers.ParseQuery(new Uri(uri, UriKind.Absolute).Query);
        var value = query.TryGetValue("context", out var values)
            ? values.FirstOrDefault()
            : null;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static bool IsTechnicalRoute(string path) =>
        path.StartsWith("/_", StringComparison.Ordinal)
        || path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
        || string.Equals(path, "/login", StringComparison.OrdinalIgnoreCase)
        || string.Equals(path, "/logout", StringComparison.OrdinalIgnoreCase);
}
