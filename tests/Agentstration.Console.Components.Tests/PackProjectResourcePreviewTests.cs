using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Identity.Contracts;
using Agentstration.Packs.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class PackProjectResourcePreviewTests
{
    private static readonly ResourceScopeRef Scope = ResourceScopeRef.Workspace(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001"));

    [TestMethod]
    [DataRow("en-US", "Identity", "Configuration")]
    [DataRow("fr-FR", "Identité", "Configuration")]
    public async Task PreviewReusesSavedIdentityAndConfigurationWithOneExactRead(string locale, string identity, string configuration)
    {
        using var culture = new TestCultureScope(locale);
        using var handler = new Handler((_, _) => Task.FromResult(Response(Resource("pack-project-a"))));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("pack-project-a"));
        Assert.AreEqual(1, handler.Reads);
        Assert.AreEqual($"/api/pack-projects/resources/{ProjectId("pack-project-a"):N}?scopeRef={Uri.EscapeDataString(Scope.Value)}&namespace=default", handler.LastPath);
        Assert.HasCount(1, cut.FindComponents<PackProjectResourceSummary>());
        Assert.HasCount(1, cut.FindComponents<ResourceIdentitySummary>());
        Assert.AreEqual(identity, cut.Find("[data-testid=pack-project-identity-summary] h2").TextContent);
        Assert.AreEqual(configuration, cut.Find("[data-testid=pack-project-configuration-summary] h2").TextContent);
        Assert.Contains("Saved pack-project-a", cut.Find("[data-testid=pack-project-identity-summary]").TextContent);
        var identityToggle = cut.Find("[data-testid=pack-project-identity-summary] .panel-disclosure");
        Assert.AreEqual("true", identityToggle.GetAttribute("aria-expanded"));
        await identityToggle.ClickAsync(new());
        Assert.AreEqual("false", identityToggle.GetAttribute("aria-expanded"));
        Assert.IsTrue(cut.Find("[data-testid=pack-project-identity-summary] .form-section-body").HasAttribute("hidden"));
        await identityToggle.ClickAsync(new());
        Assert.AreEqual("true", identityToggle.GetAttribute("aria-expanded"));
        Assert.Contains("preview", cut.Find("[data-testid=pack-project-configuration-summary]").TextContent);
        Assert.AreEqual("true", cut.Find("[data-testid=pack-project-configuration-summary] .panel-disclosure").GetAttribute("aria-expanded"));
        Assert.HasCount(1, cut.FindAll("[data-testid=pack-project-preview-open]"));
        Assert.HasCount(1, cut.FindAll("[data-testid=pack-project-preview-edit]"));
        Assert.AreEqual(locale == "fr-FR" ? "Modifier la définition" : "Edit definition", cut.Find("[data-testid=pack-project-preview-edit]").TextContent.Trim());
        Assert.IsEmpty(cut.FindAll("form, [data-testid=pack-project-summary-reconcile], .declaration-resolution-grid, .collection-view-switch"));
        cut.Render(p => p.Add(c => c.Address, Target("pack-project-a")));
        Assert.AreEqual(1, handler.Reads, "A presentation rerender must not refetch the definition.");
    }

    [TestMethod]
    public async Task OwnerMismatchNeverRendersTheOtherWorkspacesResource()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(Resource("pack-project-a") with { ScopeRef = ResourceScopeRef.Instance })));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("pack-project-a"));
        Assert.AreEqual(ResourceLoadStatus.Forbidden, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
        Assert.IsEmpty(cut.FindComponents<PackProjectResourceSummary>());
        Assert.DoesNotContain("Saved pack-project-a", cut.Markup);
    }

    [TestMethod]
    public async Task InheritedResourceOffersInspectionWithoutAnEditShortcut()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(Resource("pack-project-a"))));
        using var context = await ContextAsync(handler, selectedScope: "/instance");
        var cut = Render(context, Target("pack-project-a"));
        Assert.HasCount(1, cut.FindAll("[data-testid=pack-project-preview-open]"));
        Assert.IsEmpty(cut.FindAll("[data-testid=pack-project-preview-edit], [data-testid=pack-project-preview-designer]"));
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
        var cut = Render(context, Target("pack-project-a"));
        Assert.AreEqual(expected, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
        Assert.DoesNotContain("Private transport detail", cut.Markup);
        Assert.IsEmpty(cut.FindComponents<PackProjectResourceSummary>());
    }

    [TestMethod]
    public async Task RetryRepeatsOnlyTheSelectedResourceRead()
    {
        var calls = 0;
        using var handler = new Handler((_, _) => Task.FromResult(++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : Response(Resource("pack-project-a"))));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("pack-project-a"));
        Assert.AreEqual(ResourceLoadStatus.Failed, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
        await cut.Find("[data-testid=pack-project-preview-retry]").ClickAsync(new());
        Assert.HasCount(1, cut.FindComponents<PackProjectResourceSummary>());
        Assert.AreEqual(2, handler.Reads);
    }

    [TestMethod]
    public async Task ReplacingSelectionCancelsAndRejectsThePreviousLateResponse()
    {
        var old = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler((request, _) => request.RequestUri!.AbsolutePath.EndsWith(ProjectId("pack-project-a").ToString("N"), StringComparison.Ordinal)
            ? old.Task : Task.FromResult(Response(Resource("pack-project-b"))));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("pack-project-a"));
        var oldToken = handler.LastToken;
        Assert.HasCount(1, cut.FindComponents<LoadingState>());
        cut.Render(p => p.Add(c => c.Address, Target("pack-project-b")));
        Assert.IsTrue(oldToken.IsCancellationRequested);
        cut.WaitForAssertion(() => Assert.Contains("Saved pack-project-b", cut.Markup));
        var renders = cut.RenderCount;
        old.SetResult(Response(Resource("pack-project-a")));
        cut.WaitForState(() => cut.RenderCount > renders);
        Assert.Contains("Saved pack-project-b", cut.Markup);
        Assert.DoesNotContain("Saved pack-project-a", cut.Markup);
    }

    [TestMethod]
    public async Task ClosingCancelsThePendingRead()
    {
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler((_, _) => response.Task);
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("pack-project-a"));
        var token = handler.LastToken;
        await context.DisposeComponentsAsync();
        Assert.IsTrue(token.IsCancellationRequested);
        response.SetResult(Response(Resource("pack-project-a")));
    }

    [TestMethod]
    public async Task MissingReadPermissionPerformsNoRequest()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Unexpected read."));
        using var context = await ContextAsync(handler, permissions: new HashSet<string>());
        var cut = Render(context, Target("pack-project-a"));
        Assert.AreEqual(0, handler.Reads);
        Assert.AreEqual(ResourceLoadStatus.Forbidden, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
    }

    [TestMethod]
    public async Task MissingOwnerNeverFallsBackToAnUnscopedRead()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Unexpected unscoped read."));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("pack-project-a") with { ScopeRef = default });
        Assert.AreEqual(0, handler.Reads);
        Assert.AreEqual(ResourceLoadStatus.Failed, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
    }

    [TestMethod]
    public async Task NamespacedAddressNeverFallsBackToTheDefaultNamespace()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Unexpected default read."));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("pack-project-a") with { Namespace = ResourceNamespace.Parse("pack-a") });
        Assert.AreEqual(0, handler.Reads);
        Assert.AreEqual(ResourceLoadStatus.Failed, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
    }

    private static ScopedResourceAddress Target(string name) => ScopedResourceAddress.Create(Scope, ResourceNamespace.Default, PackAuthoringKinds.PackProject, ProjectId(name).ToString("N"));
    private static Guid ProjectId(string name) => name.EndsWith("-a", StringComparison.Ordinal) ? Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001") : Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static PackProjectResource Resource(string name) => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = PackAuthoringKinds.PackProject,
        ScopeRef = Scope,
        Uid = Guid.NewGuid(),
        Metadata = new() { Name = ProjectId(name).ToString("N") },
        Definition = new() { Publisher = "preview", PackName = name, Version = "1.0.0-dev.1", DisplayName = "Saved " + name, SourceArtifact = new("fixture", "saved-source-hash", 0, "fixture.zip"), CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch }
    };
    private static HttpResponseMessage Response(PackProjectResource resource)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(resource) };
        response.Headers.ETag = new("\"preview\"");
        return response;
    }
    private static IRenderedComponent<PackProjectResourcePreview> Render(BunitContext context, ScopedResourceAddress item) => context.Render<PackProjectResourcePreview>(p => p
        .Add(c => c.Address, item).Add(c => c.OverviewHref, "/pack-projects/" + item.Name + "#overview")
        .Add(c => c.DefinitionHref, "/pack-projects/" + item.Name + "#definition/configuration"));
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
        context.Services.AddSingleton<IPacksClient>(new PacksApiClient(http));
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
            Assert.Contains("/api/pack-projects/", LastPath);
            return respond(request, cancellationToken);
        }
    }
}
