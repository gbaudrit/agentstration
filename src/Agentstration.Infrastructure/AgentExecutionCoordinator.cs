using Agentstration.Agents;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Core;

namespace Agentstration.Infrastructure;

public sealed record SelectedAgentRoute(
    AgentRouteResult Route,
    ResourceNamespace Namespace,
    long Generation);

public sealed class AgentExecutionCoordinator(
    AgentManagementService agents,
    IAgentRouter router,
    RuntimeRunService runtimeRuns) : IAgentExecutionCoordinator
{
    public async Task<RoutedAgentExecution> RouteAndExecuteAsync(
        RuntimeRunScope scope,
        string input,
        string initiator,
        CancellationToken cancellationToken)
    {
        var selected = await SelectAgentAsync(input, null, cancellationToken);
        return await ExecuteSelectedAsync(scope, selected, input, initiator, RuntimeRunOrigin.Api, cancellationToken);
    }

    public async Task<SelectedAgentRoute> SelectAgentAsync(
        string input,
        string? requestedAgentName,
        CancellationToken cancellationToken) =>
        await SelectAgentAsync(input, requestedAgentName, ResourceNamespace.Default, cancellationToken);

    public async Task<SelectedAgentRoute> SelectAgentAsync(
        string input,
        string? requestedAgentName,
        ResourceNamespace @namespace,
        CancellationToken cancellationToken)
    {
        const int pageSize = 200;
        var available = new List<(AgentResource Agent, AgentRevision Revision)>();
        for (var skip = 0; ; skip += pageSize)
        {
            var page = await agents.ListAgentsAsync(@namespace, skip, pageSize, cancellationToken);
            foreach (var item in page)
            {
                var revision = await agents.EnsureExecutionRevisionAsync(
                    @namespace,
                    item.Value.Metadata.Name,
                    item.Value.Generation,
                    cancellationToken);
                available.Add((item.Value, revision.Value));
            }
            if (page.Count < pageSize) break;
        }

        var candidates = available
            .Select(item => new RoutableAgent(
                item.Agent.Metadata.Name,
                item.Revision.Definition.Description,
                item.Revision.Definition.Capabilities))
            .ToArray();
        if (candidates.Length == 0) throw new InvalidOperationException("No routable Agent is available.");

        var route = string.IsNullOrWhiteSpace(requestedAgentName)
            ? await router.SelectAsync(new AgentRouteRequest(input), candidates, cancellationToken)
            : candidates.Any(candidate => candidate.AgentId == requestedAgentName)
                ? new AgentRouteResult(requestedAgentName, 1, "The caller explicitly requested this Agent.")
                : throw new InvalidOperationException($"Requested Agent '{requestedAgentName}' does not exist.");
        var selected = available.Single(item => item.Agent.Metadata.Name == route.AgentId).Agent;
        return new SelectedAgentRoute(route, @namespace, selected.Generation);
    }

    public async Task<RoutedAgentExecution> ExecuteSelectedAsync(
        RuntimeRunScope scope,
        SelectedAgentRoute selected,
        string input,
        string initiator,
        RuntimeRunOrigin origin,
        CancellationToken cancellationToken)
    {
        var created = await runtimeRuns.CreateAsync(
            scope,
            new RuntimeAgentReference(selected.Route.AgentId, selected.Generation) { Namespace = selected.Namespace },
            new RuntimeRunInput { Messages = [new RuntimeRunMessage(RuntimeMessageRole.User, input)] },
            new RuntimeExecutionOptions(),
            origin,
            initiator,
            cancellationToken);

        await foreach (var _ in runtimeRuns.ObserveAsync(scope.WorkspaceId, created.Value.Id, 0, cancellationToken)) { }
        var completed = await runtimeRuns.GetAsync(scope.WorkspaceId, created.Value.Id, cancellationToken)
            ?? throw new RuntimeRunNotFoundException(created.Value.Id);
        if (completed.Value.Status.State != RuntimeRunState.Succeeded)
            throw new InvalidOperationException(completed.Value.Status.Error ?? $"Runtime Run ended as {completed.Value.Status.State}.");

        var status = completed.Value.Status;
        return new RoutedAgentExecution(
            selected.Route,
            new AgentExecutionResult(
                status.Response ?? string.Empty,
                completed.Value.Id,
                status.ModelProvider,
                status.ResolvedModel,
                new ModelExecutionOptions(status.EffectiveTemperature, status.EffectiveMaxOutputTokens),
                new AgentExecutionUsage(status.InputTokens, status.OutputTokens)),
            completed.Value.Id);
    }
}
