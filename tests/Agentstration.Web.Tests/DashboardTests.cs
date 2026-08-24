using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Contracts;
using Agentstration.Web.Components.Models;
using Agentstration.Web.Console;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class DashboardTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task DashboardAggregatesPersistedOperationalState()
    {
        var fake = new MockApiClient(new FixedTimeProvider(Now));
        var management = new StubManagementClient(
            [
                new("agent-a", "Agent A", "Agent", "1", "Succeeded", [], "local", Now),
                new("agent-b", "Agent B", "Agent", "1", "Succeeded", [], "local", Now)
            ],
            [
                Deployment("deployment-ready", "Ready", "Running"),
                Deployment("deployment-failed", "Failed", "Running", "Runtime unavailable"),
                Deployment("deployment-stopped", "Stopped", "Stopped")
            ],
            [Trigger("failed-trigger", enabled: true, TriggerLastOutcome.Failed)]);
        var providers = new StubModelProvidersClient([
            Provider("ready-provider", "ready"),
            Provider("unavailable-provider", "providerUnavailable")
        ]);
        var service = new PlatformDashboardService(
            management,
            fake,
            new StubWorkClient(new(2, 3, 1, 4, 5)),
            fake,
            providers,
            NullLogger<PlatformDashboardService>.Instance);

        var snapshot = await service.GetAsync(CancellationToken.None);

        Assert.AreEqual(2, snapshot.DefinedAgents);
        Assert.AreEqual(1, snapshot.ReadyDeployments);
        Assert.AreEqual(2, snapshot.DesiredDeployments);
        Assert.AreEqual(2, snapshot.RunningTasks);
        Assert.AreEqual(3, snapshot.ActionRequiredTasks);
        Assert.AreEqual(4, snapshot.FailedTasks);
        Assert.AreEqual(5, snapshot.CompletedTasksLast24Hours);
        Assert.AreEqual(1, snapshot.EnabledTriggers);
        Assert.AreEqual(1, snapshot.FailedTriggers);
        Assert.AreEqual(1, snapshot.ReadyModelProviders);
        Assert.AreEqual(1, snapshot.UnavailableModelProviders);
        Assert.AreEqual(10, snapshot.AttentionCount);
        Assert.AreEqual("Attention required", snapshot.Status);
        Assert.IsTrue(snapshot.Sources.All(source => source.Severity == UiStatus.Success));
    }

    [TestMethod]
    public async Task DashboardKeepsAvailableCountersWhenRuntimeIsUnavailable()
    {
        var fake = new MockApiClient(new FixedTimeProvider(Now));
        var service = new PlatformDashboardService(
            new StubManagementClient([], [], []),
            new FailingRuntimeClient(),
            new StubWorkClient(new(2, 0, 0, 0, 0)),
            fake,
            new StubModelProvidersClient([]),
            NullLogger<PlatformDashboardService>.Instance);

        var snapshot = await service.GetAsync(CancellationToken.None);

        Assert.AreEqual("Partially unavailable", snapshot.Status);
        Assert.AreEqual(2, snapshot.RunningTasks);
        Assert.AreEqual(1, snapshot.AttentionCount);
        var source = snapshot.Sources.Single(item => item.Name == "Runtime Runs");
        Assert.AreEqual(UiStatus.Danger, source.Severity);
    }

    [TestMethod]
    public async Task DashboardDoesNotClaimHealthWithoutActiveDeployments()
    {
        var fake = new MockApiClient(new FixedTimeProvider(Now));
        var service = new PlatformDashboardService(
            new StubManagementClient([], [], []),
            fake,
            new StubWorkClient(new(0, 0, 0, 0, 0)),
            fake,
            new StubModelProvidersClient([]),
            NullLogger<PlatformDashboardService>.Instance);

        var snapshot = await service.GetAsync(CancellationToken.None);

        Assert.AreEqual("No active deployments", snapshot.Status);
        Assert.AreEqual(UiStatus.Info, PlatformDashboardService.ToStatus(snapshot.Status));
    }

    [TestMethod]
    public async Task SimulatedManagementClientSupportsCrudAndConcurrency()
    {
        var fake = new MockApiClient(new FixedTimeProvider(Now));
        var request = new AgentResourceRequest
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = ResourceKinds.Agent,
            Metadata = new ResourceMetadata { Name = "new-agent" },
            Definition = new AgentProperties
            {
                DisplayName = "New agent",
                Instructions = "Help the user.",
                ModelProfile = new ResourceReference("reasoning-default")
            }
        };

        var created = await fake.PutAgentAsync(request, null, createOnly: true, CancellationToken.None);
        var stale = await Assert.ThrowsAsync<AgentstrationApiException>(() => fake.PutAgentAsync(request, "\"stale\"", createOnly: false, CancellationToken.None));
        await fake.DeleteAgentAsync("new-agent", created.ETag, CancellationToken.None);

        Assert.AreEqual(1L, created.Value.Generation);
        Assert.IsTrue(stale.IsConcurrencyConflict);
        Assert.HasCount(2, await fake.GetAgentsAsync(CancellationToken.None));
    }

    private static DeploymentSummary Deployment(string id, string status, string desiredState, string? error = null) =>
        new(id, "agent-a", "default", status, desiredState, "local", "local", "default", "revision-1", "revision-1", Now, error);

    private static TriggerResource Trigger(string name, bool enabled, TriggerLastOutcome outcome) => new()
    {
        ApiVersion = ManagementApiVersions.CoreV1,
        Kind = ResourceKinds.Trigger,
        Metadata = new ResourceMetadata { Name = name },
        Definition = new TriggerProperties
        {
            DisplayName = name,
            Enabled = enabled,
            Source = new TriggerSource { Schedule = new TriggerSchedule { Type = TriggerScheduleType.Interval, Every = "PT1H" } },
            Target = new TriggerTarget { Flow = new TriggerFlowTarget { Name = "flow-a" } }
        },
        Observed = new TriggerObservedStatus { LastOutcome = outcome }
    };

    private static ModelProviderResponse Provider(string name, string status) => new(
        name,
        name,
        new ModelProviderPropertiesResponse(name, "aep", "local", "extension", "default", "configured", status, null, 1));

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class StubManagementClient(
        IReadOnlyList<AgentSummary> agents,
        IReadOnlyList<DeploymentSummary> deployments,
        IReadOnlyList<TriggerResource> triggers) : IManagementApiClient
    {
        public Task<IReadOnlyList<AgentSummary>> GetAgentsAsync(CancellationToken cancellationToken) => Task.FromResult(agents);
        public Task<IReadOnlyList<DeploymentSummary>> GetDeploymentsAsync(CancellationToken cancellationToken) => Task.FromResult(deployments);
        public Task<IReadOnlyList<TriggerResource>> GetTriggersAsync(CancellationToken cancellationToken) => Task.FromResult(triggers);
        public Task<ResourceSnapshot<AgentResource>> GetAgentAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<AgentResource>> PutAgentAsync(AgentResourceRequest request, string? etag, bool createOnly, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAgentAsync(string name, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ManagementSummary> GetSummaryAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class StubModelProvidersClient(IReadOnlyList<ModelProviderResponse> providers) : IModelProvidersClient
    {
        public Task<IReadOnlyList<ModelProviderResponse>> GetModelProvidersAsync(CancellationToken cancellationToken) => Task.FromResult(providers);
        public Task<ResourceSnapshot<ModelProviderResource>> GetModelProviderAsync(string providerName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ModelProviderResource>> CreateModelProviderAsync(CreateModelProviderRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ModelProviderResource>> UpdateModelProviderAsync(string providerName, PutModelProviderRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteModelProviderAsync(string providerName, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ModelProviderUsagesResponse> GetModelProviderUsagesAsync(string providerName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<AvailableModelResponse>> GetProviderModelsAsync(string providerName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ModelProviderStatusResponse> GetProviderStatusAsync(string providerName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ModelProviderStatusResponse> TestProviderAsync(string providerName, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class StubWorkClient(WorkTaskOperationsCountersResponse counters) : IWorkApiClient
    {
        public Task<IReadOnlyList<WorkSummary>> GetWorkItemsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<WorkSummary>>([]);
        public Task<WorkTaskOperationsPageResponse> GetTasksAsync(string? workspaceId, WorkTaskStatus? status, string? search, bool? hasPendingAction, int page, int pageSize, string sort, string direction, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkTaskOperationsCountersResponse> GetTaskSummaryAsync(string? workspaceId, CancellationToken cancellationToken) => Task.FromResult(counters);
        public Task<WorkTaskOperationsDetailResponse> GetTaskAsync(Guid taskId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Agentstration.Flow.FlowRun> GetTaskFlowRunAsync(Guid taskId, string runId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PendingActionContract> RespondTaskPendingActionAsync(Guid taskId, Guid actionId, IReadOnlyDictionary<string, System.Text.Json.JsonElement> values, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<WorkplaceWorkspaceResponse>> GetWorkspacesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task PauseTaskAsync(Guid taskId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ResumeTaskAsync(Guid taskId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CancelTaskAsync(Guid taskId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FailingRuntimeClient : IRuntimeApiClient
    {
        public Task<IReadOnlyList<ExecutionSummary>> GetExecutionsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<RuntimeRun> CreateRunAsync(CreateRuntimeRunRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<RuntimeRun> GetRunAsync(string runId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<RuntimeRun>> GetRunsAsync(string? agentResourceId, CancellationToken cancellationToken) => throw new HttpRequestException("Runtime unavailable");
        public Task<IReadOnlyList<RuntimeRunEvent>> GetRunEventsAsync(string runId, long afterSequence, CancellationToken cancellationToken) => throw new NotSupportedException();
        public async IAsyncEnumerable<RuntimeRunEvent> ObserveRunAsync(string runId, long afterSequence, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken) { await Task.CompletedTask; yield break; }
        public Task<RuntimeRun> CancelRunAsync(string runId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<RuntimeRun> RetryRunAsync(string runId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
