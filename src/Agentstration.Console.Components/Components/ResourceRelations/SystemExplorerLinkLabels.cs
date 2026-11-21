using Agentstration.Web.Components.Pages;
using Agentstration.Web.Console;
using Microsoft.Extensions.Localization;

namespace Agentstration.Web.Components.ResourceRelations;

internal static class SystemExplorerLinkLabels
{
    internal static string Title(SystemExplorerLink link, IStringLocalizer<SystemExplorerStrings> localizer, Func<Agentstration.SystemGraph.Contracts.SystemGraphNode, string>? displayName = null)
    {
        var sets = link.Paths.Where(path => path.Kind == "toolset" && path.Via is not null).Select(path => path.Via!).DistinctBy(node => node.Id).ToArray();
        var titles = new List<string>();
        if (link.Paths.Any(path => path.Kind == "direct")) titles.Add(localizer["SummaryDirectAccess"]);
        if (sets.Length == 1) titles.Add(localizer["SummaryViaToolSet", displayName?.Invoke(sets[0]) ?? sets[0].DisplayName ?? sets[0].Identity.Name, sets[0].Evidence?.Publication ?? ""]);
        else if (sets.Length > 1) titles.Add(localizer["SummaryViaToolSets", sets.Length]);
        if (titles.Count > 0) return string.Join(" · ", titles);
        if (link.Paths.Any(path => path.Kind == "definition-path")) return localizer["SummaryDefinitionPath"];
        return string.Join(" · ", link.Paths.SelectMany(path => path.Steps).Select(edge => localizer[edge.Kind switch
        {
            "system-membership" => "Membership",
            "exact-dependency" => "Dependency",
            "dynamic-dependency" => "DynamicDependency",
            "generation-of" => "GenerationOf",
            "publication" => "Publication",
            "toolset-access" => "SummaryToolSetAccess",
            _ => "Relation"
        }].Value).Distinct(StringComparer.Ordinal));
    }
}
