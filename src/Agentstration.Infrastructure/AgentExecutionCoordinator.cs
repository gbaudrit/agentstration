using Agentstration.Management.Abstractions;
using Agentstration.Runtime.Abstractions;

namespace Agentstration.Infrastructure;

public sealed record SelectedAgentRoute(AgentRouteResult Route, string DeploymentId);

public sealed class AgentExecutionCoordinator(
    IControlPlaneStore store,
    IAgentResourceQueries agentQueries,
    IAgentRouter router,
    IRuntimeRegistry runtimes)
{
    public async Task<(AgentRouteResult Route, AgentExecutionResult Execution)> RouteAndExecuteAsync(
        string input,
        CancellationToken cancellationToken)
    {
        var selected = await SelectAgentAsync(input, null, cancellationToken);
        return (selected.Route, await ExecuteSelectedAsync(selected, input, cancellationToken));
    }

    public async Task<SelectedAgentRoute> SelectAgentAsync(
        string input,
        string? requestedAgentName,
        CancellationToken cancellationToken) =>
        await SelectAgentAsync(input, requestedAgentName, Agentstration.Resources.ResourceNamespace.Default, cancellationToken);

    public async Task<SelectedAgentRoute> SelectAgentAsync(
        string input,
        string? requestedAgentName,
        Agentstration.Resources.ResourceNamespace @namespace,
        CancellationToken cancellationToken)
    {
        var ready = (await agentQueries.ListDeploymentsAsync(cancellationToken))
            .Where(item => item.Value.AgentNamespace == @namespace && item.Value.DesiredState == DesiredAgentState.Running && item.Value.OperationalState == OperationalState.Ready);
        var pairs = new List<(AgentDeployment Deployment, AgentRevision Revision)>();
        foreach (var item in ready)
        {
            var revision = await store.GetAsync<AgentRevision>(new ResourceKey(ResourceKinds.AgentRevision, item.Value.RevisionName, @namespace), cancellationToken);
            if (revision is not null) pairs.Add((item.Value, revision.Value));
        }

        var newest = pairs
            .GroupBy(item => item.Revision.AgentUid)
            .Select(group => group.OrderByDescending(item => item.Revision.AgentVersion).First())
            .ToArray();
        var candidates = newest
            .Select(item => new RoutableAgent(
                item.Revision.Definition.AgentKey,
                item.Revision.Definition.Description,
                item.Revision.Definition.Capabilities))
            .ToArray();
        if (candidates.Length == 0) throw new InvalidOperationException("No ready and routable agent deployment is available.");

        var route = string.IsNullOrWhiteSpace(requestedAgentName)
            ? await router.SelectAsync(new AgentRouteRequest(input), candidates, cancellationToken)
            : candidates.Any(candidate => candidate.AgentId == requestedAgentName)
                ? new AgentRouteResult(requestedAgentName, 1, "The caller explicitly requested this agent.")
                : throw new InvalidOperationException($"Requested agent '{requestedAgentName}' is not ready or does not exist.");
        var deployment = newest.Single(item => item.Revision.Definition.AgentKey == route.AgentId).Deployment;
        return new SelectedAgentRoute(route, deployment.Uid.ToString("N"));
    }

    public Task<AgentExecutionResult> ExecuteSelectedAsync(
        SelectedAgentRoute selected,
        string input,
        CancellationToken cancellationToken) =>
        runtimes.ExecuteAsync(selected.DeploymentId, new AgentExecutionRequest(input), cancellationToken);
}
