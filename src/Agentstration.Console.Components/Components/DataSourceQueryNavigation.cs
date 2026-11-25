using Agentstration.DataSources.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Console;
using Microsoft.AspNetCore.WebUtilities;
namespace Agentstration.Web.Components;

internal static class DataSourceQueryNavigation
{
    public static string Destination(DataSourceQueryResource query, IServiceProvider services, bool edit)
    {
        var source = query.Definition.Selection.Source;
        if (source.ScopeRef is not { } owner || query.ScopeRef is not { } queryOwner) return "";
        var path = ResourceOwnershipContext.Resource(services, "/data-sources", source.Name, source.Namespace ?? query.Namespace, owner);
        return QueryHelpers.AddQueryString(path, new Dictionary<string, string?>
        {
            ["query"] = query.Name,
            ["queryNamespace"] = query.Namespace.Value,
            ["queryOwner"] = queryOwner.Value,
            ["queryView"] = edit ? "definition" : "overview"
        }) + "#queries";
    }
}
