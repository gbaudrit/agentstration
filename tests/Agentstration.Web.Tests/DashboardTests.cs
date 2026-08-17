using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Web.Console;
using Agentstration.Work;
using Agentstration.Work.Contracts;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class DashboardTests
{
    [TestMethod]
    public async Task SimulatedDashboardAggregatesEveryPlane()
    {
        var fake = new MockApiClient(new FixedTimeProvider(new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero)));
        var service = new PlatformDashboardService(fake, fake, new StubWorkClient(), fake);

        var snapshot = await service.GetAsync(CancellationToken.None);

        Assert.AreEqual(2, snapshot.KnownAgents);
        Assert.AreEqual(0, snapshot.ActiveAgents);
        Assert.AreEqual(3, snapshot.OpenWorkItems);
        Assert.AreEqual("Degraded", snapshot.Status);
    }

    [TestMethod]
    public async Task SimulatedManagementClientSupportsCrudAndConcurrency()
    {
        var fake = new MockApiClient(new FixedTimeProvider(new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero)));
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

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class StubWorkClient : IWorkApiClient
    {
        public Task<IReadOnlyList<WorkSummary>> GetWorkItemsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<WorkSummary>>([
            new(Guid.NewGuid(), "One", "WorkTask", "Running", "—", "personal", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new(Guid.NewGuid(), "Two", "WorkTask", "Pending", "—", "personal", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new(Guid.NewGuid(), "Three", "WorkTask", "ActionRequired", "—", "personal", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)]);
        public Task<WorkTaskOperationsPageResponse> GetTasksAsync(string? workspaceId, WorkTaskStatus? status, string? search, bool? hasPendingAction, int page, int pageSize, string sort, string direction, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkTaskOperationsCountersResponse> GetTaskSummaryAsync(string? workspaceId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkTaskOperationsDetailResponse> GetTaskAsync(Guid taskId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Agentstration.Flow.FlowRun> GetTaskFlowRunAsync(Guid taskId, string runId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<WorkplaceWorkspaceResponse>> GetWorkspacesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task PauseTaskAsync(Guid taskId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ResumeTaskAsync(Guid taskId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CancelTaskAsync(Guid taskId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
