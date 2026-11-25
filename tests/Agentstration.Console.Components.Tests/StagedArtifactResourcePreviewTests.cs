using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Artifacts.Contracts;

using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;

using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class StagedArtifactResourcePreviewTests
{
    private static readonly ResourceScopeRef Scope = ResourceScopeRef.Workspace(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001"));

    [TestMethod]
    [DataRow("en-US", "Identity", "Configuration")]
    [DataRow("fr-FR", "Identité", "Configuration")]
    public async Task PreviewReusesSavedIdentityAndConfigurationWithOneExactRead(string locale, string identity, string configuration)
    {
        using var culture = new TestCultureScope(locale);
        using var handler = new Handler((_, _) => Task.FromResult(Response(Resource("11111111111111111111111111111111"))));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("11111111111111111111111111111111"));
        Assert.AreEqual(1, handler.Reads);
        Assert.AreEqual($"/api/artifacts/staged/11111111111111111111111111111111/summary?scopeRef={Uri.EscapeDataString(Scope.Value)}&namespace=default", handler.LastPath);
        Assert.HasCount(1, cut.FindComponents<StagedArtifactResourceSummary>());
        Assert.HasCount(1, cut.FindComponents<ResourceIdentitySummary>());
        Assert.AreEqual(identity, cut.Find("[data-testid=staged-artifact-identity-summary] h2").TextContent);
        Assert.AreEqual(configuration, cut.Find("[data-testid=staged-artifact-configuration-summary] h2").TextContent);
        Assert.Contains("Saved 11111111111111111111111111111111", cut.Find("[data-testid=staged-artifact-identity-summary]").TextContent);
        var identityToggle = cut.Find("[data-testid=staged-artifact-identity-summary] .panel-disclosure");
        Assert.AreEqual("true", identityToggle.GetAttribute("aria-expanded"));
        await identityToggle.ClickAsync(new());
        Assert.AreEqual("false", identityToggle.GetAttribute("aria-expanded"));
        Assert.IsTrue(cut.Find("[data-testid=staged-artifact-identity-summary] .form-section-body").HasAttribute("hidden"));
        await identityToggle.ClickAsync(new());
        Assert.AreEqual("true", identityToggle.GetAttribute("aria-expanded"));
        Assert.DoesNotContain("private-backend-locator", cut.Markup);
        Assert.Contains("saved-value", cut.Find("[data-testid=staged-artifact-configuration-summary]").TextContent);
        Assert.AreEqual("true", cut.Find("[data-testid=staged-artifact-configuration-summary] .panel-disclosure").GetAttribute("aria-expanded"));
        Assert.HasCount(1, cut.FindAll("[data-testid=staged-artifact-preview-open]"));
        Assert.IsEmpty(cut.FindAll("[data-testid=staged-artifact-preview-edit]"));
        Assert.IsEmpty(cut.FindAll("form, [data-testid=parameter-summary-reconcile], .declaration-resolution-grid, .collection-view-switch"));
        cut.Render(p => p.Add(c => c.Address, Target("11111111111111111111111111111111")));
        Assert.AreEqual(1, handler.Reads, "A presentation rerender must not refetch the definition.");
    }

    [TestMethod]
    public async Task OwnerMismatchNeverRendersTheOtherWorkspacesResource()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(Resource("11111111111111111111111111111111") with { ScopeRef = ResourceScopeRef.Instance })));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("11111111111111111111111111111111"));
        Assert.AreEqual(ResourceLoadStatus.Forbidden, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
        Assert.IsEmpty(cut.FindComponents<StagedArtifactResourceSummary>());
        Assert.DoesNotContain("Saved 11111111111111111111111111111111", cut.Markup);
    }

    [TestMethod]
    public async Task InheritedResourceOffersInspectionWithoutAnEditShortcut()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(Resource("11111111111111111111111111111111"))));
        using var context = await ContextAsync(handler, selectedScope: "/instance");
        var cut = Render(context, Target("11111111111111111111111111111111"));
        Assert.HasCount(1, cut.FindAll("[data-testid=staged-artifact-preview-open]"));
        Assert.IsEmpty(cut.FindAll("[data-testid=staged-artifact-preview-edit], [data-testid=staged-artifact-preview-designer]"));
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
        var cut = Render(context, Target("11111111111111111111111111111111"));
        Assert.AreEqual(expected, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
        Assert.DoesNotContain("Private transport detail", cut.Markup);
        Assert.IsEmpty(cut.FindComponents<StagedArtifactResourceSummary>());
    }

    [TestMethod]
    public async Task RetryRepeatsOnlyTheSelectedResourceRead()
    {
        var calls = 0;
        using var handler = new Handler((_, _) => Task.FromResult(++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : Response(Resource("11111111111111111111111111111111"))));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("11111111111111111111111111111111"));
        Assert.AreEqual(ResourceLoadStatus.Failed, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
        await cut.Find("[data-testid=staged-artifact-preview-retry]").ClickAsync(new());
        Assert.HasCount(1, cut.FindComponents<StagedArtifactResourceSummary>());
        Assert.AreEqual(2, handler.Reads);
    }

    [TestMethod]
    public async Task ReplacingSelectionCancelsAndRejectsThePreviousLateResponse()
    {
        var old = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler((request, _) => request.RequestUri!.AbsolutePath.Contains("11111111111111111111111111111111", StringComparison.Ordinal)
            ? old.Task : Task.FromResult(Response(Resource("22222222222222222222222222222222"))));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("11111111111111111111111111111111"));
        var oldToken = handler.LastToken;
        Assert.HasCount(1, cut.FindComponents<LoadingState>());
        cut.Render(p => p.Add(c => c.Address, Target("22222222222222222222222222222222")));
        Assert.IsTrue(oldToken.IsCancellationRequested);
        cut.WaitForAssertion(() => Assert.Contains("Saved 22222222222222222222222222222222", cut.Markup));
        var renders = cut.RenderCount;
        old.SetResult(Response(Resource("11111111111111111111111111111111")));
        cut.WaitForState(() => cut.RenderCount > renders);
        Assert.Contains("Saved 22222222222222222222222222222222", cut.Markup);
        Assert.DoesNotContain("Saved 11111111111111111111111111111111", cut.Markup);
    }

    [TestMethod]
    public async Task ClosingCancelsThePendingRead()
    {
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler((_, _) => response.Task);
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("11111111111111111111111111111111"));
        var token = handler.LastToken;
        await context.DisposeComponentsAsync();
        Assert.IsTrue(token.IsCancellationRequested);
        response.SetResult(Response(Resource("11111111111111111111111111111111")));
    }

    [TestMethod]
    public async Task MissingReadPermissionPerformsNoRequest()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Unexpected read."));
        using var context = await ContextAsync(handler, permissions: new HashSet<string>());
        var cut = Render(context, Target("11111111111111111111111111111111"));
        Assert.AreEqual(0, handler.Reads);
        Assert.AreEqual(ResourceLoadStatus.Forbidden, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
    }

    [TestMethod]
    public async Task MissingOwnerNeverFallsBackToAnUnscopedRead()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Unexpected unscoped read."));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("11111111111111111111111111111111") with { ScopeRef = default });
        Assert.AreEqual(0, handler.Reads);
        Assert.AreEqual(ResourceLoadStatus.Failed, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
    }

    [TestMethod]
    public async Task NamespacedAddressNeverFallsBackToTheDefaultNamespace()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Unexpected default read."));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("11111111111111111111111111111111") with { Namespace = ResourceNamespace.Parse("pack-a") });
        Assert.AreEqual(0, handler.Reads);
        Assert.AreEqual(ResourceLoadStatus.Failed, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
    }

    [TestMethod]
    public async Task MalformedArtifactIdentifierNeverStartsARead()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Unexpected metadata read."));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("not-an-artifact-id"));
        Assert.AreEqual(0, handler.Reads);
        Assert.AreEqual(ResourceLoadStatus.Failed, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
    }

    private static ScopedResourceAddress Target(string name) => ScopedResourceAddress.Create(Scope, ResourceNamespace.Default, ArtifactResourceKinds.StagedArtifact, name);
    private static StagedArtifactSavedSummary Resource(string name) => new(new() { Name = name }, Guid.NewGuid(), Scope, 1, ResourceGovernance.User,
        new(StagedArtifactId.Parse(name), null, "Saved " + name, "saved-value", 12, "sha256", StagedArtifactStatus.Sealed,
            new() { Kind = ArtifactProducerKind.FlowRun, Id = "producer" }, "default", ResourceNamespace.Default, "filesystem", ResourceNamespace.Default,
            "1.0.0", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), null, null, null, null, []));
    private static HttpResponseMessage Response(StagedArtifactSavedSummary resource)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(resource) };
        response.Headers.ETag = new("\"preview\"");
        return response;
    }
    private static IRenderedComponent<StagedArtifactResourcePreview> Render(BunitContext context, ScopedResourceAddress item) => context.Render<StagedArtifactResourcePreview>(p => p
        .Add(c => c.Address, item).Add(c => c.OverviewHref, "/parameters/" + item.Name + "#overview")
        );
    private static async Task<BunitContext> ContextAsync(Handler handler, string? selectedScope = null, IReadOnlySet<string>? permissions = null)
    {
        var context = new BunitContext().WithCollectionPresentation().WithResourceListContext(new()
        {
            Scope = selectedScope ?? Scope.Value,
            Permissions = permissions ?? new HashSet<string> { AuthorizationPermissions.ArtifactsInspect, AuthorizationPermissions.ResourcesRead, AuthorizationPermissions.ResourcesWrite, AuthorizationPermissions.RunsExecute }
        });
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var http = new HttpClient(handler, false) { BaseAddress = new("https://agentstration.test/") };
        context.Services.AddSingleton(http);
        context.Services.AddSingleton<IArtifactsClient>(new ArtifactsApiClient(http));
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
            Assert.Contains("/api/artifacts/", LastPath);
            return respond(request, cancellationToken);
        }
    }
}
