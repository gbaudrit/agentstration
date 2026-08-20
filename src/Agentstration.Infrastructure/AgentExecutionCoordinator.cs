using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Core;

namespace Agentstration.Infrastructure;

public sealed record SelectedAgentRoute(AgentRouteResult Route, string DeploymentId, ExecutableAgentDefinition Definition);

public sealed class AgentExecutionCoordinator(
    IControlPlaneStore store,
    IAgentResourceQueries agentQueries,
    IAgentRouter router,
    IRuntimeRegistry runtimes,
    IAgentExecutionContextAssembler? contextAssembler = null,
    ICurrentRequestContext? requestContext = null)
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
        var selected = newest.Single(item => item.Revision.Definition.AgentKey == route.AgentId);
        return new SelectedAgentRoute(route, selected.Deployment.Uid.ToString("N"), RuntimeAgentDefinitionMapper.ToExecutable(selected.Revision.Definition));
    }

    public async Task<AgentExecutionResult> ExecuteSelectedAsync(
        SelectedAgentRoute selected,
        string input,
        CancellationToken cancellationToken)
    {
        var request = new AgentExecutionRequest(input);
        if (selected.Definition.Memory is not null)
        {
            if (contextAssembler is null || requestContext?.IsInitialized != true)
                throw new InvalidOperationException("Memory-enabled Agent execution requires an initialized execution scope and context assembler.");
            var current = requestContext.Current;
            request = (await contextAssembler.AssembleAsync(new AgentExecutionContextRequest(
                new RuntimeRunScope(current.TenantId, new Agentstration.Resources.WorkspaceId(current.WorkspaceId), current.PrincipalId),
                selected.Definition, [new RuntimeRunMessage(RuntimeMessageRole.User, input)], null, Guid.NewGuid().ToString("N")), cancellationToken)).Request;
        }
        return await runtimes.ExecuteAsync(selected.DeploymentId, request, cancellationToken);
    }
}
