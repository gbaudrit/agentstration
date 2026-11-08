using System.Globalization;
using Agentstration.Agents;
using Agentstration.Flows;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Tools;

namespace Agentstration.Infrastructure.Flows;

public sealed class ToolDefinitionUsageReader(IResourceStore store, IFlowRepository flows) : IToolDefinitionUsageReader
{
    public async Task<IReadOnlyList<ToolDefinitionUsage>> ReadAsync(ToolDefinitionResource definition,
        IReadOnlyList<ToolDefinitionVersionResource> versions, CancellationToken cancellationToken)
    {
        var result = new List<ToolDefinitionUsage>();
        var names = versions.ToDictionary(value => AgentstrationToolProvider.ToolResourceName(value.Name),
            value => value.Definition.Version, StringComparer.Ordinal);
        var scope = definition.ScopeRef!.Value;
        foreach (var revision in await store.ListAllAsync<AgentRevision>(AgentResourceKinds.AgentRevision, cancellationToken))
        {
            if (revision.Value.ScopeRef != scope) continue;
            foreach (var pin in revision.Value.Definition.ToolPins.Where(pin => pin.ScopeRef == scope && pin.Namespace == definition.Namespace && names.ContainsKey(pin.Name)))
                result.Add(new("Agent", revision.Value.AgentName, revision.Value.AgentNamespace, scope,
                    revision.Value.Definition.AgentVersion.ToString(CultureInfo.InvariantCulture), names[pin.Name]));
        }
        foreach (var version in await store.ListAllAsync<ToolSetVersionResource>(ToolResourceKinds.ToolSetVersion, cancellationToken))
        {
            if (version.Value.ScopeRef != scope) continue;
            foreach (var member in version.Value.Members.Where(member => member.ToolScopeRef == scope
                && member.ToolNamespace == definition.Namespace && names.ContainsKey(member.ToolName)))
                result.Add(new("ToolSet", version.Value.ToolSetName, version.Value.Namespace, scope, version.Value.Version, names[member.ToolName]));
        }
        for (var skip = 0; ; skip += 100)
        {
            var page = await flows.ListExactAsync(scope, skip, 100, cancellationToken);
            foreach (var flow in page.Items)
                foreach (var version in await flows.ListVersionsExactAsync(scope, flow.Value.Id, cancellationToken))
                    foreach (var step in version.Value.Graph?.Steps.OfType<ToolFlowStepDefinition>() ?? [])
                        if (step.Tool.ResolveNamespace(flow.Value.Id.Namespace) == definition.Namespace
                            && (step.Tool.ScopeRef ?? scope) == scope && names.TryGetValue(step.Tool.ResourceId, out var relayVersion))
                            result.Add(new("Flow", flow.Value.Id.Value, flow.Value.Id.Namespace, scope, version.Value.Version, relayVersion));
            if (!page.HasMore) break;
        }
        return result.Distinct().ToArray();
    }
}
