using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;

namespace Agentstration.Web.Components.State;

public interface IConsoleNavigationContext
{
    string? ScopeRef { get; }
}

internal sealed class ConsoleNavigationContext(
    NavigationManager navigation,
    ConsoleContextState contextState) : IConsoleNavigationContext
{
    public string? ScopeRef => GetScopeRef(navigation.Uri) ?? contextState.Current?.SelectedScopeRef;

    internal static string? GetScopeRef(string uri)
    {
        var query = QueryHelpers.ParseQuery(new Uri(uri, UriKind.Absolute).Query);
        var value = query.TryGetValue("context", out var values)
            ? values.FirstOrDefault()
            : null;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
