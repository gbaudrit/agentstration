using System.Runtime.CompilerServices;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.SystemGraph.Contracts;

namespace Agentstration.Tools;

public sealed class ToolSystemGraphContributor(IResourceStore resources, IResourceIdentityCatalog identities,
    IResourceReferenceResolver references) : ISystemGraphContributor
{
    public string Family => "tools";
    public SystemGraphContributorCoverage Coverage => new(Family, ["tool-provider-metadata", "relay-tool-provider-generations", "toolset-member-generations", "parameter-secret-identity-references", "tool-publication-materialization"], ["flow-backed-and-acquisition-targets-without-recorded-uids"]);

    public async IAsyncEnumerable<SystemGraphContribution> ReadAsync(ResourceScopeRef scope, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var stored in ResourceSystemGraph.ReadAsync<ToolProviderResource>(resources, scope, ToolResourceKinds.ToolProvider, ct))
            yield return new([ResourceSystemGraph.Node(stored.Value) with
            {
                DisplayName = stored.Value.Definition.DisplayName,
                Evidence = ResourceSystemGraph.Node(stored.Value).Evidence! with { Enabled = stored.Value.Definition.Enabled }
            }], []);
        await foreach (var stored in ResourceSystemGraph.ReadAsync<ToolResource>(resources, scope, ToolResourceKinds.Tool, ct))
        {
            var tool = stored.Value;
            var builder = new SystemGraphContributionBuilder(ResourceSystemGraph.Node(tool) with { DisplayName = tool.Definition.DisplayName }, identities);
            if (tool.Definition.Provider is { } provider)
            {
                var resolved = await references.ResolveAsync<ToolProviderResource>(provider, tool.Namespace, ToolResourceKinds.ToolProvider, scope, ct);
                if (resolved is not null) await builder.PinAsync(resolved.Value.Uid, "definition.provider", ct, kind: "declared-reference", resolution: "owner-relative");
            }
            // These are the Tool family's explicit materialization metadata, not arbitrary JSON scanning.
            if (tool.Definition.Metadata.TryGetValue("agentstration.toolDefinitionUid", out var owner) && owner.TryGetGuid(out var ownerUid)
                && tool.Definition.Version is { } version)
                await builder.PinAsync(ownerUid, "materialization.publication", ct, version: version);
            yield return builder.Build();
        }
        await foreach (var stored in ResourceSystemGraph.ReadAsync<ToolDefinitionResource>(resources, scope, ToolResourceKinds.ToolDefinition, ct))
            yield return new([ResourceSystemGraph.Node(stored.Value)], []);
        await foreach (var stored in ResourceSystemGraph.ReadAsync<ToolDefinitionVersionResource>(resources, scope, ToolResourceKinds.ToolDefinitionVersion, ct))
        {
            var publication = stored.Value;
            var node = ResourceSystemGraph.Publication(publication, publication.DefinitionUid, ToolResourceKinds.ToolDefinition,
                publication.DefinitionName, publication.Definition.Version, publishedAt: publication.PublishedAt);
            var builder = new SystemGraphContributionBuilder(node, identities);
            builder.PublicationOf(publication.DefinitionUid);
            if (publication.Source is { } relay)
            {
                await builder.PinAsync(relay.Tool.Uid, "source.tool", ct, relay.Tool.Generation);
                await builder.PinAsync(relay.Provider.Uid, "source.provider", ct, relay.Provider.Generation);
            }
            else if (publication.Definition is RelayToolDefinitionProperties target)
                await builder.PinAsync(target.Target.Uid, "definition.target", ct, target.Target.Generation);
            foreach (var binding in publication.BindingIdentities)
                await builder.PinAsync(binding.Uid, "bindings" + binding.Path, ct, kind: "value-reference", resolution: "fixed-identity", identityOnly: true);
            yield return builder.Build();
        }
        await foreach (var stored in ResourceSystemGraph.ReadAsync<ToolSetResource>(resources, scope, ToolResourceKinds.ToolSet, ct))
            yield return new([ResourceSystemGraph.Node(stored.Value)], []);
        await foreach (var stored in ResourceSystemGraph.ReadAsync<ToolSetVersionResource>(resources, scope, ToolResourceKinds.ToolSetVersion, ct))
        {
            var publication = stored.Value;
            var builder = new SystemGraphContributionBuilder(ResourceSystemGraph.Publication(publication, publication.ToolSetUid,
                ToolResourceKinds.ToolSet, publication.ToolSetName, publication.Version, publication.DefinitionHash, generation: publication.ToolSetGeneration, publishedAt: publication.PublishedAt), identities);
            builder.PublicationOf(publication.ToolSetUid);
            foreach (var member in publication.Members)
            {
                await builder.PinAsync(member.ToolUid, $"members[{member.Route}].tool", ct, member.ToolGeneration);
                await builder.PinAsync(member.ProviderUid, $"members[{member.Route}].provider", ct, member.ProviderGeneration);
            }
            yield return builder.Build();
        }
    }
}
