using System.Runtime.CompilerServices;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Resources;
using Agentstration.SystemGraph.Contracts;

namespace Agentstration.Flows.Application;

public sealed record FlowToolGraphIdentity(SystemGraphIdentity Identity, long Generation);

public sealed class FlowSystemGraphContributor(IFlowRepository flows, IFlowToolContractResolver? toolReferences = null) : ISystemGraphContributor
{
    public string Family => "flows";
    public SystemGraphContributorCoverage Coverage => new(Family, ["agent-router-revisions", "flow-call-uid-publications", "function-publications", "schema-publications", "live-owner-relative-tool-bindings"], ["legacy-composite-without-uids", "active-flow-selection-not-a-recorded-pin", "toolroute-without-recorded-toolset-uid"]);

    public async IAsyncEnumerable<SystemGraphContribution> ReadAsync(ResourceScopeRef scope, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        const int pageSize = 200;
        for (var offset = 0; ; offset += pageSize)
        {
            var page = await flows.ListExactAsync(scope, offset, pageSize, cancellationToken);
            foreach (var stored in page.Items)
            {
                var flow = stored.Value;
                var identity = new SystemGraphIdentity(flow.Uid, scope, flow.Id.Namespace, FlowResourceKinds.Flow, flow.Id.Value);
                var definition = new SystemGraphNode(SystemGraphIds.Definition(flow.Uid), identity, "definition", new(ETag: stored.ETag, OriginKind: flow.Governance.Origin.Kind.ToString()));
                yield return new([definition], []);
                for (var versionOffset = 0; ; versionOffset += pageSize)
                {
                    var versionPage = await flows.ListVersionsPageExactAsync(scope, flow.Id, versionOffset, pageSize, cancellationToken);
                    foreach (var publication in versionPage.Items)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var version = publication.Value;
                        if (version.ResourceUid is { } attested && attested != flow.Uid) continue;
                        var node = new SystemGraphNode(SystemGraphIds.Publication(flow.Uid, version.Version), identity, "publication",
                            new(ETag: publication.ETag, Publication: version.Version, Hash: version.DefinitionHash, OriginKind: version.Governance.Origin.Kind.ToString()) { PublishedAt = version.PublishedAt });
                        var edges = new List<SystemGraphRelation> { new(definition.Id, node.Id, "publication") };
                        var nodes = new List<SystemGraphNode> { node };
                        edges.AddRange(SchemaReferences(version, node.Id));
                        foreach (var (targetId, path) in References(version))
                            edges.Add(new(node.Id, targetId, "exact-dependency", path, "recorded-pin"));
                        if (toolReferences is not null)
                            foreach (var step in version.Graph?.Steps.OfType<ToolFlowStepDefinition>() ?? [])
                            {
                                var target = await toolReferences.InspectDependencyAsync(scope, flow.Id.Namespace, step.Tool, cancellationToken);
                                if (target is null) continue;
                                var id = SystemGraphIds.Generation(target.Identity.ResourceUid, target.Generation);
                                nodes.Add(new(id, target.Identity, "recorded-generation", new(target.Generation)));
                                edges.Add(new(id, SystemGraphIds.Definition(target.Identity.ResourceUid), "generation-of"));
                                edges.Add(new(node.Id, id, "dynamic-dependency", $"graph.steps[{step.Name}].tool", "owner-relative-runtime"));
                            }
                        yield return new(nodes, edges) { BindingOwner = node.Identity, BindingOwnerNodeId = node.Id };
                    }
                    if (!versionPage.HasMore) break;
                }
            }
            if (!page.HasMore) yield break;
        }
    }

    private static IEnumerable<SystemGraphRelation> SchemaReferences(FlowVersion version, string source)
    {
        foreach (var binding in version.SchemaBindings ?? new Dictionary<string, Agentstration.Schemas.Contracts.SchemaBindingSnapshot>())
            if (Relation(binding.Value, binding.Key) is { } relation) yield return relation;
        foreach (var step in version.Graph?.Steps ?? [])
        {
            var schemas = step switch { TransformFlowStepDefinition transform => transform.FunctionSchemaSnapshots, ToolFlowStepDefinition tool => tool.FunctionSchemaSnapshots, _ => null };
            foreach (var binding in schemas ?? new Dictionary<string, Agentstration.Schemas.Contracts.SchemaBindingSnapshot>())
                if (Relation(binding.Value, $"graph.steps[{step.Name}].functionSchemas[{binding.Key}]") is { } relation) yield return relation;
        }
        SystemGraphRelation? Relation(Agentstration.Schemas.Contracts.SchemaBindingSnapshot binding, string field) =>
            binding is { SchemaUid: { } uid, Source: { } schema } ? new(source, SystemGraphIds.Publication(uid, schema.Version), "exact-dependency", field, "recorded-schema")
            { SelectedPublication = schema.Version, TargetField = schema.SchemaPath, ExpectedHash = binding.DefinitionHash, ParentHash = binding.ParentDefinitionHash } : null;
    }

    private static IEnumerable<(string TargetId, string Path)> References(FlowVersion version)
    {
        if (version.Graph is { } graph)
        {
            foreach (var step in graph.Steps)
            {
                var flowCall = step switch { FlowCallStepDefinition call => call.Flow, RepeatFlowStepDefinition repeat => repeat.Flow, _ => null };
                if (flowCall is { VersionStrategy: FlowCallVersionStrategy.Exact, ResourceUid: { } flowUid, Version: { } flowVersion })
                    yield return (SystemGraphIds.Publication(flowUid, flowVersion), $"graph.steps[{step.Name}].flow");
                var function = step switch { TransformFlowStepDefinition transform => transform.Function, ToolFlowStepDefinition tool => tool.FunctionBinding?.Reference, _ => null };
                if (function is { Uid: { } functionUid })
                    yield return (SystemGraphIds.Publication(functionUid, function.Version), $"graph.steps[{step.Name}].function");
                if (step.ArtifactOutput?.StorageFlow is { VersionStrategy: FlowCallVersionStrategy.Exact, ResourceUid: { } storageFlowUid, Version: { } storageVersion })
                    yield return (SystemGraphIds.Publication(storageFlowUid, storageVersion), $"graph.steps[{step.Name}].artifactOutput.storageFlow");
                if (step is AgentFlowStepDefinition agent && agent.Agent.RevisionUid is { } uid)
                    yield return (SystemGraphIds.Definition(uid), $"graph.steps[{step.Name}].agent");
                if (step is RouterFlowStepDefinition router)
                {
                    foreach (var candidate in router.Candidates)
                        if (candidate.Agent.RevisionUid is { } candidateUid)
                            yield return (SystemGraphIds.Definition(candidateUid), $"graph.steps[{step.Name}].candidates[{candidate.Route}].agent");
                    if (router.Fallback?.RevisionUid is { } fallback)
                        yield return (SystemGraphIds.Definition(fallback), $"graph.steps[{step.Name}].fallback");
                }
            }
            yield break;
        }
        var targets = version.Definition switch
        {
            DirectFlowDefinition direct => new[] { direct.Target },
            RoutingFlowDefinition routing => routing.Destinations.Concat(routing.Fallback is { } fallback ? [fallback] : []),
            WorkflowFlowDefinition workflow => workflow.Nodes.Where(node => node.Target is not null).Select(node => node.Target!),
            OrchestrationFlowDefinition orchestration => orchestration.Participants.Concat(orchestration.Pattern is MagenticOrchestrationPattern magentic ? [magentic.Manager] : []),
            _ => []
        };
        var index = 0;
        foreach (var target in targets)
        {
            if (target.Kind == FlowTargetKind.Agent && target.RevisionUid is { } uid)
                yield return (SystemGraphIds.Definition(uid), $"definition.targets[{index}]");
            index++;
        }
    }
}
