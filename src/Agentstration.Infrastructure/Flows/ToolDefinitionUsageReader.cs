using System.Globalization;
using Agentstration.Agents;
using Agentstration.Flows;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Identity;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Tools;

namespace Agentstration.Infrastructure.Flows;

public sealed class ToolDefinitionUsageReader(IResourceStore store, IFlowRepository flows,
    Lazy<ResourceScopeInventoryService> inventory, IResourceScopeResolver scopes,
    ICurrentRequestContext current, IRequestContextScopeFactory requests, IResourceReferenceResolver references) : IToolDefinitionUsageReader
{
    public async Task<IReadOnlyList<ToolDefinitionUsage>> ReadAsync(ToolDefinitionResource definition,
        IReadOnlyList<ToolDefinitionVersionResource> versions, CancellationToken cancellationToken)
    {
        var result = new List<ToolDefinitionUsage>();
        var names = versions.ToDictionary(value => AgentstrationToolProvider.ToolResourceName(value.Name),
            value => value.Definition.Version, StringComparer.Ordinal);
        var scope = definition.ScopeRef!.Value;
        var caller = current.Current;
        foreach (var node in await inventory.Value.GetAsync(cancellationToken))
        {
            var hierarchy = await scopes.ResolveAsync(node.ScopeRef, cancellationToken);
            if (node.ScopeRef != scope && hierarchy?.Ancestors.Any(ancestor => ancestor.Ref == scope) != true) continue;
            var consumer = node.ScopeRef;
            using var request = consumer.Kind switch
            {
                ResourceScopeKind.Instance => requests.PushInstance(caller),
                ResourceScopeKind.Tenant => requests.PushTenant(caller.PrincipalId, consumer.TargetId!.Value),
                _ => requests.Push(caller with { TenantId = hierarchy!.Ancestors.Single(ancestor => ancestor.Ref.Kind == ResourceScopeKind.Tenant).Ref.TargetId!.Value, WorkspaceId = consumer.TargetId!.Value })
            };
            foreach (var revision in await store.ListAllAsync<AgentRevision>(AgentResourceKinds.AgentRevision, cancellationToken))
            {
                if (revision.Value.ScopeRef != consumer) continue;
                foreach (var pin in revision.Value.Definition.ToolPins.Where(pin => pin.ScopeRef == scope && pin.Namespace == definition.Namespace && names.ContainsKey(pin.Name)))
                    result.Add(new("Agent", revision.Value.AgentName, revision.Value.AgentNamespace, consumer,
                        revision.Value.Definition.AgentVersion.ToString(CultureInfo.InvariantCulture), names[pin.Name]));
            }
            foreach (var version in await store.ListAllAsync<ToolSetVersionResource>(ToolResourceKinds.ToolSetVersion, cancellationToken))
            {
                if (version.Value.ScopeRef != consumer) continue;
                foreach (var member in version.Value.Members.Where(member => member.ToolScopeRef == scope
                    && member.ToolNamespace == definition.Namespace && names.ContainsKey(member.ToolName)))
                    result.Add(new("ToolSet", version.Value.ToolSetName, version.Value.Namespace, consumer, version.Value.Version, names[member.ToolName]));
            }
            for (var skip = 0; ; skip += 100)
            {
                var page = await flows.ListExactAsync(consumer, skip, 100, cancellationToken);
                foreach (var flow in page.Items)
                    foreach (var version in await flows.ListVersionsExactAsync(consumer, flow.Value.Id, cancellationToken))
                        foreach (var step in version.Value.Graph?.Steps.OfType<ToolFlowStepDefinition>() ?? [])
                            if (step.Tool.ResolveNamespace(flow.Value.Id.Namespace) == definition.Namespace
                                && names.TryGetValue(step.Tool.ResourceId, out var relayVersion))
                            {
                                // Keep recorded usages visible even after target disablement or revocation.
                                var targetScope = step.Tool.ScopeRef ?? consumer;
                                if (step.Tool.ScopeRef is null && consumer != scope)
                                {
                                    var target = await references.ResolveAsync<ToolResource>(new(step.Tool.ResourceId, null, definition.Namespace),
                                        flow.Value.Id.Namespace, ToolResourceKinds.Tool, consumer, cancellationToken);
                                    targetScope = target?.Value.ScopeRef ?? consumer;
                                }
                                if (targetScope == scope)
                                    result.Add(new("Flow", flow.Value.Id.Value, flow.Value.Id.Namespace, consumer, version.Value.Version, relayVersion));
                            }
                if (!page.HasMore) break;
            }
        }
        return result.Distinct().ToArray();
    }
}
