using System.Net;
using System.Security.Claims;
using Agentstration.Bootstrap.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Bootstrap;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class BootstrapStartupProgressTests
{
    [TestMethod]
    public void PendingStartupRedirectsWithoutRenderingTheOverview()
    {
        var api = new StartupClient();
        using var context = Context(api);
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/?source=login");
        var rendered = false;
        _ = context.Render<BootstrapStartupGate>(p => p.Add(x => x.ChildContent, builder => { rendered = true; builder.AddContent(0, "overview"); }));
        Assert.IsFalse(rendered);
        StringAssert.Contains(navigation.Uri, "/startup/progress?ReturnUrl=%2F%3Fsource%3Dlogin");
        Assert.AreEqual(0, api.Reads);
    }

    [TestMethod]
    public void DeniedCallersSeeNoRouteOrProgress()
    {
        var api = new StartupClient { AdmissionDenied = true };
        using var context = Context(api);
        var cut = context.Render<BootstrapStartupGate>(p => p.AddChildContent("normal-route"));
        StringAssert.Contains(cut.Markup, "Access temporarily unavailable");
        Assert.IsFalse(cut.Markup.Contains("normal-route", StringComparison.Ordinal));
        Assert.AreEqual(0, api.Reads);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void AdmittedRoutesKeepTheirComponentDuringContextUpdates(bool ready)
    {
        var api = new StartupClient { Ready = ready };
        using var context = Context(api);
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(ready ? "/" : "/startup/progress");
        var cut = context.Render<BootstrapStartupGate>(p => p.AddChildContent(builder =>
        {
            builder.OpenComponent<RouteProbe>(0);
            builder.CloseComponent();
        }));
        var route = cut.FindComponent<RouteProbe>().Instance;
        api.NextAdmission = new(TaskCreationOptions.RunContinuationsAsynchronously);
        navigation.NavigateTo(ready ? "/agents?context=%2Finstance" : "/startup/progress?context=%2Finstance");
        cut.Render();
        Assert.AreSame(route, cut.FindComponent<RouteProbe>().Instance);
        Assert.AreEqual(1, api.AdmissionReads);
        if (!ready)
        {
            api.NextAdmission = null;
            navigation.NavigateTo("/agent-runs");
            cut.Render();
            Assert.AreEqual(2, api.AdmissionReads);
            StringAssert.Contains(navigation.Uri, "/startup/progress?ReturnUrl=%2Fagent-runs");
            Assert.AreEqual(0, cut.FindComponents<RouteProbe>().Count);
        }
    }

    [TestMethod]
    [DataRow("en-US", "Instance initialization", "Required model")]
    [DataRow("fr-FR", "Initialisation de l’instance", "Modèle attendu")]
    public void TheSharedProgressPanelUsesProviderNeutralDataAndLocalizedLabels(string culture, string title, string modelLabel)
    {
        using var cultures = new TestCultureScope(culture);
        var api = new StartupClient();
        using var context = Context(api);
        var cut = context.Render<BootstrapStartupProgress>();
        StringAssert.Contains(System.Net.WebUtility.HtmlDecode(cut.Markup), title);
        StringAssert.Contains(cut.Find("[role='tabpanel']").TextContent, modelLabel);
        StringAssert.Contains(cut.Find("[role='tabpanel']").TextContent, "model:exact");
        Assert.AreEqual(0, cut.FindAll("[role='tabpanel'] a").Count);
        Assert.AreEqual(1, api.Reads);
    }

    [TestMethod]
    public void StartupLayoutUsesTheStandardFrameWithConsultationAndPersistentNotice()
    {
        using var context = Context(new StartupClient());
        var cut = context.Render<BootstrapStartupLayout>(p => p.Add(x => x.Body, builder => builder.AddContent(0, "Progress body")));
        Assert.IsFalse(cut.FindComponent<MainLayout>().Instance.EnableCommandPalette);
        Assert.AreEqual(1, cut.FindAll("[data-testid='console-shell']").Count);
        Assert.AreEqual(1, cut.FindAll(".sidebar").Count);
        Assert.AreEqual(1, cut.FindAll("[data-testid='bootstrap-startup-notice']").Count);
        Assert.AreEqual(1, cut.FindAll("a[href='/logout']").Count);
        StringAssert.Contains(cut.Find("main").TextContent, "Progress body");
    }

    [TestMethod]
    public void OnlyPlannedFamiliesAppearAndTheResourceCollectionIsPagedAndFilterable()
    {
        var api = new StartupClient();
        api.Snapshot = api.Snapshot with
        {
            Families = [new("resources", "Parameter", "running", 512, 511, 511, 0, 1, 0, 0)]
        };
        api.ResourceRows = Enumerable.Range(0, 512).Select(index => new BootstrapStartupResource(
            "row-" + index, "resources", "load-test", index + ".yaml", "Parameter", $"item-{index:D3}",
            "default", "/tenants/dev", index == 511 ? "failed" : "created",
            [new("resource_apply", index == 511 ? "failed" : "created", DateTimeOffset.UnixEpoch)])).ToArray();
        using var context = Context(api);
        var cut = context.Render<BootstrapStartupProgress>();
        Assert.AreEqual(5, cut.FindAll("[role='tab']").Count, "Only the four stages and one selected family should appear.");
        Assert.AreEqual(0, cut.FindAll("[data-step-id='resources:Agent']").Count);
        cut.Find("[data-step-id='resources:Parameter']").Click();
        cut.WaitForAssertion(() => Assert.AreEqual(25, cut.FindAll("[data-testid='startup-resource-row']").Count));
        Assert.AreEqual(25, api.ResourceQueries.Last().Limit);
        cut.FindAll(".collection-pager button").Single(button => button.TextContent.Trim() == "Next").Click();
        cut.WaitForAssertion(() => Assert.AreEqual(25, api.ResourceQueries.Last().Offset));
        cut.Find("[data-testid='startup-resource-state']").Change("failed");
        cut.WaitForAssertion(() => Assert.AreEqual(1, cut.FindAll("[data-testid='startup-resource-row']").Count));
        Assert.AreEqual(0, api.ResourceQueries.Last().Offset);
        cut.Find("[data-testid='startup-resource-row'] .collection-actions button").Click();
        StringAssert.Contains(cut.Find("[data-testid='startup-resource-operations']").TextContent, "Apply resource");
        Assert.AreEqual("failed", api.ResourceQueries.Last().State);
    }

    [TestMethod]
    [DataRow("en-US", "Change provider or model")]
    [DataRow("fr-FR", "Modifier le fournisseur ou le modèle")]
    public void ABlockedProfileCanSelectAnotherProviderAndKnownModelWithoutLeavingProgress(string culture, string label)
    {
        using var cultures = new TestCultureScope(culture);
        var api = new StartupClient();
        using var context = Context(api);
        var cut = context.Render<BootstrapStartupModelCorrection>(p => p.Add(x => x.AttemptId, api.Snapshot.AttemptId).Add(x => x.ResourceId, "profile-id"));
        StringAssert.Contains(cut.Find("button").TextContent, label);
        cut.Find("[data-testid='startup-change-model']").Click();
        cut.Find("[data-testid='startup-model-provider']").Change(api.BackupProvider.ToString("D"));
        cut.Find("[data-testid='startup-model-name']").Change("replacement:model");
        cut.Find("[data-testid='startup-model-apply']").Click();
        Assert.AreEqual(1, api.ModelChanges.Count);
        Assert.AreEqual("backup", api.ModelChanges[0].Provider.Name);
        Assert.AreEqual(ResourceScopeRef.Instance, api.ModelChanges[0].Provider.ScopeRef);
        Assert.AreEqual("replacement:model", api.ModelChanges[0].Model);
        Assert.AreEqual(api.Snapshot.AttemptId, api.ModelChanges[0].AttemptId);
        Assert.AreEqual(0L, api.ModelChanges[0].Version);
        Assert.AreEqual(0, cut.FindAll("[data-testid='startup-model-apply']").Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PollingRecoversAndRedirectsOnlyAtReadinessToALocalDestination(bool timeout)
    {
        var api = new StartupClient();
        var clock = new ManualClock();
        using var context = Context(api, clock);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/startup/progress?ReturnUrl=%2F%2Fexternal.example");
        var cut = context.Render<BootstrapStartupProgress>();
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        // The Console shell preserves context through a location-changing handler.
        using var registration = navigation.RegisterLocationChangingHandler(change =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(change.TargetLocation);
            return ValueTask.CompletedTask;
        });
        api.Timeout = timeout; api.FailRead = true;
        clock.Tick();
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, "Observation will resume"));
        Assert.IsFalse(cut.Markup.Contains("PRIVATE-TRANSPORT-ERROR", StringComparison.Ordinal));
        api.FailRead = false; api.Snapshot = api.Snapshot with { Ready = true, State = "ready" };
        clock.Tick();
        cut.WaitForAssertion(() => Assert.AreEqual("http://localhost/", navigation.Uri));
        Assert.IsTrue(api.Reads >= 3);
    }

    [TestMethod]
    public void TerminalFailureRetainsTheProgressViewWithRecoveryGuidance()
    {
        var api = new StartupClient();
        api.Snapshot = api.Snapshot with { State = "failed", Steps = api.Snapshot.Steps.Select(s => s.Id == "resources" ? s with { State = "failed" } : s).ToArray() };
        using var context = Context(api);
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/startup/progress");
        var cut = context.Render<BootstrapStartupProgress>();
        StringAssert.Contains(cut.Markup, "Bootstrap failed");
        StringAssert.Contains(cut.Markup, "restart the instance");
        Assert.AreEqual("http://localhost/startup/progress", navigation.Uri);
    }

    private static BunitContext Context(StartupClient api, TimeProvider? clock = null)
    {
        var context = new BunitContext();
        context.Services.AddAgentstrationWebComponents();
        context.Services.AddSingleton<TimeProvider>(clock ?? TimeProvider.System);
        context.Services.AddSingleton<IBootstrapStartupApiClient>(api);
        context.Services.AddSingleton<AuthenticationStateProvider>(new UserState());
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/step-progress.js").Mode = JSRuntimeMode.Loose;
        context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/collection-ui.js").Mode = JSRuntimeMode.Loose;
        return context;
    }
    private sealed class RouteProbe : ComponentBase { }
    private sealed class UserState : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "Administrator")], "fixture"))));
    }
    private sealed class StartupClient : IBootstrapStartupApiClient
    {
        public bool Ready { get; set; }
        public bool AdmissionDenied { get; set; }
        public bool FailRead { get; set; }
        public bool Timeout { get; set; }
        public int Reads { get; private set; }
        public int AdmissionReads { get; private set; }
        public Guid MainProvider { get; } = Guid.NewGuid();
        public Guid BackupProvider { get; } = Guid.NewGuid();
        public List<BootstrapStartupModelChange> ModelChanges { get; } = [];
        public Task<BootstrapStartupModelOptions> GetModelOptionsAsync(Guid attemptId, string resourceId, string? providerName, string? providerNamespace, CancellationToken cancellationToken) =>
            Task.FromResult(new BootstrapStartupModelOptions(attemptId, resourceId, 0,
                new(providerName ?? "primary", ResourceScopeRef.Instance, ResourceNamespace.Default), "missing:model",
                [new(MainProvider, new("primary", ResourceScopeRef.Instance, ResourceNamespace.Default), "Primary"),
                 new(BackupProvider, new("backup", ResourceScopeRef.Instance, ResourceNamespace.Default), "Backup")], ["replacement:model"]));
        public Task ApplyModelChangeAsync(string resourceId, BootstrapStartupModelChange request, CancellationToken cancellationToken)
        { ModelChanges.Add(request); return Task.CompletedTask; }
        public IReadOnlyList<BootstrapStartupResource> ResourceRows { get; set; } = [];
        public List<BootstrapStartupResourceQuery> ResourceQueries { get; } = [];
        public Task<BootstrapStartupResourcePage> GetResourcesAsync(BootstrapStartupResourceQuery query, CancellationToken cancellationToken)
        {
            ResourceQueries.Add(query);
            var matches = ResourceRows.Where(row => (query.StepId is null || row.StepId == query.StepId)
                && (query.Kind is null || row.Kind == query.Kind) && (query.State is null || row.State == query.State)).ToArray();
            return Task.FromResult(new BootstrapStartupResourcePage(Snapshot.AttemptId, Snapshot.Revision,
                matches.Length, query.Offset, query.Limit, matches.Skip(query.Offset).Take(query.Limit).ToArray()));
        }
        public TaskCompletionSource<BootstrapStartupAdmission>? NextAdmission { get; set; }
        public BootstrapStartupSnapshot Snapshot { get; set; } = new(Guid.NewGuid(), 1, false, true, "running",
            [new("topology", "succeeded"), new("extensions", "succeeded"), new("resources", "waiting"), new("finalization", "pending")],
            "resources", new("ModelProfile", "profile", "provider", "model:exact", "required_model_missing", "other", ProviderScopeRef: "/instance"), 2, 1, 0, 0);
        public Task<BootstrapStartupAdmission> GetAdmissionAsync(CancellationToken cancellationToken)
        {
            AdmissionReads++;
            return AdmissionDenied ? throw new AgentstrationApiException("PRIVATE", "fixture", HttpStatusCode.Forbidden)
                : NextAdmission?.Task.WaitAsync(cancellationToken) ?? Task.FromResult(new BootstrapStartupAdmission(Ready));
        }
        public Task<BootstrapStartupSnapshot> GetAsync(CancellationToken cancellationToken)
        {
            Reads++;
            if (FailRead && Timeout) throw new TaskCanceledException("PRIVATE-TRANSPORT-ERROR");
            return FailRead ? throw new AgentstrationApiException("PRIVATE-TRANSPORT-ERROR", "fixture", HttpStatusCode.ServiceUnavailable)
                : Task.FromResult(Snapshot);
        }
    }
    private sealed class ManualClock : TimeProvider
    {
        private Action? tick;
        public void Tick() => tick?.Invoke();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(() => callback(state)); tick = timer.Tick; return timer;
        }
        private sealed class ManualTimer(Action callback) : ITimer
        {
            private bool disposed;
            public void Tick() { if (!disposed) callback(); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !disposed;
            public void Dispose() => disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
