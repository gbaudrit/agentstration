using System.Globalization;
using Agentstration.Extensions.Contracts;
using Agentstration.Flows;
using Agentstration.Models.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Contracts;
using Agentstration.Triggers;
using Agentstration.Web.Components.Models;
using Agentstration.Web.Components.State;
using Agentstration.Work.Contracts;

namespace Agentstration.Web.Console;

public sealed class PlatformDashboardService(
    IManagementApiClient management,
    IRuntimeApiClient runtime,
    IWorkApiClient work,
    IFlowApiClient flow,
    IModelProvidersClient modelProviders,
    IExtensionsClient extensions,
    ILogger<PlatformDashboardService> logger) : IPlatformStatusProvider, IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly object loadLock = new();
    private PlatformDashboardLoad? sharedLoad;
    private int disposed;

    public PlatformDashboardLoad StartShared()
    {
        lock (loadLock)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            return sharedLoad ??= Start(lifetime.Token);
        }
    }

    public PlatformDashboardLoad Start(CancellationToken cancellationToken)
    {
        var agentsTask = LoadAsync("Agents", management.GetAgentsAsync, Array.Empty<AgentSummary>(), cancellationToken);
        var instancesTask = LoadAsync("Agent instances", runtime.GetAgentInstancesAsync, Array.Empty<AgentInstanceResponse>(), cancellationToken);
        var runtimeRunsTask = LoadAsync("Agents Run", token => runtime.GetRunsAsync(null, token), Array.Empty<RuntimeRun>(), cancellationToken);
        var workTask = LoadAsync("Work Tasks", token => work.GetTaskSummaryAsync(null, token), new WorkTaskOperationsCountersResponse(0, 0, 0, 0, 0), cancellationToken);
        var flowsTask = LoadAsync("Flows", flow.GetFlowsAsync, Array.Empty<FlowSummary>(), cancellationToken);
        var flowRunsTask = LoadAsync("Flow Runs", token => flow.GetFlowRunsAsync(null, token), Array.Empty<FlowRun>(), cancellationToken);
        var triggersTask = LoadAsync("Triggers", management.GetTriggersAsync, Array.Empty<TriggerResource>(), cancellationToken);
        var providersTask = LoadAsync("Model providers", modelProviders.GetModelProvidersAsync, Array.Empty<ModelProviderResponse>(), cancellationToken);
        var extensionsTask = LoadAsync("Extensions", extensions.GetExtensionsAsync, Array.Empty<ExtensionResponse>(), cancellationToken);
        var snapshotTask = BuildSnapshotAsync(agentsTask, instancesTask, runtimeRunsTask, workTask, flowsTask, flowRunsTask, triggersTask, providersTask, extensionsTask);

        return new(
            ToMetricAsync(agentsTask, agents => new(FormatCount(agents.Count), null, UiStatus.Info)),
            ToMetricAsync(flowsTask, flows => new(FormatCount(flows.Count), null, UiStatus.Info)),
            ToMetricAsync(extensionsTask, configured => new(FormatCount(configured.Count), null, UiStatus.Info)),
            ToMetricAsync(instancesTask, instances => new(FormatCount(instances.Count), null, UiStatus.Info)),
            ToMetricAsync(runtimeRunsTask, runs => new(
                FormatCount(runs.Count(run => run.Status.State == RuntimeRunState.Running)), null, UiStatus.Info)),
            ToMetricAsync(workTask, tasks => new(FormatCount(tasks.Running), null, UiStatus.Info)),
            ToMetricAsync(snapshotTask, snapshot => new(FormatCount(snapshot.AttentionCount), null,
                snapshot.AttentionCount == 0 ? UiStatus.Success : UiStatus.Warning)),
            ToMetricAsync(flowRunsTask, runs =>
            {
                var running = runs.Count(run => run.Status == FlowRunStatus.Running);
                var waiting = runs.Count(run => run.Status == FlowRunStatus.WaitingForInput);
                return new(FormatCount(running), null,
                    waiting > 0 ? UiStatus.Warning : UiStatus.Info,
                    "Metric.AwaitingInput",
                    [waiting]);
            }),
            ToMetricAsync(triggersTask, triggers =>
            {
                var failed = triggers.Count(trigger => trigger.Observed.LastOutcome == TriggerLastOutcome.Failed);
                return new(FormatCount(triggers.Count(trigger => trigger.Definition.Enabled)), null,
                    failed == 0 ? UiStatus.Success : UiStatus.Danger,
                    "Metric.FailedLastFiring",
                    [failed]);
            }),
            ToMetricAsync(providersTask, providers =>
            {
                var unavailable = providers.Count(provider => ModelManagementUi.Status(provider.Properties.Status) is UiStatus.Warning or UiStatus.Danger);
                return new(FormatCount(providers.Count(provider => ModelManagementUi.Status(provider.Properties.Status) == UiStatus.Success)),
                    null, unavailable == 0 ? UiStatus.Success : UiStatus.Warning,
                    "Metric.UnavailableCount",
                    [unavailable]);
            }),
            snapshotTask);
    }

    public Task<PlatformSnapshot> GetAsync(CancellationToken cancellationToken) =>
        StartShared().Snapshot.WaitAsync(cancellationToken);

    public async Task<PlatformStatusResult> GetStatusAsync(CancellationToken cancellationToken)
    {
        var snapshot = await GetAsync(cancellationToken);
        return new(ToStatus(snapshot.Status), snapshot.Status switch
        {
            "Operational" => PlatformStatusKind.Operational,
            "Attention required" => PlatformStatusKind.AttentionRequired,
            "Partially unavailable" => PlatformStatusKind.PartiallyUnavailable,
            "No active deployments" => PlatformStatusKind.NoActiveDeployments,
            _ => PlatformStatusKind.Unavailable
        });
    }

    private static async Task<DashboardMetric> ToMetricAsync<T>(Task<SourceLoad<T>> sourceTask, Func<T, DashboardMetric> project)
    {
        var source = await sourceTask;
        return source.Available ? project(source.Value) : new("—", null, UiStatus.Danger, "Unavailable");
    }

    private static async Task<DashboardMetric> ToMetricAsync(Task<PlatformSnapshot> snapshotTask, Func<PlatformSnapshot, DashboardMetric> project) =>
        project(await snapshotTask);

    private static string FormatCount(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static async Task<PlatformSnapshot> BuildSnapshotAsync(
        Task<SourceLoad<IReadOnlyList<AgentSummary>>> agentsTask,
        Task<SourceLoad<IReadOnlyList<AgentInstanceResponse>>> instancesTask,
        Task<SourceLoad<IReadOnlyList<RuntimeRun>>> runtimeRunsTask,
        Task<SourceLoad<WorkTaskOperationsCountersResponse>> workTask,
        Task<SourceLoad<IReadOnlyList<FlowSummary>>> flowsTask,
        Task<SourceLoad<IReadOnlyList<FlowRun>>> flowRunsTask,
        Task<SourceLoad<IReadOnlyList<TriggerResource>>> triggersTask,
        Task<SourceLoad<IReadOnlyList<ModelProviderResponse>>> providersTask,
        Task<SourceLoad<IReadOnlyList<ExtensionResponse>>> extensionsTask)
    {

        await Task.WhenAll(agentsTask, instancesTask, runtimeRunsTask, workTask, flowsTask, flowRunsTask, triggersTask, providersTask, extensionsTask);

        var agents = await agentsTask;
        var instances = await instancesTask;
        var runtimeRuns = await runtimeRunsTask;
        var tasks = await workTask;
        var flows = await flowsTask;
        var flowRuns = await flowRunsTask;
        var triggers = await triggersTask;
        var providers = await providersTask;
        var extensions = await extensionsTask;

        var failedTriggerItems = triggers.Value.Where(trigger => trigger.Observed.LastOutcome == TriggerLastOutcome.Failed).ToArray();
        var failedTriggers = failedTriggerItems.Length;
        var runningFlowRuns = flowRuns.Value.Count(run => run.Status == FlowRunStatus.Running);
        var waitingFlowRuns = flowRuns.Value.Where(run => run.Status == FlowRunStatus.WaitingForInput).ToArray();
        var unavailableProviders = providers.Value.Where(provider => ModelManagementUi.Status(provider.Properties.Status) is UiStatus.Warning or UiStatus.Danger).ToArray();

        var attention = new List<ComponentHealth>();
        if (tasks.Value.ActionRequired > 0)
            attention.Add(new("tasks-action-required", "Action required", $"{tasks.Value.ActionRequired} awaiting input", UiStatus.Warning, "/tasks?hasPendingAction=true"));
        if (tasks.Value.Failed > 0)
            attention.Add(new("tasks-failed", "Failed", $"{tasks.Value.Failed} failed tasks", UiStatus.Danger, "/tasks?status=Failed"));
        attention.AddRange(failedTriggerItems.Select(trigger => new ComponentHealth(
            trigger.Definition.DisplayName,
            "Failed",
            $"{trigger.Namespace.Value}/{trigger.Name}",
            UiStatus.Danger,
            $"/triggers/{Uri.EscapeDataString(trigger.Name)}")));
        attention.AddRange(waitingFlowRuns.Select(ToFlowRunAttention));
        attention.AddRange(unavailableProviders.Select(provider => new ComponentHealth(
            provider.Properties.DisplayName,
            ModelManagementUi.Label(provider.Properties.Status),
            provider.Properties.LastCheckedAt is { } checkedAt ? $"Last checked {checkedAt.LocalDateTime:g}" : "Status unavailable",
            ModelManagementUi.Status(provider.Properties.Status),
            $"/modelproviders/{Uri.EscapeDataString(provider.Name)}?namespace={Uri.EscapeDataString(provider.Namespace)}")));

        var sources = new[]
        {
            agents.ToSource($"{agents.Value.Count} agents", "/agents"),
            instances.ToSource($"{instances.Value.Count} active Agent instances", "/agent-instances"),
            runtimeRuns.ToSource($"{runtimeRuns.Value.Count} runs", "/agent-runs"),
            tasks.ToSource("Available", "/tasks"),
            flows.ToSource($"{flows.Value.Count} flows", "/flows"),
            flowRuns.ToSource($"{flowRuns.Value.Count} runs", "/flow-runs"),
            triggers.ToSource($"{triggers.Value.Count} triggers", "/triggers"),
            providers.ToSource($"{providers.Value.Count} providers", "/modelproviders"),
            extensions.ToSource($"{extensions.Value.Count} extensions", "/extensions")
        };
        attention.AddRange(sources.Where(source => source.Severity == UiStatus.Danger));

        var unavailableSources = sources.Count(source => source.Severity == UiStatus.Danger);
        var attentionCount = tasks.Value.ActionRequired + tasks.Value.Failed + waitingFlowRuns.Length + failedTriggers + unavailableProviders.Length + unavailableSources;
        var status = unavailableSources > 0
            ? "Partially unavailable"
                : attentionCount > 0
                ? "Attention required"
                : "Operational";

        return new PlatformSnapshot
        {
            Status = status,
            DefinedAgents = agents.Value.Count,
            DefinedFlows = flows.Value.Count,
            ConfiguredExtensions = extensions.Value.Count,
            ReadyDeployments = instances.Value.Count,
            DesiredDeployments = instances.Value.Count,
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
        "operational" or "healthy" or "ready" or "active" or "online" or "running" or "completed" or "succeeded" => UiStatus.Success,
        "attention required" or "degraded" or "stale" or "waiting" or "waitingforinput" or "waitingforchild" or "needsinput" or "actionrequired" or "paused" or "queued" or "draft" => UiStatus.Warning,
        "partially unavailable" or "offline" or "failed" or "error" or "unavailable" or "cancelled" or "canceled" => UiStatus.Danger,
        "no active deployments" => UiStatus.Success,
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
        return new(
            deployment.Id,
            deployment.Status,
            detail,
            severity,
            ConsoleResourceUrls.Deployment(ResourceNamespace.Parse(deployment.Namespace), deployment.Id));
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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        lifetime.Cancel();
        lifetime.Dispose();
    }
}
