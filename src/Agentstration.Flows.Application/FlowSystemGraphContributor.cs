using System.Runtime.CompilerServices;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Resources;
using Agentstration.SystemGraph.Contracts;

namespace Agentstration.Flows.Application;

public sealed class FlowSystemGraphContributor(IFlowRepository flows) : ISystemGraphContributor
{
    public string Family => "flows";

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
                        var node = new SystemGraphNode(SystemGraphIds.Publication(flow.Uid, version.Version), identity, "publication",
                            new(ETag: publication.ETag, Publication: version.Version, Hash: version.DefinitionHash, OriginKind: version.Governance.Origin.Kind.ToString()));
                        var edges = new List<SystemGraphRelation> { new(definition.Id, node.Id, "publication") };
                        foreach (var (revisionUid, path) in References(version))
                            edges.Add(new(node.Id, SystemGraphIds.Definition(revisionUid), "exact-dependency", path, "recorded-pin"));
                        yield return new([node], edges);
                    }
                    if (!versionPage.HasMore) break;
                }
            }
            if (!page.HasMore) yield break;
        }
    }

    private static IEnumerable<(Guid RevisionUid, string Path)> References(FlowVersion version)
    {
        if (version.Graph is { } graph)
        {
            foreach (var step in graph.Steps)
            {
                if (step is AgentFlowStepDefinition agent && agent.Agent.RevisionUid is { } uid)
                    yield return (uid, $"graph.steps[{step.Name}].agent");
                if (step is RouterFlowStepDefinition router)
                {
                    foreach (var candidate in router.Candidates)
                        if (candidate.Agent.RevisionUid is { } candidateUid)
                            yield return (candidateUid, $"graph.steps[{step.Name}].candidates[{candidate.Route}].agent");
                    if (router.Fallback?.RevisionUid is { } fallback)
                        yield return (fallback, $"graph.steps[{step.Name}].fallback");
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
                yield return (uid, $"definition.targets[{index}]");
            index++;
        }
    }
}
