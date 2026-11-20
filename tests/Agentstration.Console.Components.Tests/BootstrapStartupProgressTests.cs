using System.Net;
using System.Security.Claims;
using Agentstration.Bootstrap.Contracts;
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
        navigation.NavigateTo("/agents/assistant?tab=overview");
        var rendered = false;
        _ = context.Render<BootstrapStartupGate>(p => p.Add(x => x.ChildContent, builder => { rendered = true; builder.AddContent(0, "overview"); }));
        Assert.IsFalse(rendered);
        StringAssert.Contains(navigation.Uri, "/startup/progress?ReturnUrl=%2Fagents%2Fassistant%3Ftab%3Doverview");
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
            navigation.NavigateTo("/agents");
            cut.Render();
            Assert.AreEqual(2, api.AdmissionReads);
            StringAssert.Contains(navigation.Uri, "/startup/progress?ReturnUrl=%2Fagents");
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
        Assert.AreEqual("/modelproviders/provider?namespace=other&scopeRef=%2Finstance&context=%2Finstance", cut.Find("[role='tabpanel'] a").GetAttribute("href"));
        Assert.AreEqual(1, api.Reads);
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
