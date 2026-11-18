using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;

namespace Agentstration.Infrastructure.Flows;

public sealed class FlowDiscoveryPolicy(IResourceAccessPolicyService accessPolicies) : IFlowDiscoveryPolicy
{
    public async Task<bool> CanDiscoverAsync(FlowResource flow, ResourceScopeRef consumerScopeRef,
        CancellationToken cancellationToken) =>
        (await accessPolicies.EvaluateAsync(
            new ResourceAccessIdentity(flow.Uid, flow.ScopeRef, flow.Id.Namespace, FlowResourceKinds.Flow,
                Version: flow.Version),
            consumerScopeRef,
            ResourceAccessAction.Discover,
            cancellationToken)).Allowed;
}

public sealed class FlowUsePolicy(IResourceAccessPolicyService accessPolicies) : IFlowUsePolicy
{
    public async Task ValidateAsync(FlowResource flow, string version, ResourceScopeRef consumer, CancellationToken cancellationToken)
    {
        if (!(await accessPolicies.EvaluateAsync(new(flow.Uid, flow.ScopeRef, flow.Id.Namespace, FlowResourceKinds.Flow), consumer, ResourceAccessAction.Use, cancellationToken)).Allowed)
            throw new FlowValidationException("flow_composition_access_denied", "The exact published Flow does not authorize this consumer.");
    }
}
