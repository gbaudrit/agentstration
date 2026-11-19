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
    private string LinkTitle(SystemExplorerLink link) =>
        Agentstration.Web.Components.ResourceRelations.SystemExplorerLinkLabels.Title(link, Localizer, NodeDisplayName);
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
