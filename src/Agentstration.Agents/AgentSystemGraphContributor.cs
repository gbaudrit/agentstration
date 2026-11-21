using System.Globalization;
using System.Runtime.CompilerServices;
using Agentstration.Models;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.SystemGraph.Contracts;

namespace Agentstration.Agents;

public sealed partial class AgentSystemGraphContributor(IResourceStore resources, IResourceReferenceResolver references, Agentstration.Tools.ToolSetService toolSets) : ISystemGraphContributor
{
    public string Family => "agents";
    public SystemGraphContributorCoverage Coverage => new(Family, ["model-runtime-generations", "tool-generations", "toolset-publications", "declared-tools-and-toolsets", "schema-publications", "agent-revisions", "recorded-deployment-configurations"], ["opaque-middleware-and-settings", "legacy-deployment-bindings-without-uids"]);

    public async IAsyncEnumerable<SystemGraphContribution> ReadAsync(ResourceScopeRef scope, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var publications = new Dictionary<(Guid Uid, string Version), Agentstration.Tools.ToolSetVersionResource?>();
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
            foreach (var selection in stored.Value.Definition.ToolSets)
            {
                StoredResource<Agentstration.Tools.ToolSetResource>? resolved;
                try
                {
                    resolved = await references.ResolveAsync<Agentstration.Tools.ToolSetResource>(selection.ToolSet, stored.Value.Namespace,
                    Agentstration.Tools.ToolResourceKinds.ToolSet, scope, cancellationToken);
                }
                catch (Exception error) when (error is ResourceReferenceAmbiguousException or ResourceReferenceOutsideScopeException) { continue; }
                if (resolved is null) continue;
                var publication = await PublicationAsync(resolved.Value.ScopeRef!.Value, resolved.Value.Namespace, resolved.Value.Name, selection.Version, resolved.Value.Uid);
                if (publication is null) continue;
                yield return ToolSetAccess(node, publication, selection.Members, null, "declared-toolset-member");
            }

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
            {
                var publication = await PublicationAsync(assignment.ToolSetScopeRef, assignment.ToolSetNamespace, assignment.ToolSetName, assignment.Version, assignment.ToolSetUid);
                if (publication is null)
                {
                    // Keep the original canonical publication reference even when a legacy
                    // publication does not use today's address convention. The graph still
                    // requires that exact authorized publication endpoint to exist. Missing
                    // selection evidence never establishes a Tool access grant.
                    var identity = await resources.GetIdentityByUidAsync(assignment.ToolSetUid, cancellationToken);
                    if (identity is not null && identity.Kind == Agentstration.Tools.ToolResourceKinds.ToolSet
                        && identity.ScopeRef == assignment.ToolSetScopeRef && identity.Namespace == assignment.ToolSetNamespace
                        && identity.Name == assignment.ToolSetName)
                        relations.Add(new(source.Id, SystemGraphIds.Publication(assignment.ToolSetUid, assignment.Version),
                            "exact-dependency", "definition.toolSets", "recorded-pin")
                        { SelectedPublication = assignment.Version, ExpectedHash = assignment.DefinitionHash });
                    continue;
                }
                if (publication.DefinitionHash != assignment.DefinitionHash
                    || (publication.Uid == assignment.ToolSetUid ? publication.Generation : publication.ToolSetGeneration) != assignment.ToolSetGeneration) continue;
                relations.Add(new(source.Id, SystemGraphIds.Publication(publication.ToolSetUid, publication.Version), "exact-dependency", "definition.toolSets", "recorded-pin"));
                yield return ToolSetAccess(source, publication, assignment.Members, revision.Definition.ToolPins, "recorded-toolset-member");
            }
            foreach (var schema in revision.Definition.SchemaBindings)
                if (schema.Value.SchemaUid is { } uid && schema.Value.Source is { } reference)
                    relations.Add(new(source.Id, SystemGraphIds.Publication(uid, reference.Version), "exact-dependency", schema.Key, "recorded-schema")
                    { SelectedPublication = reference.Version, TargetField = reference.SchemaPath, ExpectedHash = schema.Value.DefinitionHash, ParentHash = schema.Value.ParentDefinitionHash });
            yield return new(nodes, relations) { BindingOwner = source.Identity, BindingOwnerNodeId = source.Id };
        }
        await foreach (var stored in ResourceSystemGraph.ReadAsync<AgentDeployment>(resources, scope, AgentResourceKinds.AgentDeployment, cancellationToken))
        {
            var deployment = ResourceSystemGraph.Node(stored.Value);
            yield return new([deployment], []);
            foreach (var config in stored.Value.Configurations)
            {
                var id = $"deployment:{stored.Value.Uid:D}:{config.Id:D}";
                var node = new SystemGraphNode(id, deployment.Identity, "recorded-deployment-configuration",
                    new(Publication: config.Id.ToString("D"), Hash: config.DefinitionHash))
                {
                    DisplayName = stored.Value.Name,
                    Deployment = new(stored.Value.Uid, config.Id, config.AgentUid, config.RevisionUid, config.RevisionOwner,
                        config.RevisionNamespace, config.RevisionName, config.AgentVersion, config.DefinitionHash,
                        config.Environment, config.Target.Id, config.Target.Name, config.Target.Kind, config.Target.Runtime, config.RecordedAt)
                };
                // Only the selected configuration is followed by default. History is selected explicitly.
                var edges = new List<SystemGraphRelation> { new(node.Id, SystemGraphIds.Definition(config.RevisionUid), "deployed-revision", "configuration.revision", "recorded-uid") };
                if (config.Id == stored.Value.Configurations.LastOrDefault()?.Id) edges.Add(new(deployment.Id, node.Id, "deployment-configuration"));
                yield return new([node], edges) { BindingOwner = deployment.Identity, BindingOwnerNodeId = deployment.Id };
            }
        }
        async Task<Agentstration.Tools.ToolSetVersionResource?> PublicationAsync(ResourceScopeRef owner, ResourceNamespace ns, string name, string version, Guid uid)
        {
            if (publications.TryGetValue((uid, version), out var found))
                return found?.ScopeRef == owner && found.Namespace == ns && found.ToolSetName == name ? found : null;
            var identity = await resources.GetIdentityByUidAsync(uid, cancellationToken);
            StoredResource<Agentstration.Tools.ToolSetVersionResource>? stored = null;
            if (identity is not null && identity.ScopeRef == owner && identity.Namespace == ns)
            {
                if (identity.Kind == Agentstration.Tools.ToolResourceKinds.ToolSetVersion)
                    stored = await resources.GetExactAsync<Agentstration.Tools.ToolSetVersionResource>(new(owner, ns, identity.Kind, identity.Name), cancellationToken);
                else if (identity.Kind == Agentstration.Tools.ToolResourceKinds.ToolSet && identity.Name == name)
                    stored = await toolSets.GetVersionExactAsync(owner, ns, name, version, cancellationToken);
            }
            var value = stored?.Value;
            found = value is not null && (value.Uid == uid || value.ToolSetUid == uid) && value.ScopeRef == owner && value.Namespace == ns
                && value.ToolSetName == name && value.Version == version ? value : null;
            publications[(uid, version)] = found;
            return found;
        }
    }
}
