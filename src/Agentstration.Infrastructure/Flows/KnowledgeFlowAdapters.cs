using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Identity.Contracts;
using Agentstration.Knowledge;
using Agentstration.Knowledge.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;

namespace Agentstration.Infrastructure.Flows;

public sealed class KnowledgeFlowResolver(FlowService flows) : IKnowledgeFlowResolver
{
    public async Task<ResolvedKnowledgeFlowBinding> ResolveAsync(
        ResourceScopeRef scopeRef,
        ResourceNamespace ownerNamespace,
        KnowledgeFlowTarget target,
        CancellationToken cancellationToken)
    {
        if (scopeRef.Kind != ResourceScopeKind.Workspace || scopeRef.TargetId is not { } workspaceId)
            throw new KnowledgeSourceValidationException("knowledge_source_scope_invalid", "A KnowledgeSource must belong to a Workspace scope.");
        var flowNamespace = target.Namespace ?? ownerNamespace;
        var reference = new FlowReference(new FlowId(target.Name, flowNamespace), target.Version, target.UseActiveVersion, flowNamespace);
        try
        {
            var resolved = await flows.ResolveAsync(new WorkspaceId(workspaceId), reference, ownerNamespace, cancellationToken);
            return new(
                resolved.FlowId.Value,
                resolved.FlowId.Namespace,
                resolved.Version,
                target.UseActiveVersion,
                resolved.Graph?.InputSchema?.Clone(),
                resolved.Graph?.OutputSchema?.Clone());
        }
        catch (Exception exception) when (exception is FlowNotFoundException or FlowValidationException or ArgumentException)
        {
            throw new KnowledgeSourceValidationException("knowledge_source_flow_unavailable",
                $"Flow binding '{flowNamespace}/{target.Name}' is not available as a published version: {exception.Message}");
        }
    }
}

public sealed class KnowledgeFlowDeletionGuard(
    IResourceStore store,
    IRequestContextScopeFactory requestScopes) : IFlowDeletionGuard
{
    public async Task ValidateDeleteAsync(WorkspaceId workspaceId, FlowId flowId, CancellationToken cancellationToken)
    {
        using var requestScope = requestScopes.PushSystem();
        var sources = await store.ListAllAsync<KnowledgeSourceResource>(KnowledgeResourceKinds.KnowledgeSource, cancellationToken);
        var usage = sources.Select(value => value.Value).FirstOrDefault(value =>
            value.ScopeRef is { Kind: ResourceScopeKind.Workspace, TargetId: { } targetId }
            && targetId == workspaceId.Value
            && (References(value, value.Definition.IngestionFlow, flowId)
                || References(value, value.Definition.RetrievalFlow, flowId)));
        if (usage is not null)
            throw new FlowValidationException("flow_in_use_by_knowledge_source",
                $"Flow '{flowId}' is referenced by KnowledgeSource '{usage.Address}'.");
    }

    private static bool References(KnowledgeSourceResource source, KnowledgeFlowTarget? target, FlowId flowId) =>
        target is not null
        && string.Equals(target.Name, flowId.Value, StringComparison.Ordinal)
        && (target.Namespace ?? source.Namespace) == flowId.Namespace;
}
