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
