using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;

namespace Agentstration.Web.Components.State;

public interface IConsoleNavigationContext
{
    string? ScopeRef { get; }
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

    public void SetSelectedScopeRef(string? scopeRef) => selectedScopeRef = scopeRef;

    internal static string? GetScopeRef(string uri)
    {
        var query = QueryHelpers.ParseQuery(new Uri(uri, UriKind.Absolute).Query);
        var value = query.TryGetValue("context", out var values)
            ? values.FirstOrDefault()
            : null;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
