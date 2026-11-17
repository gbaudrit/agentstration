using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Agentstration.Resources;
using Agentstration.SystemGraph.Contracts;
using Agentstration.SystemOperations.Contracts;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class SystemOperationsPanelTests
{
    [TestMethod]
    [DataRow("en-US", "Operational evidence", "Metrics unavailable")]
    [DataRow("fr-FR", "Données opérationnelles", "Métriques indisponibles")]
    public async Task OverlayIsOptInLocalizedAndClearsFactsWhenStreamDisconnects(string culture, string title, string unknown)
    {
        using var selectedCulture = new TestCultureScope(culture);
        using var context = new BunitContext();
        var client = new Client();
        context.Services.AddSingleton(TimeProvider.System);
        context.Services.AddLocalization(o => o.ResourcesPath = "Resources");
        context.Services.AddSingleton<ISystemOperationsClient>(client);
        var scope = ResourceScopeRef.Instance;
        var component = context.Render<SystemOperationsPanel>(p => p.Add(v => v.Root, "root").Add(v => v.Context, scope).Add(v => v.Namespace, ResourceNamespace.Default));
        Assert.Contains(title, component.Markup); Assert.AreEqual(0, client.Reads);
        await component.Find("input").ChangeAsync(true);
        var facts = new SystemOperationalFacts("root", new(Guid.NewGuid(), scope, ResourceNamespace.Default, "Agent", "safe"),
            "definition", "unknown", "unknown", "unknown", null, [], scope, scope, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "attested-window", false, []);
        await client.Frames.Writer.WriteAsync(new(Guid.NewGuid(), 1, 0, "snapshot", DateTimeOffset.UnixEpoch, facts));
        component.WaitForAssertion(() => Assert.Contains(unknown, component.Markup));
        Assert.IsFalse(component.Markup.Contains("OpsState_", StringComparison.Ordinal));
        client.Frames.Writer.Complete();
        component.WaitForAssertion(() => Assert.IsEmpty(component.FindAll("dl")));
        Assert.AreEqual(1, client.Reads);
    }
    [TestMethod]
    public async Task StalledFrameCancelsBeforeIteratorDisposalAndClearsStaleFacts()
    {
        using var context = new BunitContext();
        var clock = new ManualClock(); var client = new Client();
        context.Services.AddSingleton<TimeProvider>(clock);
        context.Services.AddLocalization(o => o.ResourcesPath = "Resources");
        context.Services.AddSingleton<ISystemOperationsClient>(client);
        var component = context.Render<SystemOperationsPanel>(p => p.Add(v => v.Root, "root").Add(v => v.Context, ResourceScopeRef.Instance).Add(v => v.Namespace, ResourceNamespace.Default));
        await component.Find("input").ChangeAsync(true);
        var facts = new SystemOperationalFacts("root", new(Guid.NewGuid(), ResourceScopeRef.Instance, ResourceNamespace.Default, "Agent", "safe"),
            "definition", "unknown", "unknown", "unknown", null, [], ResourceScopeRef.Instance, ResourceScopeRef.Instance,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "attested-window", false, []);
        await client.Frames.Writer.WriteAsync(new(Guid.NewGuid(), 1, 0, "snapshot", clock.GetUtcNow(), facts));
        component.WaitForElement("dl");
        clock.Advance(TimeSpan.FromSeconds(31));
        component.WaitForAssertion(() =>
        {
            Assert.IsEmpty(component.FindAll("dl"));
            Assert.Contains("Evidence is stale", component.Markup);
        });
    }
    [TestMethod]
    [DataRow("en-US", "Flow binding evidence", "Observed distinct turns")]
    [DataRow("fr-FR", "Bindings enregistrés par les Flow", "Tours distincts observés")]
    public async Task FlowBindingProofLocalizesAndLinksScopedStepWithoutInventingMetrics(string culture, string title, string turns)
    {
        using var selectedCulture = new TestCultureScope(culture);
        using var context = new BunitContext();
        var client = new Client();
        context.Services.AddSingleton(TimeProvider.System);
        context.Services.AddLocalization(o => o.ResourcesPath = "Resources");
        context.Services.AddSingleton<ISystemOperationsClient>(client);
        var scope = ResourceScopeRef.Workspace(Guid.NewGuid());
        var component = context.Render<SystemOperationsPanel>(p => p.Add(v => v.Root, "root").Add(v => v.Context, scope).Add(v => v.Namespace, ResourceNamespace.Default));
        await component.Find("input").ChangeAsync(true);
        var invocation = new SystemFlowInvocationEvidence(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "respond",
            Guid.NewGuid(), Guid.NewGuid(), 3, "active", 2, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var binding = new SystemFlowBindingEvidence("flow-id", scope, scope, ResourceScopeRef.Instance, ResourceNamespace.Default,
            Guid.NewGuid(), Guid.NewGuid(), 2, "hash", "<img src=x>", "running", DateTimeOffset.UnixEpoch, [invocation]);
        var facts = new SystemOperationalFacts("root", new(binding.AgentUid, ResourceScopeRef.Instance, ResourceNamespace.Default, "Agent", "safe"),
            "definition", "unknown", "unknown", "unknown", null, [], scope, scope, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "attested-window", false, [])
        { FlowBindings = [binding] };
        await client.Frames.Writer.WriteAsync(new(Guid.NewGuid(), 1, 0, "snapshot", DateTimeOffset.UnixEpoch, facts));
        component.WaitForElement("[data-testid=system-flow-bindings]");
        Assert.Contains(title, component.Markup); Assert.Contains(turns, component.Markup);
        Assert.IsEmpty(component.FindAll("img"));
        var link = component.Find("[data-testid=system-flow-bindings] a[href*='step=']");
        Assert.Contains("/flow-runs/flow-id?", link.GetAttribute("href")!);
        Assert.Contains("step=respond", link.GetAttribute("href")!);
        Assert.Contains("scopeRef=%2Fworkspaces%2F", link.GetAttribute("href")!);
        Assert.Contains("context=%2Fworkspaces%2F", link.GetAttribute("href")!);
        Assert.IsNull(facts.Metrics);
    }
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UnixEpoch;
        private readonly List<ManualTimer> timers = [];
        public override DateTimeOffset GetUtcNow() => now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { var timer = new ManualTimer(this, callback, state); timers.Add(timer); timer.Change(dueTime, period); return timer; }
        public void Advance(TimeSpan elapsed) { now += elapsed; foreach (var timer in timers.ToArray()) timer.Fire(); }
        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            private DateTimeOffset? due;
            public bool Change(TimeSpan dueTime, TimeSpan period) { due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.now + dueTime; return true; }
            public void Fire() { if (due is { } at && at <= clock.now) { due = null; callback(state); } }
            public void Dispose() => due = null;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
    private sealed class Client : ISystemOperationsClient
    {
        public readonly Channel<SystemOperationsFrame> Frames = Channel.CreateBounded<SystemOperationsFrame>(1);
        public int Reads;
        public async IAsyncEnumerable<SystemOperationsFrame> WatchAsync(ResourceScopeRef context, SystemOperationsQuery query,
            [EnumeratorCancellation] CancellationToken ct)
        { ++Reads; await foreach (var frame in Frames.Reader.ReadAllAsync(ct)) yield return frame; }
    }
}
