using Agentstration.Web.Components.Models;

namespace Agentstration.Web.Console;

public sealed class PlatformDashboardService(IManagementApiClient management, IRuntimeApiClient runtime, IWorkApiClient work, IFlowApiClient flow)
{
    public async Task<PlatformSnapshot> GetAsync(CancellationToken cancellationToken)
    {
        var agentsTask = management.GetAgentsAsync(cancellationToken);
        var runtimeTask = runtime.GetInstancesAsync(cancellationToken);
        var workTask = LoadWorkAsync(cancellationToken);
        var flowTask = flow.GetFlowsAsync(cancellationToken);
        var executionsTask = runtime.GetExecutionsAsync(cancellationToken);
        await Task.WhenAll(agentsTask, runtimeTask, workTask, flowTask, executionsTask);
        var agents = await agentsTask;
        var instances = await runtimeTask;
        var workResult = await workTask; var workItems = workResult.Items;
        var flows = await flowTask;
        var executions = await executionsTask;
        var components = instances.Select(instance => new ComponentHealth(instance.Id, instance.Status, instance.Error ?? instance.Activity, ToStatus(instance.Status)))
            .Append(new ComponentHealth("work-api", workResult.Available ? "Ready" : "Unavailable", workResult.Available ? "Operational Tasks are available" : "Task supervision is temporarily unavailable", workResult.Available ? UiStatus.Success : UiStatus.Danger)).ToArray();
        var degraded = components.Any(component => component.Severity is UiStatus.Warning or UiStatus.Danger);
        return new(degraded ? "Degraded" : "Healthy", agents.Count, agents.Count(agent => agent.Status == "Ready"), executions.Count(run => run.Status == "Running"), workItems.Count(item => item.Status is not "Completed" and not "Canceled" and not "Failed"), flows.Count(item => item.Status == "Active"), components);
    }

    private async Task<(IReadOnlyList<WorkSummary> Items, bool Available)> LoadWorkAsync(CancellationToken cancellationToken)
    {
        try { return (await work.GetWorkItemsAsync(cancellationToken), true); }
        catch (Exception exception) when (exception is AgentstrationApiException or HttpRequestException) { return ([], false); }
    }

    public static UiStatus ToStatus(string status) => status.ToLowerInvariant() switch
    {
        "healthy" or "ready" or "active" or "running" or "completed" => UiStatus.Success,
        "degraded" or "waiting" or "needsinput" or "actionrequired" or "paused" or "queued" or "draft" => UiStatus.Warning,
        "failed" or "error" or "unavailable" or "cancelled" or "canceled" => UiStatus.Danger,
        _ => UiStatus.Neutral
    };
}
