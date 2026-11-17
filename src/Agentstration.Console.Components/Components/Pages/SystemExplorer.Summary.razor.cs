using Agentstration.SystemGraph.Contracts;
using Agentstration.Web.Console;
using Microsoft.AspNetCore.Components;

namespace Agentstration.Web.Components.Pages;

public partial class SystemExplorer
{
    [SupplyParameterFromQuery(Name = "technical")] public bool TechnicalQuery { get; set; }
    private bool technicalDiagram;
    private SystemExplorerSummary? summary;
    private SystemGraphPage? visualPage;
    private SystemExplorerLink? selectedLink;
    private IReadOnlyDictionary<string, string> summaryLabels = new Dictionary<string, string>();
    private void RefreshSummary()
    {
        if (page is null || diagramPage is null) { summary = null; visualPage = null; selectedLink = null; return; }
        if (technicalDiagram || Perspective == "deployment")
        { summary = null; visualPage = diagramPage; selectedLink = null; summaryLabels = new Dictionary<string, string>(); return; }
        var next = SystemExplorerSummaries.Create(page, diagramPage, ActiveRoot);
        var nodes = next.Graph.Nodes.Select(node =>
        {
            var versions = next.RepresentedNodes[node.Id];
            var generations = versions.Where(value => value.State == "recorded-generation").Select(value => value.Evidence?.Generation)
                .Where(value => value is not null).Distinct().Order().ToArray();
            var current = versions.FirstOrDefault(value => value.State == "definition")?.Evidence?.Generation;
            var badges = new List<string>();
            if (generations.Length > 0) badges.Add(T("RecordedGenerations") + " " + string.Join(", ", generations));
            if (current is not null && generations.Any(value => value != current)) badges.Add(T("CurrentGeneration") + " " + current);
            return badges.Count == 0 ? node : node with { DisplayName = (node.DisplayName ?? node.Identity.Name) + " · " + string.Join(" · ", badges) };
        }).ToArray();
        var labels = next.Links.ToDictionary(link => link.Id, LinkTitle, StringComparer.Ordinal);
        // Unrelated arrivals beyond the window must not restart a layout.
        if (visualPage is null || !visualPage.Nodes.SequenceEqual(nodes) || !visualPage.Relations.SequenceEqual(next.Graph.Relations)
            || labels.Count != summaryLabels.Count || labels.Any(pair => !summaryLabels.TryGetValue(pair.Key, out var value) || value != pair.Value))
            visualPage = next.Graph with { Nodes = nodes };
        summary = next; summaryLabels = labels;
        if (selectedLink is not null) selectedLink = summary.Links.FirstOrDefault(link => link.Id == selectedLink.Id);
    }
    private string LinkTitle(SystemExplorerLink link)
    {
        var sets = link.Paths.Where(path => path.Kind == "toolset" && path.Via is not null).Select(path => path.Via!).DistinctBy(node => node.Id).ToArray();
        var titles = new List<string>();
        if (link.Paths.Any(path => path.Kind == "direct")) titles.Add(T("SummaryDirectAccess"));
        if (sets.Length == 1) titles.Add(Localizer["SummaryViaToolSet", NodeDisplayName(sets[0]), sets[0].Evidence?.Publication ?? ""]);
        else if (sets.Length > 1) titles.Add(Localizer["SummaryViaToolSets", sets.Length]);
        if (titles.Count > 0) return string.Join(" · ", titles);
        if (link.Paths.Any(path => path.Kind == "definition-path")) return T("SummaryDefinitionPath");
        return string.Join(" · ", link.Paths.SelectMany(path => path.Steps).Select(edge => RelationKind(edge.Kind)).Distinct(StringComparer.Ordinal));
    }
    private void ChangeTechnical(ChangeEventArgs args)
    {
        technicalDiagram = args.Value is true; selectedLink = null; diagramFailed = false;
        RefreshSummary();
    }
    private void SelectLink(string id)
    {
        selectedLink = summary?.Links.FirstOrDefault(link => link.Id == id);
    }
}
