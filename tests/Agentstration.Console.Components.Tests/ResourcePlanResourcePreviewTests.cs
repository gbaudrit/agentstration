using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Identity.Contracts;
using Agentstration.ResourcePlanning.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class ResourcePlanResourcePreviewTests
{
    private static readonly ResourceScopeRef Scope = ResourceScopeRef.Workspace(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001"));

    [TestMethod]
    [DataRow("en-US", "Identity", "Configuration")]
    [DataRow("fr-FR", "Identité", "Configuration")]
    public async Task PreviewReusesSavedIdentityAndConfigurationWithOneExactRead(string locale, string identity, string configuration)
    {
        using var culture = new TestCultureScope(locale);
        using var handler = new Handler((_, _) => Task.FromResult(Response(Resource("resource-plan-a"))));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("resource-plan-a"));
        Assert.AreEqual(1, handler.Reads);
        Assert.AreEqual($"/api/resource-plans/{PlanId("resource-plan-a"):D}?scopeRef={Uri.EscapeDataString(Scope.Value)}&namespace=default", handler.LastPath);
        Assert.HasCount(1, cut.FindComponents<ResourcePlanResourceSummary>());
        Assert.HasCount(1, cut.FindComponents<ResourceIdentitySummary>());
        Assert.AreEqual(identity, cut.Find("[data-testid=resource-plan-identity-summary] h2").TextContent);
        Assert.AreEqual(configuration, cut.Find("[data-testid=resource-plan-configuration-summary] h2").TextContent);
        Assert.Contains("Saved resource-plan-a", cut.Find("[data-testid=resource-plan-identity-summary]").TextContent);
        var identityToggle = cut.Find("[data-testid=resource-plan-identity-summary] .panel-disclosure");
        Assert.AreEqual("true", identityToggle.GetAttribute("aria-expanded"));
        await identityToggle.ClickAsync(new());
        Assert.AreEqual("false", identityToggle.GetAttribute("aria-expanded"));
        Assert.IsTrue(cut.Find("[data-testid=resource-plan-identity-summary] .form-section-body").HasAttribute("hidden"));
        await identityToggle.ClickAsync(new());
        Assert.AreEqual("true", identityToggle.GetAttribute("aria-expanded"));
        Assert.Contains("saved-goal", cut.Find("[data-testid=resource-plan-configuration-summary]").TextContent);
        Assert.AreEqual("true", cut.Find("[data-testid=resource-plan-configuration-summary] .panel-disclosure").GetAttribute("aria-expanded"));
        Assert.HasCount(1, cut.FindAll("[data-testid=resource-plan-preview-open]"));
        Assert.IsEmpty(cut.FindAll("[data-testid=resource-plan-preview-edit]"));
        Assert.IsEmpty(cut.FindAll("form, [data-testid=resource-plan-summary-reconcile], .declaration-resolution-grid, .collection-view-switch"));
        cut.Render(p => p.Add(c => c.Address, Target("resource-plan-a")));
        Assert.AreEqual(1, handler.Reads, "A presentation rerender must not refetch the definition.");
    }

    [TestMethod]
    public async Task OwnerMismatchNeverRendersTheOtherWorkspacesResource()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(Resource("resource-plan-a") with { Scope = new(Guid.Empty, new(Guid.NewGuid())) })));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("resource-plan-a"));
        Assert.AreEqual(ResourceLoadStatus.Forbidden, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
        Assert.IsEmpty(cut.FindComponents<ResourcePlanResourceSummary>());
        Assert.DoesNotContain("Saved resource-plan-a", cut.Markup);
    }

    [TestMethod]
    public async Task InheritedResourceOffersInspectionWithoutAnEditShortcut()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(Resource("resource-plan-a"))));
        using var context = await ContextAsync(handler, selectedScope: "/instance");
        var cut = Render(context, Target("resource-plan-a"));
        Assert.HasCount(1, cut.FindAll("[data-testid=resource-plan-preview-open]"));
        Assert.IsEmpty(cut.FindAll("[data-testid=resource-plan-preview-edit], [data-testid=resource-plan-preview-designer]"));
        Assert.IsEmpty(cut.FindComponents<ManagedResourceBadge>(), "Native Plans do not have Core Resource governance fields.");
    }

    [TestMethod]
    [DataRow(403, ResourceLoadStatus.Forbidden)]
    [DataRow(404, ResourceLoadStatus.NotFound)]
    public async Task ExpectedReadFailuresUseSharedFeedbackWithoutLeakingTransportContent(int status, ResourceLoadStatus expected)
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        { Content = JsonContent.Create(new { title = "Private transport detail", status }) }));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("resource-plan-a"));
        Assert.AreEqual(expected, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
        Assert.DoesNotContain("Private transport detail", cut.Markup);
        Assert.IsEmpty(cut.FindComponents<ResourcePlanResourceSummary>());
    }

    [TestMethod]
    public async Task RetryRepeatsOnlyTheSelectedResourceRead()
    {
        var calls = 0;
        using var handler = new Handler((_, _) => Task.FromResult(++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : Response(Resource("resource-plan-a"))));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("resource-plan-a"));
        Assert.AreEqual(ResourceLoadStatus.Failed, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
        await cut.Find("[data-testid=resource-plan-preview-retry]").ClickAsync(new());
        Assert.HasCount(1, cut.FindComponents<ResourcePlanResourceSummary>());
        Assert.AreEqual(2, handler.Reads);
    }

    [TestMethod]
    public async Task ReplacingSelectionCancelsAndRejectsThePreviousLateResponse()
    {
        var old = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler((request, _) => request.RequestUri!.AbsolutePath.EndsWith(PlanId("resource-plan-a").ToString("D"), StringComparison.Ordinal)
            ? old.Task : Task.FromResult(Response(Resource("resource-plan-b"))));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("resource-plan-a"));
        var oldToken = handler.LastToken;
        Assert.HasCount(1, cut.FindComponents<LoadingState>());
        cut.Render(p => p.Add(c => c.Address, Target("resource-plan-b")));
        Assert.IsTrue(oldToken.IsCancellationRequested);
        cut.WaitForAssertion(() => Assert.Contains("Saved resource-plan-b", cut.Markup));
        var renders = cut.RenderCount;
        old.SetResult(Response(Resource("resource-plan-a")));
        cut.WaitForState(() => cut.RenderCount > renders);
        Assert.Contains("Saved resource-plan-b", cut.Markup);
        Assert.DoesNotContain("Saved resource-plan-a", cut.Markup);
    }

    [TestMethod]
    public async Task ClosingCancelsThePendingRead()
    {
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler((_, _) => response.Task);
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("resource-plan-a"));
        var token = handler.LastToken;
        await context.DisposeComponentsAsync();
        Assert.IsTrue(token.IsCancellationRequested);
        response.SetResult(Response(Resource("resource-plan-a")));
    }

    [TestMethod]
    public async Task MissingReadPermissionPerformsNoRequest()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Unexpected read."));
        using var context = await ContextAsync(handler, permissions: new HashSet<string>());
        var cut = Render(context, Target("resource-plan-a"));
        Assert.AreEqual(0, handler.Reads);
        Assert.AreEqual(ResourceLoadStatus.Forbidden, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
    }

    [TestMethod]
    public async Task MissingOwnerNeverFallsBackToAnUnscopedRead()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Unexpected unscoped read."));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("resource-plan-a") with { ScopeRef = default });
        Assert.AreEqual(0, handler.Reads);
        Assert.AreEqual(ResourceLoadStatus.Failed, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
    }

    [TestMethod]
    public async Task NamespacedAddressNeverFallsBackToTheDefaultNamespace()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Unexpected default read."));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("resource-plan-a") with { Namespace = ResourceNamespace.Parse("pack-a") });
        Assert.AreEqual(0, handler.Reads);
        Assert.AreEqual(ResourceLoadStatus.Failed, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
    }

    private static ScopedResourceAddress Target(string name) => ScopedResourceAddress.Create(Scope, ResourceNamespace.Default, "ResourcePlan", PlanId(name).ToString("N"));
    private static Guid PlanId(string name) => name.EndsWith("-a", StringComparison.Ordinal) ? Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001") : Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static ResourcePlan Resource(string name) => new(new(PlanId(name)), new(Guid.Empty, new(Scope.TargetId!.Value)), "Saved " + name, "saved-goal", null,
        new("v1", JsonSerializer.SerializeToElement(new { })), ResourcePlanStatus.Ready, 2, new(Guid.Empty), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
    private static HttpResponseMessage Response(ResourcePlan resource)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(resource) };
        response.Headers.ETag = new("\"preview\"");
        return response;
    }
    private static IRenderedComponent<ResourcePlanResourcePreview> Render(BunitContext context, ScopedResourceAddress item) => context.Render<ResourcePlanResourcePreview>(p => p
        .Add(c => c.Address, item).Add(c => c.OverviewHref, "/resource-plans/" + item.Name + "#overview")
        .Add(c => c.DefinitionHref, "/resource-plans/" + item.Name + "#definition/configuration"));
    private static async Task<BunitContext> ContextAsync(Handler handler, string? selectedScope = null, IReadOnlySet<string>? permissions = null)
    {
        var context = new BunitContext().WithCollectionPresentation().WithResourceListContext(new()
        {
            Scope = selectedScope ?? Scope.Value,
            Permissions = permissions ?? new HashSet<string> { AuthorizationPermissions.ResourcesRead, AuthorizationPermissions.ResourcesWrite, AuthorizationPermissions.RunsExecute }
        });
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var http = new HttpClient(handler, false) { BaseAddress = new("https://agentstration.test/") };
        context.Services.AddSingleton(http);
        context.Services.AddSingleton<IResourcePlansApiClient>(new ResourcePlansApiClient(http));
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
            Assert.Contains("/api/resource-plans/", LastPath);
            return respond(request, cancellationToken);
        }
    }
}
