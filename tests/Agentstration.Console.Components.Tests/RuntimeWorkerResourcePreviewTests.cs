using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Contracts;
using Agentstration.Web.Components;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class RuntimeWorkerResourcePreviewTests
{
    private static readonly ResourceScopeRef Scope = ResourceScopeRef.Instance;

    [TestMethod]
    [DataRow("en-US", "Identity", "Configuration")]
    [DataRow("fr-FR", "Identité", "Configuration")]
    public async Task PreviewReusesSavedIdentityAndConfigurationWithOneExactRead(string locale, string identity, string configuration)
    {
        using var culture = new TestCultureScope(locale);
        using var handler = new Handler((_, _) => Task.FromResult(Response(Resource("runtime-worker-a"))));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("runtime-worker-a"));
        Assert.AreEqual(1, handler.Reads);
        Assert.AreEqual($"/api/runtime/workers/{WorkerId("runtime-worker-a"):D}/summary", handler.LastPath);
        Assert.HasCount(1, cut.FindComponents<RuntimeWorkerResourceSummary>());
        Assert.HasCount(1, cut.FindComponents<ResourceIdentitySummary>());
        Assert.AreEqual(identity, cut.Find("[data-testid=runtime-worker-identity-summary] h2").TextContent);
        Assert.AreEqual(configuration, cut.Find("[data-testid=runtime-worker-configuration-summary] h2").TextContent);
        Assert.Contains("Saved runtime-worker-a", cut.Find("[data-testid=runtime-worker-identity-summary]").TextContent);
        var identityToggle = cut.Find("[data-testid=runtime-worker-identity-summary] .panel-disclosure");
        Assert.AreEqual("true", identityToggle.GetAttribute("aria-expanded"));
        await identityToggle.ClickAsync(new());
        Assert.AreEqual("false", identityToggle.GetAttribute("aria-expanded"));
        Assert.IsTrue(cut.Find("[data-testid=runtime-worker-identity-summary] .form-section-body").HasAttribute("hidden"));
        await identityToggle.ClickAsync(new());
        Assert.AreEqual("true", identityToggle.GetAttribute("aria-expanded"));
        Assert.Contains("saved-protocol", cut.Find("[data-testid=runtime-worker-configuration-summary]").TextContent);
        Assert.AreEqual("true", cut.Find("[data-testid=runtime-worker-configuration-summary] .panel-disclosure").GetAttribute("aria-expanded"));
        Assert.HasCount(1, cut.FindAll("[data-testid=runtime-worker-preview-open]"));
        Assert.IsEmpty(cut.FindAll("[data-testid=runtime-worker-preview-edit]"));
        Assert.IsEmpty(cut.FindAll("form, [data-testid=runtime-worker-summary-reconcile], .declaration-resolution-grid, .collection-view-switch"));
        cut.Render(p => p.Add(c => c.Address, Target("runtime-worker-a")));
        Assert.AreEqual(1, handler.Reads, "A presentation rerender must not refetch the definition.");
    }

    [TestMethod]
    public async Task WorkerIdMismatchNeverRendersTheOtherEnrollment()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(Resource("runtime-worker-a") with { WorkerId = Guid.NewGuid() })));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("runtime-worker-a"));
        Assert.AreEqual(ResourceLoadStatus.Forbidden, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
        Assert.IsEmpty(cut.FindComponents<RuntimeWorkerResourceSummary>());
        Assert.DoesNotContain("Saved runtime-worker-a", cut.Markup);
    }

    [TestMethod]
    public async Task NonInstanceOwnerDoesNotReadAnEnrollment()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Unexpected owner read."));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("runtime-worker-a") with { ScopeRef = ResourceScopeRef.Workspace(Guid.NewGuid()) });
        Assert.AreEqual(0, handler.Reads);
        Assert.IsEmpty(cut.FindComponents<RuntimeWorkerResourceSummary>());
    }

    [TestMethod]
    [DataRow(403, ResourceLoadStatus.Forbidden)]
    [DataRow(404, ResourceLoadStatus.NotFound)]
    public async Task ExpectedReadFailuresUseSharedFeedbackWithoutLeakingTransportContent(int status, ResourceLoadStatus expected)
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        { Content = JsonContent.Create(new { title = "Private transport detail", status }) }));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("runtime-worker-a"));
        Assert.AreEqual(expected, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
        Assert.DoesNotContain("Private transport detail", cut.Markup);
        Assert.IsEmpty(cut.FindComponents<RuntimeWorkerResourceSummary>());
    }

    [TestMethod]
    public async Task RetryRepeatsOnlyTheSelectedResourceRead()
    {
        var calls = 0;
        using var handler = new Handler((_, _) => Task.FromResult(++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : Response(Resource("runtime-worker-a"))));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("runtime-worker-a"));
        Assert.AreEqual(ResourceLoadStatus.Failed, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
        await cut.Find("[data-testid=runtime-worker-preview-retry]").ClickAsync(new());
        Assert.HasCount(1, cut.FindComponents<RuntimeWorkerResourceSummary>());
        Assert.AreEqual(2, handler.Reads);
    }

    [TestMethod]
    public async Task ReplacingSelectionCancelsAndRejectsThePreviousLateResponse()
    {
        var old = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler((request, _) => request.RequestUri!.AbsolutePath.EndsWith(WorkerId("runtime-worker-a").ToString("D") + "/summary", StringComparison.Ordinal)
            ? old.Task : Task.FromResult(Response(Resource("runtime-worker-b"))));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("runtime-worker-a"));
        var oldToken = handler.LastToken;
        Assert.HasCount(1, cut.FindComponents<LoadingState>());
        cut.Render(p => p.Add(c => c.Address, Target("runtime-worker-b")));
        Assert.IsTrue(oldToken.IsCancellationRequested);
        cut.WaitForAssertion(() => Assert.Contains("Saved runtime-worker-b", cut.Markup));
        var renders = cut.RenderCount;
        old.SetResult(Response(Resource("runtime-worker-a")));
        cut.WaitForState(() => cut.RenderCount > renders);
        Assert.Contains("Saved runtime-worker-b", cut.Markup);
        Assert.DoesNotContain("Saved runtime-worker-a", cut.Markup);
    }

    [TestMethod]
    public async Task ClosingCancelsThePendingRead()
    {
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler((_, _) => response.Task);
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("runtime-worker-a"));
        var token = handler.LastToken;
        await context.DisposeComponentsAsync();
        Assert.IsTrue(token.IsCancellationRequested);
        response.SetResult(Response(Resource("runtime-worker-a")));
    }

    [TestMethod]
    public async Task MissingReadPermissionPerformsNoRequest()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Unexpected read."));
        using var context = await ContextAsync(handler, permissions: new HashSet<string>());
        var cut = Render(context, Target("runtime-worker-a"));
        Assert.AreEqual(0, handler.Reads);
        Assert.AreEqual(ResourceLoadStatus.Forbidden, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
    }

    [TestMethod]
    public async Task MissingOwnerNeverFallsBackToAnUnscopedRead()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Unexpected unscoped read."));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("runtime-worker-a") with { ScopeRef = default });
        Assert.AreEqual(0, handler.Reads);
        Assert.AreEqual(ResourceLoadStatus.Failed, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
    }

    [TestMethod]
    public async Task NamespacedAddressNeverFallsBackToTheDefaultNamespace()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Unexpected default read."));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("runtime-worker-a") with { Namespace = ResourceNamespace.Parse("pack-a") });
        Assert.AreEqual(0, handler.Reads);
        Assert.AreEqual(ResourceLoadStatus.Failed, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
    }

    private static ScopedResourceAddress Target(string name) => ScopedResourceAddress.Create(Scope, ResourceNamespace.Default, "AwpWorkerIdentity", WorkerId(name).ToString("N"));
    private static Guid WorkerId(string name) => name.EndsWith("-a", StringComparison.Ordinal) ? Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001") : Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static RuntimeWorkerSummaryResponse Resource(string name) => new(WorkerId(name), "Saved " + name, "saved-protocol", "Pending", RuntimeWorkerPresenceState.Unknown, null, "1.0.0", 2, 0, DateTimeOffset.UnixEpoch, null, []);
    private static HttpResponseMessage Response(RuntimeWorkerSummaryResponse resource)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(resource) };
        response.Headers.ETag = new("\"preview\"");
        return response;
    }
    private static IRenderedComponent<RuntimeWorkerResourcePreview> Render(BunitContext context, ScopedResourceAddress item) => context.Render<RuntimeWorkerResourcePreview>(p => p
        .Add(c => c.Address, item).Add(c => c.OverviewHref, "/parameters/" + item.Name + "#overview")
        .Add(c => c.DefinitionHref, "/parameters/" + item.Name + "#definition/configuration"));
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
        context.Services.AddSingleton<IRuntimeApiClient>(new RuntimeApiClient(http));
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
            Assert.Contains("/api/runtime/workers/", LastPath);
            return respond(request, cancellationToken);
        }
    }
}
