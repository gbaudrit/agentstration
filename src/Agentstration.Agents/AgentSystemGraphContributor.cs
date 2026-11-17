using System.Globalization;
using System.Runtime.CompilerServices;
using Agentstration.ResourceManagement;
using Agentstration.Models;
using Agentstration.Runtime.Abstractions;
using Agentstration.Resources;
using Agentstration.SystemGraph.Contracts;

namespace Agentstration.Agents;

public sealed class AgentSystemGraphContributor(IResourceStore resources, IResourceReferenceResolver references) : ISystemGraphContributor
{
    public string Family => "agents";
    public SystemGraphContributorCoverage Coverage => new(Family, ["model-runtime-generations", "tool-generations", "toolset-publications", "declared-tools-and-toolsets", "schema-publications", "agent-revisions"], ["opaque-middleware-and-settings"]);

    public async IAsyncEnumerable<SystemGraphContribution> ReadAsync(ResourceScopeRef scope, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var stored in ResourceSystemGraph.ReadAsync<AgentResource>(resources, scope, AgentResourceKinds.Agent, cancellationToken))
        {
            var node = ResourceSystemGraph.Node(stored.Value);
            var declared = new List<SystemGraphRelation>();
            await AddDeclaredAsync<ModelProfileResource>(stored.Value.Definition.ModelProfile, ModelResourceKinds.ModelProfile, "definition.modelProfile");
            await AddDeclaredAsync<RuntimeProfileResource>(stored.Value.Definition.RuntimeProfile, RuntimeProfileResourceKinds.RuntimeProfile, "definition.runtimeProfile");
            for (var index = 0; index < stored.Value.Definition.Tools.Count; index++)
                await AddDeclaredAsync<Agentstration.Tools.ToolResource>(stored.Value.Definition.Tools[index], Agentstration.Tools.ToolResourceKinds.Tool, $"definition.tools[{index}]");
            for (var index = 0; index < stored.Value.Definition.ToolSets.Count; index++)
            {
                var selection = stored.Value.Definition.ToolSets[index];
                await AddDeclaredAsync<Agentstration.Tools.ToolSetResource>(selection.ToolSet, Agentstration.Tools.ToolResourceKinds.ToolSet, $"definition.toolSets[{index}]", selection.Version);
            }
            yield return new([node], declared);

            async Task AddDeclaredAsync<T>(ResourceReference reference, string kind, string field, string? version = null) where T : Resource
            {
                try
                {
                    var target = await references.ResolveAsync<T>(reference, stored.Value.Namespace, kind, scope, cancellationToken);
                    if (target is not null) declared.Add(new(node.Id,
                        version is null ? SystemGraphIds.Definition(target.Value.Uid) : SystemGraphIds.Publication(target.Value.Uid, version),
                        "declared-reference", field, reference.ScopeRef is null ? "owner-relative" : "exact-owner")
                    { SelectedPublication = version });
                }
                catch (Exception exception) when (exception is ResourceReferenceAmbiguousException or ResourceReferenceOutsideScopeException)
                { /* Missing/ambiguous declarations never manufacture exact pins. */ }
            }
        }
        await foreach (var stored in ResourceSystemGraph.ReadAsync<AgentRevision>(resources, scope, AgentResourceKinds.AgentRevision, cancellationToken))
        {
            var revision = stored.Value;
            var source = ResourceSystemGraph.Node(revision, state: "publication", publication: revision.AgentVersion.ToString(CultureInfo.InvariantCulture), hash: revision.DefinitionHash);
            var nodes = new List<SystemGraphNode> { source };
            var relations = new List<SystemGraphRelation> { new(SystemGraphIds.Definition(revision.AgentUid), source.Id, "publication") };
            foreach (var (pin, field) in new[] { (revision.Definition.ModelProfilePin, "definition.modelProfile"), (revision.Definition.RuntimeProfilePin, "definition.runtimeProfile") }
                .Concat(revision.Definition.ToolPins.Select((pin, index) => ((ResolvedResourceDependency?)pin, $"definition.tools[{index}]"))))
            {
                if (pin is null) continue; // Legacy/unresolved evidence is not resolved or invented here.
                var id = SystemGraphIds.Generation(pin.Uid, pin.Generation);
                nodes.Add(new(id, new(pin.Uid, pin.ScopeRef, pin.Namespace, pin.Kind, pin.Name), "recorded-generation", new(pin.Generation)));
                relations.Add(new(id, SystemGraphIds.Definition(pin.Uid), "generation-of"));
                relations.Add(new(source.Id, id, "exact-dependency", field, "recorded-pin") { SelectedPublication = pin.Version });
            }
            foreach (var assignment in revision.Definition.ToolSetAssignments)
                relations.Add(new(source.Id, SystemGraphIds.Publication(assignment.ToolSetUid, assignment.Version), "exact-dependency", "definition.toolSets", "recorded-pin"));
            foreach (var schema in revision.Definition.SchemaBindings)
                if (schema.Value.SchemaUid is { } uid && schema.Value.Source is { } reference)
                    relations.Add(new(source.Id, SystemGraphIds.Publication(uid, reference.Version), "exact-dependency", schema.Key, "recorded-schema")
                    { SelectedPublication = reference.Version, TargetField = reference.SchemaPath, ExpectedHash = schema.Value.DefinitionHash, ParentHash = schema.Value.ParentDefinitionHash });
            yield return new(nodes, relations) { BindingOwner = source.Identity, BindingOwnerNodeId = source.Id };
        }
    }
}
