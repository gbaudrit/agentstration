using System.Net;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Contracts;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
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
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(30), ResourceScopeRef.Instance)
            ]
        });

        var cut = context.Render<Deployments>();

        StringAssert.Contains(cut.Markup, "concierge");
        Assert.AreEqual("instance-1", cut.Find(".collection-identity .copyable-value code").TextContent);
        Assert.AreEqual("Instance", cut.Find(".resource-scope-badge").TextContent);
        Assert.IsNotNull(cut.Find(".collection-actions-menu"));
        Assert.AreEqual("/flow-runs/flowrun-1?scopeRef=%2Finstance", cut.Find(".collection-identity a").GetAttribute("href"));
        StringAssert.Contains(cut.Markup, "2 turn(s)");
        StringAssert.Contains(cut.Markup, $"/settings/runtime-workers/{workerId:D}");
        Assert.AreEqual("instance-1", cut.Find("[data-testid='runtime-agent-instance-row']").GetAttribute("data-instance-id"));
        Assert.AreEqual("flowrun-1", cut.Find("[data-testid='runtime-agent-instance-row']").GetAttribute("data-run-id"));
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

        StringAssert.Contains(cut.Markup, ">Workers<");
        StringAssert.Contains(cut.Markup, "runtime-worker-1");
        StringAssert.Contains(cut.Markup, "Online");
        StringAssert.Contains(cut.Find(".status-badge").ClassName, "status-success");
        StringAssert.Contains(cut.Markup, "1 active assignment(s)");
    }

    [TestMethod]
    public void WorkerDetailsUseTheStandardPageStructureAndShowDurableAssignmentHistory()
    {
        using var culture = new TestCultureScope("en-US");
        var workerId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var worker = new RuntimeWorkerSummaryResponse(workerId, "runtime-worker-1", "1", "Active",
            RuntimeWorkerPresenceState.Online, Guid.NewGuid(), "0.3.0-alpha.1+6df69331b49a5ed45cc5ca51a796f85146ebb93d",
            1, 0, now.AddMinutes(-2), now, [new("microsoft-agent-framework", "1.0", ["1.0"], "maf-1.0")]);
        var assignment = new RuntimeAssignmentPlacementResponse(Guid.NewGuid(), nameof(RuntimeAssignmentTargetKind.FlowRun),
            "flowrun-root-1", "Succeeded", "microsoft-agent-framework", "1.0", "1.0", now.AddMinutes(-1), now,
            [new(Guid.NewGuid(), workerId, Guid.NewGuid(), 1, "Succeeded", now.AddMinutes(-1), now, now.AddSeconds(30), now, null)],
            ResourceScopeRef.Instance);
        var client = new RuntimeClient { Details = new(worker, [assignment]) };
        using var context = CreateContext(client);

        var navigation = context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        navigation.NavigateTo($"/settings/runtime-workers/{workerId:D}");
        var module = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-navigation.js");
        module.Mode = JSRuntimeMode.Loose;
        module.Setup<string>("currentLocation", _ => true).SetResult(navigation.Uri + "#overview");
        var cut = context.Render<RuntimeWorkerDetails>(parameters => parameters.Add(component => component.WorkerId, workerId));

        StringAssert.Contains(cut.Markup, "Back to Workers");
        StringAssert.Contains(cut.Find(".resource-header .status-badge").ClassName, "status-success");
        Assert.AreEqual("page", cut.Find("[data-resource-section=overview]").GetAttribute("aria-current"));
        StringAssert.Contains(cut.Find("[data-worker-section=overview]").TextContent, "Identity");
        StringAssert.Contains(cut.Find("[data-worker-section=overview]").TextContent, workerId.ToString("D"));
        StringAssert.Contains(cut.Find("[data-worker-section=overview]").TextContent, "Configuration");
        StringAssert.Contains(cut.Markup, "0.3.0-alpha.1");
        Assert.IsTrue(cut.Find("[data-worker-section=assignments]").HasAttribute("hidden"));

        navigation.NavigateTo(navigation.Uri.Split('#')[0] + "#assignments");

        cut.WaitForAssertion(() => Assert.AreEqual("page", cut.Find("[data-resource-section=assignments]").GetAttribute("aria-current")));
        Assert.IsFalse(cut.Find("[data-worker-section=assignments]").HasAttribute("hidden"));
        Assert.IsTrue(cut.Find("[data-worker-section=overview]").HasAttribute("hidden"));
        StringAssert.Contains(cut.Markup, "Durable assignments handled by this Worker");
        StringAssert.Contains(cut.Markup, "/flow-runs/flowrun-root-1?scopeRef=%2Finstance");
        Assert.IsFalse(cut.Markup.Contains("This Worker has no assignment history.", StringComparison.Ordinal));
        Assert.AreEqual(1, client.WorkerReads, "Fragment transitions retain the loaded worker and bounded assignment history.");
        navigation.NavigateTo(navigation.Uri.Split('#')[0] + "#overview");
        cut.WaitForAssertion(() => Assert.IsFalse(cut.Find("[data-worker-section=overview]").HasAttribute("hidden")));
        Assert.AreEqual(1, client.WorkerReads);
        Assert.AreEqual(0, cut.FindAll(".resource-header [role=switch]").Count, "Presence is observed, not an activation flag.");
    }

    [TestMethod]
    public void WorkerDetailsRejectAResponseForAnotherWorker()
    {
        using var culture = new TestCultureScope("en-US");
        var worker = new RuntimeWorkerSummaryResponse(Guid.NewGuid(), "Another worker", "1", "Active", RuntimeWorkerPresenceState.Unknown,
            null, null, null, 0, null, null, []);
        using var context = CreateContext(new RuntimeClient { Details = new(worker, []) });
        var rendered = context.Render<RuntimeWorkerDetails>(parameters => parameters.Add(component => component.WorkerId, Guid.NewGuid()));
        StringAssert.Contains(rendered.Markup, "does not match the requested identity");
        Assert.HasCount(1, rendered.FindAll("[data-testid=runtime-worker-details]"));
        Assert.IsEmpty(rendered.FindAll("[data-worker-section], .resource-details-navigation"));
        Assert.IsFalse(rendered.Markup.Contains("Another worker", StringComparison.Ordinal));
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
        Assert.AreEqual("3", cut.FindAll(".runtime-attempt-card dd").Last().TextContent.Trim());
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Forbidden, false)]
    [DataRow(HttpStatusCode.NotFound, false)]
    [DataRow(HttpStatusCode.ServiceUnavailable, true)]
    public async Task WorkerRefreshUsesSharedStatesAndKeepsOnlyRecoverableContent(HttpStatusCode status, bool retain)
    {
        var worker = Worker(Guid.NewGuid(), "Authorized worker fixture");
        var client = new RuntimeClient { Details = new(worker, []) };
        using var context = WorkerContext(client, worker.WorkerId);
        var cut = context.Render<RuntimeWorkerDetails>(p => p.Add(c => c.WorkerId, worker.WorkerId));
        var state = cut.FindComponent<ResourceDetailsTemplate>().Instance.SectionState;
        client.ReadError = new AgentstrationApiException("fixture", "worker-fixture", status);
        await cut.InvokeAsync(() => cut.FindComponent<ResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
        Assert.AreEqual(retain, cut.FindAll("[data-worker-section=overview]").Count > 0);
        Assert.AreSame(state, cut.FindComponent<ResourceDetailsTemplate>().Instance.SectionState);
        if (retain)
        {
            Assert.AreEqual(ResourceLoadStatus.Failed, cut.FindComponent<ResourceDetailsTemplate>().Instance.LoadState!.Status);
            client.ReadError = null;
            await cut.Find("[data-testid=runtime-worker-retry]").ClickAsync(new());
            Assert.AreEqual(ResourceLoadStatus.Ready, cut.FindComponent<ResourceDetailsTemplate>().Instance.LoadState!.Status);
            Assert.AreEqual(3, client.WorkerReads);
        }
        else Assert.DoesNotContain("Authorized worker fixture", cut.Markup);
    }

    [TestMethod]
    public async Task WorkerLoadCanRetryWithoutNavigatingOrInventingAnOwner()
    {
        var worker = Worker(Guid.NewGuid(), "Worker retry fixture");
        var client = new RuntimeClient { Details = new(worker, []), ReadError = new AgentstrationApiException("fixture", "worker-fixture", HttpStatusCode.ServiceUnavailable) };
        using var context = WorkerContext(client, worker.WorkerId);
        var cut = context.Render<RuntimeWorkerDetails>(p => p.Add(c => c.WorkerId, worker.WorkerId));
        Assert.AreEqual(ResourceLoadStatus.Failed, cut.FindComponent<ResourceDetailsTemplate>().Instance.LoadState!.Status);
        Assert.IsEmpty(cut.FindAll(".resource-details-navigation, [data-worker-section], .resource-header [role=switch]"));
        client.ReadError = null; await cut.Find("[data-testid=runtime-worker-retry]").ClickAsync(new());
        Assert.Contains("Worker retry fixture", cut.Find(".resource-header").TextContent);
        Assert.IsEmpty(cut.FindAll(".resource-header .resource-scope-badge"));
    }

    [TestMethod]
    public async Task ADelayedWorkerCannotReplaceTheCurrentlyRequestedIdentity()
    {
        var firstWorker = Worker(Guid.NewGuid(), "Old worker fixture"); var current = Worker(Guid.NewGuid(), "Current worker fixture");
        var pending = new TaskCompletionSource<RuntimeWorkerDetailsResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new RuntimeClient { Details = new(current, []), PendingRead = pending.Task };
        using var context = WorkerContext(client, firstWorker.WorkerId);
        var cut = context.Render<RuntimeWorkerDetails>(p => p.Add(c => c.WorkerId, firstWorker.WorkerId));
        client.PendingRead = null;
        cut.Render(p => p.Add(c => c.WorkerId, current.WorkerId));
        await cut.InvokeAsync(() => pending.SetResult(new(firstWorker, [])));
        cut.WaitForAssertion(() => Assert.Contains("Current worker fixture", cut.Find(".resource-header").TextContent));
        Assert.DoesNotContain("Old worker fixture", cut.Markup);
    }

    [TestMethod]
    public void AssignmentLinksKeepExecutionScopeSeparateFromTheNavigationContext()
    {
        var worker = Worker(Guid.NewGuid(), "Worker fixture"); var executionScope = ResourceScopeRef.Workspace(Guid.NewGuid());
        var assignment = new RuntimeAssignmentPlacementResponse(Guid.NewGuid(), nameof(RuntimeAssignmentTargetKind.RuntimeRun), "run-fixture", "Succeeded", "microsoft-agent-framework", "1.0", "1.0", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, [], executionScope);
        using var context = WorkerContext(new RuntimeClient { Details = new(worker, [assignment]) }, worker.WorkerId);
        var cut = context.Render<RuntimeWorkerDetails>(p => p.Add(c => c.WorkerId, worker.WorkerId));
        var link = cut.Find("[data-worker-section=assignments] .worker-run-link").GetAttribute("href")!;
        Assert.Contains("scopeRef=" + Uri.EscapeDataString(executionScope.Value), link);
        Assert.Contains("context=%2Finstance", link);
    }

    private static RuntimeWorkerSummaryResponse Worker(Guid id, string name) => new(id, name, "1.0", "Active", RuntimeWorkerPresenceState.Unknown, null, null, null, 0, null, null, []);
    private static BunitContext WorkerContext(RuntimeClient client, Guid id)
    {
        var context = CreateContext(client);
        context.Services.AddSingleton<IConsoleNavigationContext>(new WorkerNavigation());
        var navigation = context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        navigation.NavigateTo($"/settings/runtime-workers/{id:D}");
        var module = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-navigation.js"); module.Mode = JSRuntimeMode.Loose;
        module.Setup<string>("currentLocation", _ => true).SetResult(navigation.Uri + "#overview");
        module.Setup<string>("navigate", _ => true).SetResult(navigation.Uri + "#overview");
        return context;
    }
    private sealed class WorkerNavigation : IConsoleNavigationContext
    {
        public string? ScopeRef => "/instance";
        public string AddToUri(string uri) => uri + (uri.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "context=%2Finstance";
    }

    private static BunitContext CreateContext(IRuntimeApiClient client)
    {
        var context = new BunitContext().WithCollectionPresentation();
        context.Services.AddLogging();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton(client);
        return context;
    }

    private sealed class RuntimeClient : IRuntimeApiClient
    {
        public IReadOnlyList<AgentInstanceResponse> Instances { get; init; } = [];
        public IReadOnlyList<RuntimeWorkerSummaryResponse> Workers { get; init; } = [];
        public RuntimeWorkerDetailsResponse? Details { get; set; }
        public Exception? ReadError { get; set; }
        public Task<RuntimeWorkerDetailsResponse>? PendingRead { get; set; }
        public RuntimeAssignmentPlacementResponse? Placement { get; init; }
        public Task<IReadOnlyList<AgentInstanceResponse>> GetAgentInstancesAsync(CancellationToken cancellationToken) => Task.FromResult(Instances);
        public Task<IReadOnlyList<RuntimeWorkerSummaryResponse>> GetRuntimeWorkersAsync(CancellationToken cancellationToken) => Task.FromResult(Workers);
        public int WorkerReads { get; private set; }
        public Task<RuntimeWorkerDetailsResponse> GetRuntimeWorkerAsync(Guid workerId, CancellationToken cancellationToken)
        {
            WorkerReads++;
            if (ReadError is { } error) return Task.FromException<RuntimeWorkerDetailsResponse>(error);
            return PendingRead ?? Task.FromResult(Details ?? throw new InvalidOperationException("Worker details were not configured."));
        }
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
