using Agentstration.ResourceManagement;
using Agentstration.SystemGraph.Contracts;
using Agentstration.Tools;

namespace Agentstration.Agents;

public sealed partial class AgentSystemGraphContributor
{
    private static SystemGraphContribution ToolSetAccess(SystemGraphNode agent, ToolSetVersionResource publication,
        IReadOnlyList<string> selected, IReadOnlyCollection<ResolvedResourceDependency>? recordedPins, string resolution)
    {
        var via = ResourceSystemGraph.Publication(publication, publication.ToolSetUid, ToolResourceKinds.ToolSet,
            publication.ToolSetName, publication.Version, publication.DefinitionHash, generation: publication.ToolSetGeneration,
            publishedAt: publication.PublishedAt);
        var nodes = new List<SystemGraphNode>(); var edges = new List<SystemGraphRelation>();
        foreach (var member in publication.Members)
        {
            var key = ToolResourceIdentity.CatalogId(member.ToolNamespace, member.ToolName);
            // A draft's empty selection means all members. A revision stores its exact selected keys.
            if (!(recordedPins is null && selected.Count == 0) && !selected.Contains(key, StringComparer.Ordinal)) continue;
            if (recordedPins is not null && !recordedPins.Any(pin => pin.Uid == member.ToolUid && pin.Generation == member.ToolGeneration
                && pin.ScopeRef == member.ToolScopeRef && pin.Namespace == member.ToolNamespace && pin.Kind == ToolResourceKinds.Tool)) continue;
            var id = SystemGraphIds.Generation(member.ToolUid, member.ToolGeneration);
            nodes.Add(new(id, new(member.ToolUid, member.ToolScopeRef, member.ToolNamespace, ToolResourceKinds.Tool, member.ToolName),
                "recorded-generation", new(member.ToolGeneration)));
            edges.Add(new(agent.Id, id, "toolset-access", "definition.toolSets.members[" + key + "]", resolution)
            { ViaNodeId = via.Id, SelectedPublication = publication.Version, ExpectedHash = publication.DefinitionHash });
        }
        return new(nodes, edges) { BindingOwner = via.Identity };
    }
}
