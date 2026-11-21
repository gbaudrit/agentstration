using System.Net;
using System.Net.Http.Json;
using Agentstration.Flows;
using Agentstration.Flows.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Agentstration.Web.FlowDesigner.Components;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class FlowResourcePreviewTests
{
    private static readonly ResourceScopeRef Scope = ResourceScopeRef.Workspace(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001"));

    [TestMethod]
    [DataRow("en-US", "Identity", "Configuration")]
    [DataRow("fr-FR", "Identité", "Configuration")]
    public async Task PreviewReusesTopologyIdentityAndConfigurationWithOneExactRead(string locale, string identity, string configuration)
    {
        using var culture = new TestCultureScope(locale);
        using var handler = new Handler((_, _) => Task.FromResult(Response(Resource("flow-a"))));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("flow-a"));
        Assert.AreEqual(1, handler.Reads);
        Assert.AreEqual($"/api/flows/flow-a?scopeRef={Uri.EscapeDataString(Scope.Value)}", handler.LastPath);
        Assert.HasCount(1, cut.FindComponents<FlowResourceSummary>());
        Assert.HasCount(1, cut.FindComponents<ResourceIdentitySummary>());
        Assert.AreEqual(identity, cut.Find("[data-testid=flow-identity-summary] h2").TextContent);
        Assert.AreEqual(configuration, cut.Find("[data-testid=flow-configuration-summary] h2").TextContent);
        Assert.Contains("Saved flow-a", cut.Find("[data-testid=flow-identity-summary]").TextContent);
        var identityToggle = cut.Find("[data-testid=flow-identity-summary] .panel-disclosure");
        Assert.AreEqual("true", identityToggle.GetAttribute("aria-expanded"));
        await identityToggle.ClickAsync(new());
        Assert.AreEqual("false", identityToggle.GetAttribute("aria-expanded"));
        Assert.IsTrue(cut.Find("[data-testid=flow-identity-summary] .form-section-body").HasAttribute("hidden"));
        await identityToggle.ClickAsync(new());
        Assert.AreEqual("true", identityToggle.GetAttribute("aria-expanded"));
        Assert.Contains("1.0.0", cut.Find("[data-testid=flow-configuration-summary]").TextContent);
        Assert.HasCount(1, cut.FindComponents<Bunit.TestDoubles.Stub<FlowTopologyViewer>>());
        Assert.AreEqual("true", cut.Find("[data-testid=flow-configuration-summary] .panel-disclosure").GetAttribute("aria-expanded"));
        Assert.HasCount(1, cut.FindAll("[data-testid=flow-preview-open]"));
        Assert.HasCount(1, cut.FindAll("[data-testid=flow-preview-edit]"));
        Assert.AreEqual(locale == "fr-FR" ? "Modifier la définition" : "Edit definition", cut.Find("[data-testid=flow-preview-edit]").TextContent.Trim());
        Assert.HasCount(1, cut.FindAll("[data-testid=flow-preview-designer]"));
        Assert.IsEmpty(cut.FindAll("form, [data-testid=flow-summary-reconcile], .declaration-resolution-grid, .collection-view-switch"));
        cut.Render(p => p.Add(c => c.Address, Target("flow-a")));
        Assert.AreEqual(1, handler.Reads, "A presentation rerender must not refetch the definition.");
    }

    [TestMethod]
    public async Task OwnerMismatchNeverRendersTheOtherWorkspacesResource()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(Resource("flow-a") with { ScopeRef = ResourceScopeRef.Instance })));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("flow-a"));
        Assert.AreEqual(ResourceLoadStatus.Forbidden, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
        Assert.IsEmpty(cut.FindComponents<FlowResourceSummary>());
        Assert.DoesNotContain("Saved flow-a", cut.Markup);
    }

    [TestMethod]
    public async Task InheritedResourceOffersInspectionWithoutAnEditShortcut()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(Resource("flow-a"))));
        using var context = await ContextAsync(handler, selectedScope: "/instance");
        var cut = Render(context, Target("flow-a"));
        Assert.HasCount(1, cut.FindAll("[data-testid=flow-preview-open]"));
        Assert.IsEmpty(cut.FindAll("[data-testid=flow-preview-edit], [data-testid=flow-preview-designer]"));
        Assert.IsTrue(cut.FindComponent<ManagedResourceBadge>().Instance.IsReadOnly);
    }

    [TestMethod]
    [DataRow(403, ResourceLoadStatus.Forbidden)]
    [DataRow(404, ResourceLoadStatus.NotFound)]
    public async Task ExpectedReadFailuresUseSharedFeedbackWithoutLeakingTransportContent(int status, ResourceLoadStatus expected)
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        { Content = JsonContent.Create(new { title = "Private transport detail", status }) }));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("flow-a"));
        Assert.AreEqual(expected, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
        Assert.DoesNotContain("Private transport detail", cut.Markup);
        Assert.IsEmpty(cut.FindComponents<FlowResourceSummary>());
    }

    [TestMethod]
    public async Task RetryRepeatsOnlyTheSelectedResourceRead()
    {
        var calls = 0;
        using var handler = new Handler((_, _) => Task.FromResult(++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : Response(Resource("flow-a"))));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("flow-a"));
        Assert.AreEqual(ResourceLoadStatus.Failed, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
        await cut.Find("[data-testid=flow-preview-retry]").ClickAsync(new());
        Assert.HasCount(1, cut.FindComponents<FlowResourceSummary>());
        Assert.AreEqual(2, handler.Reads);
    }

    [TestMethod]
    public async Task ReplacingSelectionCancelsAndRejectsThePreviousLateResponse()
    {
        var old = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler((request, _) => request.RequestUri!.AbsolutePath.EndsWith("flow-a", StringComparison.Ordinal)
            ? old.Task : Task.FromResult(Response(Resource("flow-b"))));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("flow-a"));
        var oldToken = handler.LastToken;
        Assert.HasCount(1, cut.FindComponents<LoadingState>());
        cut.Render(p => p.Add(c => c.Address, Target("flow-b")));
        Assert.IsTrue(oldToken.IsCancellationRequested);
        cut.WaitForAssertion(() => Assert.Contains("Saved flow-b", cut.Markup));
        var renders = cut.RenderCount;
        old.SetResult(Response(Resource("flow-a")));
        cut.WaitForState(() => cut.RenderCount > renders);
        Assert.Contains("Saved flow-b", cut.Markup);
        Assert.DoesNotContain("Saved flow-a", cut.Markup);
    }

    [TestMethod]
    public async Task ClosingCancelsThePendingRead()
    {
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler((_, _) => response.Task);
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("flow-a"));
        var token = handler.LastToken;
        await context.DisposeComponentsAsync();
        Assert.IsTrue(token.IsCancellationRequested);
        response.SetResult(Response(Resource("flow-a")));
    }

    [TestMethod]
    public async Task MissingReadPermissionPerformsNoRequest()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Unexpected read."));
        using var context = await ContextAsync(handler, permissions: new HashSet<string>());
        var cut = Render(context, Target("flow-a"));
        Assert.AreEqual(0, handler.Reads);
        Assert.AreEqual(ResourceLoadStatus.Forbidden, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
    }

    [TestMethod]
    public async Task MissingOwnerNeverFallsBackToAnUnscopedRead()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Unexpected unscoped read."));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("flow-a") with { ScopeRef = default });
        Assert.AreEqual(0, handler.Reads);
        Assert.AreEqual(ResourceLoadStatus.Failed, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
    }

    [TestMethod]
    [DataRow("uid")]
    [DataRow("etag")]
    [DataRow("match")]
    public async Task GraphPreviewRequiresTheObservedUidAndSavedState(string change)
    {
        var saved = Resource("graph-preview");
        using var handler = new Handler((_, _) => Task.FromResult(Response(saved)));
        using var context = await ContextAsync(handler);
        var expectation = new ResourcePreviewExpectation(Scope, change == "uid" ? Guid.NewGuid() : saved.Uid!.Value,
            change == "generation" ? 999 : null, change == "etag" ? "stale" : "\"preview\"");
        var cut = context.Render<FlowResourcePreview>(p => p.Add(c => c.Address, Target("graph-preview"))
            .Add(c => c.Expectation, expectation).Add(c => c.OverviewHref, "/overview").Add(c => c.DefinitionHref, "/definition").Add(c => c.DesignerHref, "/designer"));
        if (change == "match") Assert.HasCount(1, cut.FindComponents<FlowResourceSummary>());
        else
        {
            Assert.IsEmpty(cut.FindComponents<FlowResourceSummary>());
            Assert.AreEqual(ResourceLoadStatus.NotFound, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
            Assert.DoesNotContain("Saved graph-preview", cut.Markup);
        }
    }

    [TestMethod]
    public async Task GraphPreviewDoesNotReadInAnotherActorContext()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Unexpected read."));
        using var context = await ContextAsync(handler);
        var cut = context.Render<FlowResourcePreview>(p => p.Add(c => c.Address, Target("graph-preview"))
            .Add(c => c.Expectation, new ResourcePreviewExpectation(ResourceScopeRef.Instance, Guid.NewGuid()))
            .Add(c => c.OverviewHref, "/overview").Add(c => c.DefinitionHref, "/definition").Add(c => c.DesignerHref, "/designer"));
        Assert.AreEqual(0, handler.Reads);
        Assert.AreEqual(ResourceLoadStatus.Forbidden, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ChangingActorOrRevokingReadPermissionClearsAnOpenGraphPreview(bool changeActor)
    {
        var saved = Resource("graph-preview");
        using var handler = new Handler((_, _) => Task.FromResult(Response(saved)));
        using var context = await ContextAsync(handler);
        var cut = context.Render<FlowResourcePreview>(p => p.Add(c => c.Address, Target("graph-preview"))
            .Add(c => c.Expectation, new ResourcePreviewExpectation(Scope, saved.Uid!.Value))
            .Add(c => c.OverviewHref, "/overview").Add(c => c.DefinitionHref, "/definition").Add(c => c.DesignerHref, "/designer"));
        Assert.HasCount(1, cut.FindComponents<FlowResourceSummary>());
        var provider = (ResourceListContextProvider)context.Services.GetRequiredService<Agentstration.Web.Components.State.IConsoleContextProvider>();
        if (changeActor) provider.Scope = ResourceScopeRef.Instance.Value;
        else provider.Permissions = new HashSet<string>();
        await cut.InvokeAsync(() => context.Services.GetRequiredService<ConsoleContextState>().LoadAsync(CancellationToken.None));
        cut.WaitForAssertion(() => Assert.IsEmpty(cut.FindComponents<FlowResourceSummary>()));
        Assert.DoesNotContain("Saved graph-preview", cut.Markup);
        Assert.AreEqual(1, handler.Reads);
    }

    private static ScopedResourceAddress Target(string name) => ScopedResourceAddress.Create(Scope, ResourceNamespace.Default, FlowResourceKinds.Flow, name);
    private static FlowResponse Resource(string name) => new(name, "Saved " + name, null, "1.0.0", true, "1.0.0",
        new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, "agent")),
        new Dictionary<string, string>(), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)
    { ScopeRef = Scope, Namespace = ResourceNamespace.Default, Uid = Guid.NewGuid() };
    private static HttpResponseMessage Response(FlowResponse resource)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(resource) };
        response.Headers.ETag = new("\"preview\"");
        return response;
    }
    private static IRenderedComponent<FlowResourcePreview> Render(BunitContext context, ScopedResourceAddress item) => context.Render<FlowResourcePreview>(p => p
        .Add(c => c.Address, item).Add(c => c.OverviewHref, "/flows/" + item.Name + "#overview")
        .Add(c => c.DefinitionHref, "/flows/" + item.Name + "#definition/configuration").Add(c => c.DesignerHref, "/flows/" + item.Name + "/designer"));
    private static async Task<BunitContext> ContextAsync(Handler handler, string? selectedScope = null, IReadOnlySet<string>? permissions = null)
    {
        var context = new BunitContext().WithCollectionPresentation().WithResourceListContext(new()
        {
            Scope = selectedScope ?? Scope.Value,
            Permissions = permissions ?? new HashSet<string> { AuthorizationPermissions.ResourcesRead, AuthorizationPermissions.ResourcesWrite, AuthorizationPermissions.RunsExecute }
        });
        context.ComponentFactories.AddStub<FlowTopologyViewer>();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var http = new HttpClient(handler, false) { BaseAddress = new("https://agentstration.test/") };
        context.Services.AddSingleton(http);
        context.Services.AddSingleton<IFlowApiClient>(new FlowApiClient(http));
        await context.Services.GetRequiredService<ConsoleContextState>().EnsureLoadedAsync(CancellationToken.None);
        return context;
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Reads { get; private set; }
        public string? LastPath { get; private set; }
        public CancellationToken LastToken { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Reads++;
            LastPath = request.RequestUri!.PathAndQuery;
            LastToken = cancellationToken;
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.Contains("/api/flows/", LastPath);
            return respond(request, cancellationToken);
        }
    }
}
