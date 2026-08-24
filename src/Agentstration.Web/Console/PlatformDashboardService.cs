using System.Globalization;
using Agentstration.Flow;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Runtime.Abstractions;
using Agentstration.Web.Components.Models;
using Agentstration.Work.Contracts;

namespace Agentstration.Web.Console;

public sealed class PlatformDashboardService(
    IManagementApiClient management,
    IRuntimeApiClient runtime,
    IWorkApiClient work,
    IFlowApiClient flow,
    IModelProvidersClient modelProviders,
    ILogger<PlatformDashboardService> logger)
{
    public PlatformDashboardLoad Start(CancellationToken cancellationToken)
    {
        var agentsTask = LoadAsync("Agents", management.GetAgentsAsync, Array.Empty<AgentSummary>(), cancellationToken);
        var deploymentsTask = LoadAsync("Deployments", management.GetDeploymentsAsync, Array.Empty<DeploymentSummary>(), cancellationToken);
        var runtimeRunsTask = LoadAsync("Agents Run", token => runtime.GetRunsAsync(null, token), Array.Empty<RuntimeRun>(), cancellationToken);
        var workTask = LoadAsync("Work Tasks", token => work.GetTaskSummaryAsync(null, token), new WorkTaskOperationsCountersResponse(0, 0, 0, 0, 0), cancellationToken);
        var flowsTask = LoadAsync("Flows", flow.GetFlowsAsync, Array.Empty<FlowSummary>(), cancellationToken);
        var flowRunsTask = LoadAsync("Flow Runs", token => flow.GetFlowRunsAsync(null, token), Array.Empty<FlowRun>(), cancellationToken);
        var triggersTask = LoadAsync("Triggers", management.GetTriggersAsync, Array.Empty<TriggerResource>(), cancellationToken);
        var providersTask = LoadAsync("Model providers", modelProviders.GetModelProvidersAsync, Array.Empty<ModelProviderResponse>(), cancellationToken);
        var snapshotTask = BuildSnapshotAsync(agentsTask, deploymentsTask, runtimeRunsTask, workTask, flowsTask, flowRunsTask, triggersTask, providersTask);

        return new(
            ToMetricAsync(agentsTask, agents => new(FormatCount(agents.Count), null, UiStatus.Info)),
            ToMetricAsync(deploymentsTask, deployments =>
            {
                var desired = deployments.Where(IsDesiredRunning).ToArray();
                var ready = desired.Count(IsReady);
                return new($"{FormatCount(ready)}/{FormatCount(desired.Length)}", "Ready / desired",
                    ready == desired.Length && desired.Length > 0 ? UiStatus.Success : UiStatus.Warning);
            }),
            ToMetricAsync(runtimeRunsTask, runs => new(
                FormatCount(runs.Count(run => run.Status.State == RuntimeRunState.Running)), null, UiStatus.Info)),
            ToMetricAsync(workTask, tasks => new(FormatCount(tasks.Running), null, UiStatus.Info)),
            ToMetricAsync(snapshotTask, snapshot => new(FormatCount(snapshot.AttentionCount), null,
                snapshot.AttentionCount == 0 ? UiStatus.Success : UiStatus.Warning)),
            ToMetricAsync(flowRunsTask, runs =>
            {
                var running = runs.Count(run => run.Status == FlowRunStatus.Running);
                var waiting = runs.Count(run => run.Status == FlowRunStatus.WaitingForInput);
                return new(FormatCount(running), $"{FormatCount(waiting)} awaiting input",
                    waiting > 0 ? UiStatus.Warning : UiStatus.Info);
            }),
            ToMetricAsync(triggersTask, triggers =>
            {
                var failed = triggers.Count(trigger => trigger.Observed.LastOutcome == TriggerLastOutcome.Failed);
                return new(FormatCount(triggers.Count(trigger => trigger.Definition.Enabled)), $"{FormatCount(failed)} failed last firing",
                    failed == 0 ? UiStatus.Success : UiStatus.Danger);
            }),
            ToMetricAsync(providersTask, providers =>
            {
                var unavailable = providers.Count(provider => ModelManagementUi.Status(provider.Properties.Status) is UiStatus.Warning or UiStatus.Danger);
                return new(FormatCount(providers.Count(provider => ModelManagementUi.Status(provider.Properties.Status) == UiStatus.Success)),
                    $"{FormatCount(unavailable)} unavailable", unavailable == 0 ? UiStatus.Success : UiStatus.Warning);
            }),
            snapshotTask);
    }

    public Task<PlatformSnapshot> GetAsync(CancellationToken cancellationToken) => Start(cancellationToken).Snapshot;

    private static async Task<DashboardMetric> ToMetricAsync<T>(Task<SourceLoad<T>> sourceTask, Func<T, DashboardMetric> project)
    {
        var source = await sourceTask;
        return source.Available ? project(source.Value) : new("—", "Unavailable", UiStatus.Danger);
    }

    private static async Task<DashboardMetric> ToMetricAsync(Task<PlatformSnapshot> snapshotTask, Func<PlatformSnapshot, DashboardMetric> project) =>
        project(await snapshotTask);

    private static string FormatCount(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static async Task<PlatformSnapshot> BuildSnapshotAsync(
        Task<SourceLoad<IReadOnlyList<AgentSummary>>> agentsTask,
        Task<SourceLoad<IReadOnlyList<DeploymentSummary>>> deploymentsTask,
        Task<SourceLoad<IReadOnlyList<RuntimeRun>>> runtimeRunsTask,
        Task<SourceLoad<WorkTaskOperationsCountersResponse>> workTask,
        Task<SourceLoad<IReadOnlyList<FlowSummary>>> flowsTask,
        Task<SourceLoad<IReadOnlyList<FlowRun>>> flowRunsTask,
        Task<SourceLoad<IReadOnlyList<TriggerResource>>> triggersTask,
        Task<SourceLoad<IReadOnlyList<ModelProviderResponse>>> providersTask)
    {

        await Task.WhenAll(agentsTask, deploymentsTask, runtimeRunsTask, workTask, flowsTask, flowRunsTask, triggersTask, providersTask);

        var agents = await agentsTask;
        var deployments = await deploymentsTask;
        var runtimeRuns = await runtimeRunsTask;
        var tasks = await workTask;
        var flows = await flowsTask;
        var flowRuns = await flowRunsTask;
        var triggers = await triggersTask;
        var providers = await providersTask;

        var desiredDeployments = deployments.Value.Where(IsDesiredRunning).ToArray();
        var readyDeployments = desiredDeployments.Count(IsReady);
        var deploymentAttention = desiredDeployments.Where(NeedsAttention).Select(ToAttentionItem).ToArray();
        var failedTriggers = triggers.Value.Count(trigger => trigger.Observed.LastOutcome == TriggerLastOutcome.Failed);
        var runningFlowRuns = flowRuns.Value.Count(run => run.Status == FlowRunStatus.Running);
        var waitingFlowRuns = flowRuns.Value.Where(run => run.Status == FlowRunStatus.WaitingForInput).ToArray();
        var unavailableProviders = providers.Value.Where(provider => ModelManagementUi.Status(provider.Properties.Status) is UiStatus.Warning or UiStatus.Danger).ToArray();

        var attention = new List<ComponentHealth>();
        attention.AddRange(deploymentAttention);
        if (tasks.Value.ActionRequired > 0)
            attention.Add(new("tasks-action-required", "Action required", $"{tasks.Value.ActionRequired} awaiting input", UiStatus.Warning));
        if (tasks.Value.Failed > 0)
            attention.Add(new("tasks-failed", "Failed", $"{tasks.Value.Failed} failed tasks", UiStatus.Danger));
        if (failedTriggers > 0)
            attention.Add(new("triggers-failed", "Failed", $"{failedTriggers} failed triggers", UiStatus.Danger));
        attention.AddRange(waitingFlowRuns.Select(ToFlowRunAttention));
        attention.AddRange(unavailableProviders.Select(provider => new ComponentHealth(
            $"provider-{provider.Name}",
            ModelManagementUi.Label(provider.Properties.Status),
            provider.Properties.LastCheckedAt is { } checkedAt ? $"Last checked {checkedAt.LocalDateTime:g}" : "Status unavailable",
            ModelManagementUi.Status(provider.Properties.Status))));

        var sources = new[]
        {
            agents.ToSource($"{agents.Value.Count} agents", "/agents"),
            deployments.ToSource($"{deployments.Value.Count} deployments", "/deployments"),
            runtimeRuns.ToSource($"{runtimeRuns.Value.Count} runs", "/agent-runs"),
            tasks.ToSource("Available", "/tasks"),
            flows.ToSource($"{flows.Value.Count} flows", "/flows"),
            flowRuns.ToSource($"{flowRuns.Value.Count} runs", "/flow-runs"),
            triggers.ToSource($"{triggers.Value.Count} triggers", "/triggers"),
            providers.ToSource($"{providers.Value.Count} providers", "/modelproviders")
        };
        attention.AddRange(sources.Where(source => source.Severity == UiStatus.Danger));

        var unavailableSources = sources.Count(source => source.Severity == UiStatus.Danger);
        var attentionCount = deploymentAttention.Length + tasks.Value.ActionRequired + tasks.Value.Failed + waitingFlowRuns.Length + failedTriggers + unavailableProviders.Length + unavailableSources;
        var status = unavailableSources > 0
            ? "Partially unavailable"
            : attentionCount > 0
                ? "Attention required"
                : desiredDeployments.Length == 0
                    ? "No active deployments"
                    : "Operational";

        return new PlatformSnapshot
        {
            Status = status,
            DefinedAgents = agents.Value.Count,
            ReadyDeployments = readyDeployments,
            DesiredDeployments = desiredDeployments.Length,
            RunningRuntimeRuns = runtimeRuns.Value.Count(run => run.Status.State == RuntimeRunState.Running),
            RunningTasks = tasks.Value.Running,
            ActionRequiredTasks = tasks.Value.ActionRequired,
            FailedTasks = tasks.Value.Failed,
            CompletedTasksLast24Hours = tasks.Value.CompletedRecently,
            EnabledFlows = flows.Value.Count(item => item.Status == "Active"),
            RunningFlowRuns = runningFlowRuns,
            WaitingForInputFlowRuns = waitingFlowRuns.Length,
            EnabledTriggers = triggers.Value.Count(trigger => trigger.Definition.Enabled),
            FailedTriggers = failedTriggers,
            ReadyModelProviders = providers.Value.Count(provider => ModelManagementUi.Status(provider.Properties.Status) == UiStatus.Success),
            UnavailableModelProviders = unavailableProviders.Length,
            AttentionCount = attentionCount,
            AttentionItems = attention,
            Sources = sources
        };
    }

    public static UiStatus ToStatus(string status) => status.ToLowerInvariant() switch
    {
        "operational" or "healthy" or "ready" or "active" or "running" or "completed" => UiStatus.Success,
        "attention required" or "degraded" or "waiting" or "needsinput" or "actionrequired" or "paused" or "queued" or "draft" => UiStatus.Warning,
        "partially unavailable" or "failed" or "error" or "unavailable" or "cancelled" or "canceled" => UiStatus.Danger,
        "no active deployments" => UiStatus.Info,
        _ => UiStatus.Neutral
    };

    private async Task<SourceLoad<T>> LoadAsync<T>(
        string name,
        Func<CancellationToken, Task<T>> load,
        T fallback,
        CancellationToken cancellationToken)
    {
        try
        {
            return new(name, await load(cancellationToken), true, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Dashboard source {DashboardSource} is unavailable", name);
            var detail = exception is AgentstrationApiException apiException
                ? $"Source unavailable · error {apiException.ErrorId}"
                : "Source temporarily unavailable";
            return new(name, fallback, false, detail);
        }
    }

    private static bool IsDesiredRunning(DeploymentSummary deployment) =>
        string.Equals(deployment.DesiredState, "Running", StringComparison.OrdinalIgnoreCase);

    private static bool IsReady(DeploymentSummary deployment) =>
        string.Equals(deployment.Status, "Ready", StringComparison.OrdinalIgnoreCase);

    private static bool NeedsAttention(DeploymentSummary deployment) =>
        !IsReady(deployment)
        || !string.IsNullOrWhiteSpace(deployment.Error)
        || deployment.ObservedRevision is not null && !string.Equals(deployment.ObservedRevision, deployment.Revision, StringComparison.Ordinal);

    private static ComponentHealth ToAttentionItem(DeploymentSummary deployment)
    {
        var severity = !string.IsNullOrWhiteSpace(deployment.Error) || ToStatus(deployment.Status) == UiStatus.Danger
            ? UiStatus.Danger
            : UiStatus.Warning;
        var detail = deployment.Error
            ?? (deployment.ObservedRevision is not null && !string.Equals(deployment.ObservedRevision, deployment.Revision, StringComparison.Ordinal)
                ? $"Observed {deployment.ObservedRevision}; desired {deployment.Revision}"
                : $"Desired Running; observed {deployment.Status}");
        return new(deployment.Id, deployment.Status, detail, severity);
    }

    private static ComponentHealth ToFlowRunAttention(FlowRun run) => new(
        run.FlowId.Value,
        "Input required",
        $"Run {run.Id[..Math.Min(12, run.Id.Length)]} awaiting input",
        UiStatus.Warning,
        $"/flow-runs/{Uri.EscapeDataString(run.Id)}");

    private sealed record SourceLoad<T>(string Name, T Value, bool Available, string? Error)
    {
        public ComponentHealth ToSource(string availableDetail, string url) => new(
            Name,
            Available ? "Available" : "Unavailable",
            Available ? availableDetail : Error ?? "Source temporarily unavailable",
            Available ? UiStatus.Success : UiStatus.Danger,
            url);
    }
}
