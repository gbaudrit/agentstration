using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Contracts;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
[DoNotParallelize]
public sealed class RuntimeObservabilityConsoleTests
{
    [TestMethod]
    public void AgentInstancesRenderEffectiveWorkerAndTurnCount()
    {
        using var culture = new TestCultureScope("en-US");
        var workerId = Guid.NewGuid();
        using var context = CreateContext(new RuntimeClient
        {
            Instances =
            [
                new("instance-1", ResourceNamespace.Default, "concierge", 3, "concierge--000003",
                    "maf-local", "FlowRun", "flowrun-1", "support", Guid.NewGuid(), 2,
                    Guid.NewGuid(), Guid.NewGuid(), workerId, Guid.NewGuid(), DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(30))
            ]
        });

        var cut = context.Render<Deployments>();

        StringAssert.Contains(cut.Markup, "concierge");
        StringAssert.Contains(cut.Markup, "2 turn(s)");
        StringAssert.Contains(cut.Markup, $"/settings/runtime-workers/{workerId:D}");
        Assert.IsFalse(cut.Markup.Contains("In process", StringComparison.Ordinal));
    }

    [TestMethod]
    public void WorkerInventoryDistinguishesPresenceAndEnrollment()
    {
        using var culture = new TestCultureScope("en-US");
        var workerId = Guid.NewGuid();
        using var context = CreateContext(new RuntimeClient
        {
            Workers =
            [
                new(workerId, "runtime-worker-1", "1", "Active", RuntimeWorkerPresenceState.Online,
                    Guid.NewGuid(), "1.4.0", 4, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                    [new("microsoft-agent-framework", "1.0", ["1.0"], "maf-1.0")])
            ]
        });

        var cut = context.Render<RuntimeWorkers>();

        StringAssert.Contains(cut.Markup, "runtime-worker-1");
        StringAssert.Contains(cut.Markup, "Online");
        StringAssert.Contains(cut.Markup, "1 active assignment(s)");
    }

    [TestMethod]
    public void RunPlacementLinksEffectiveWorkerAndPreservesAttemptHistory()
    {
        using var culture = new TestCultureScope("en-US");
        var workerId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        using var context = CreateContext(new RuntimeClient
        {
            Placement = new(Guid.NewGuid(), nameof(RuntimeAssignmentTargetKind.RuntimeRun), "run-1", "Assigned",
                "microsoft-agent-framework", "1.0", "1.0", now, now,
                [new(Guid.NewGuid(), workerId, Guid.NewGuid(), 3, "Active", now, now, now.AddSeconds(30), null, null)])
        });

        var cut = context.Render<RuntimePlacementSummary>(parameters => parameters
            .Add(component => component.TargetKind, RuntimeAssignmentTargetKind.RuntimeRun)
            .Add(component => component.RunId, "run-1"));

        StringAssert.Contains(cut.Markup, "Effective Worker");
        StringAssert.Contains(cut.Markup, $"/settings/runtime-workers/{workerId:D}");
        StringAssert.Contains(cut.Markup, "Attempt history (1)");
        StringAssert.Contains(cut.Markup, "Fencing generation");
        StringAssert.Contains(cut.Markup, "<dd>3</dd>");
    }

    private static BunitContext CreateContext(IRuntimeApiClient client)
    {
        var context = new BunitContext();
        context.Services.AddLogging();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton(client);
        return context;
    }

    private sealed class RuntimeClient : IRuntimeApiClient
    {
        public IReadOnlyList<AgentInstanceResponse> Instances { get; init; } = [];
        public IReadOnlyList<RuntimeWorkerSummaryResponse> Workers { get; init; } = [];
        public RuntimeAssignmentPlacementResponse? Placement { get; init; }
        public Task<IReadOnlyList<AgentInstanceResponse>> GetAgentInstancesAsync(CancellationToken cancellationToken) => Task.FromResult(Instances);
        public Task<IReadOnlyList<RuntimeWorkerSummaryResponse>> GetRuntimeWorkersAsync(CancellationToken cancellationToken) => Task.FromResult(Workers);
        public Task<RuntimeAssignmentPlacementResponse?> GetPlacementAsync(RuntimeAssignmentTargetKind targetKind, string runId, CancellationToken cancellationToken) => Task.FromResult(Placement);
        public Task<IReadOnlyList<ExecutionSummary>> GetExecutionsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<RuntimeRun> CreateRunAsync(CreateRuntimeRunRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<RuntimeRun> GetRunAsync(string runId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<RuntimeRun>> GetRunsAsync(string? agentResourceId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<RuntimeRunEvent>> GetRunEventsAsync(string runId, long afterSequence, CancellationToken cancellationToken) => throw new NotSupportedException();
        public IAsyncEnumerable<RuntimeRunEvent> ObserveRunAsync(string runId, long afterSequence, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<RuntimeRun> CancelRunAsync(string runId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<RuntimeRun> RetryRunAsync(string runId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
