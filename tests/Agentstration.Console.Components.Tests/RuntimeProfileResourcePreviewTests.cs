using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Contracts;
using Agentstration.Web.Components;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class RuntimeProfileResourcePreviewTests
{
    private static readonly ResourceScopeRef Scope = ResourceScopeRef.Instance;

    [TestMethod]
    [DataRow("en-US", "Identity", "Configuration")]
    [DataRow("fr-FR", "Identité", "Configuration")]
    public async Task PreviewReusesSavedIdentityAndConfigurationWithOneExactRead(string locale, string identity, string configuration)
    {
        using var culture = new TestCultureScope(locale);
        using var handler = new Handler((_, _) => Task.FromResult(Response(Resource("runtime-profile-a"))));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("runtime-profile-a"));
        Assert.AreEqual(1, handler.Reads);
        Assert.AreEqual($"/api/runtimeprofiles/runtime-profile-a?resourceNamespace=default&scopeRef={Uri.EscapeDataString(Scope.Value)}", handler.LastPath);
        Assert.HasCount(1, cut.FindComponents<RuntimeProfileResourceSummary>());
        Assert.HasCount(1, cut.FindComponents<ResourceIdentitySummary>());
        Assert.AreEqual(identity, cut.Find("[data-testid=runtime-profile-identity-summary] h2").TextContent);
        Assert.AreEqual(configuration, cut.Find("[data-testid=runtime-profile-configuration-summary] h2").TextContent);
        Assert.Contains("Saved runtime-profile-a", cut.Find("[data-testid=runtime-profile-identity-summary]").TextContent);
        var identityToggle = cut.Find("[data-testid=runtime-profile-identity-summary] .panel-disclosure");
        Assert.AreEqual("true", identityToggle.GetAttribute("aria-expanded"));
        await identityToggle.ClickAsync(new());
        Assert.AreEqual("false", identityToggle.GetAttribute("aria-expanded"));
        Assert.IsTrue(cut.Find("[data-testid=runtime-profile-identity-summary] .form-section-body").HasAttribute("hidden"));
        await identityToggle.ClickAsync(new());
        Assert.AreEqual("true", identityToggle.GetAttribute("aria-expanded"));
        Assert.Contains("microsoft-agent-framework", cut.Find("[data-testid=runtime-profile-configuration-summary]").TextContent);
        Assert.AreEqual("true", cut.Find("[data-testid=runtime-profile-configuration-summary] .panel-disclosure").GetAttribute("aria-expanded"));
        Assert.HasCount(1, cut.FindAll("[data-testid=runtime-profile-preview-open]"));
        Assert.HasCount(1, cut.FindAll("[data-testid=runtime-profile-preview-edit]"));
        Assert.AreEqual(locale == "fr-FR" ? "Modifier la définition" : "Edit definition", cut.Find("[data-testid=runtime-profile-preview-edit]").TextContent.Trim());
        Assert.IsEmpty(cut.FindAll("form, [data-testid=runtime-profile-summary-reconcile], .declaration-resolution-grid, .collection-view-switch"));
        cut.Render(p => p.Add(c => c.Address, Target("runtime-profile-a")));
        Assert.AreEqual(1, handler.Reads, "A presentation rerender must not refetch the definition.");
    }

    [TestMethod]
    public async Task OwnerMismatchNeverRendersTheOtherWorkspacesResource()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(Resource("runtime-profile-a") with { ScopeRef = ResourceScopeRef.Tenant(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001")) })));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("runtime-profile-a"));
        Assert.AreEqual(ResourceLoadStatus.Forbidden, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
        Assert.IsEmpty(cut.FindComponents<RuntimeProfileResourceSummary>());
        Assert.DoesNotContain("Saved runtime-profile-a", cut.Markup);
    }

    [TestMethod]
    public async Task InheritedResourceOffersInspectionWithoutAnEditShortcut()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(Resource("runtime-profile-a"))));
        using var context = await ContextAsync(handler, selectedScope: "/workspaces/bbbbbbbb-0000-0000-0000-000000000001");
        var cut = Render(context, Target("runtime-profile-a"));
        Assert.HasCount(1, cut.FindAll("[data-testid=runtime-profile-preview-open]"));
        Assert.IsEmpty(cut.FindAll("[data-testid=runtime-profile-preview-edit], [data-testid=runtime-profile-preview-designer]"));
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
        var cut = Render(context, Target("runtime-profile-a"));
        Assert.AreEqual(expected, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
        Assert.DoesNotContain("Private transport detail", cut.Markup);
        Assert.IsEmpty(cut.FindComponents<RuntimeProfileResourceSummary>());
    }

    [TestMethod]
    public async Task RetryRepeatsOnlyTheSelectedResourceRead()
    {
        var calls = 0;
        using var handler = new Handler((_, _) => Task.FromResult(++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : Response(Resource("runtime-profile-a"))));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("runtime-profile-a"));
        Assert.AreEqual(ResourceLoadStatus.Failed, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
        await cut.Find("[data-testid=runtime-profile-preview-retry]").ClickAsync(new());
        Assert.HasCount(1, cut.FindComponents<RuntimeProfileResourceSummary>());
        Assert.AreEqual(2, handler.Reads);
    }

    [TestMethod]
    public async Task ReplacingSelectionCancelsAndRejectsThePreviousLateResponse()
    {
        var old = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler((request, _) => request.RequestUri!.AbsolutePath.EndsWith("runtime-profile-a", StringComparison.Ordinal)
            ? old.Task : Task.FromResult(Response(Resource("runtime-profile-b"))));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("runtime-profile-a"));
        var oldToken = handler.LastToken;
        Assert.HasCount(1, cut.FindComponents<LoadingState>());
        cut.Render(p => p.Add(c => c.Address, Target("runtime-profile-b")));
        Assert.IsTrue(oldToken.IsCancellationRequested);
        cut.WaitForAssertion(() => Assert.Contains("Saved runtime-profile-b", cut.Markup));
        var renders = cut.RenderCount;
        old.SetResult(Response(Resource("runtime-profile-a")));
        cut.WaitForState(() => cut.RenderCount > renders);
        Assert.Contains("Saved runtime-profile-b", cut.Markup);
        Assert.DoesNotContain("Saved runtime-profile-a", cut.Markup);
    }

    [TestMethod]
    public async Task ClosingCancelsThePendingRead()
    {
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler((_, _) => response.Task);
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("runtime-profile-a"));
        var token = handler.LastToken;
        await context.DisposeComponentsAsync();
        Assert.IsTrue(token.IsCancellationRequested);
        response.SetResult(Response(Resource("runtime-profile-a")));
    }

    [TestMethod]
    public async Task MissingReadPermissionPerformsNoRequest()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Unexpected read."));
        using var context = await ContextAsync(handler, permissions: new HashSet<string>());
        var cut = Render(context, Target("runtime-profile-a"));
        Assert.AreEqual(0, handler.Reads);
        Assert.AreEqual(ResourceLoadStatus.Forbidden, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
    }

    [TestMethod]
    public async Task MissingOwnerNeverFallsBackToAnUnscopedRead()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Unexpected unscoped read."));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("runtime-profile-a") with { ScopeRef = default });
        Assert.AreEqual(0, handler.Reads);
        Assert.AreEqual(ResourceLoadStatus.Failed, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
    }

    [TestMethod]
    public async Task NamespaceMismatchRejectsTheResponse()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(Resource("runtime-profile-a") with { Metadata = new() { Name = "runtime-profile-a", Namespace = ResourceNamespace.Parse("other") } })));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("runtime-profile-a"));
        Assert.AreEqual(ResourceLoadStatus.Forbidden, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
        Assert.IsEmpty(cut.FindComponents<RuntimeProfileResourceSummary>());
    }

    private static ScopedResourceAddress Target(string name) => ScopedResourceAddress.Create(Scope, ResourceNamespace.Default, RuntimeProfileResourceKinds.RuntimeProfile, name);
    private static RuntimeProfileResource Resource(string name) => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = RuntimeProfileResourceKinds.RuntimeProfile,
        Metadata = new() { Name = name },
        ScopeRef = Scope,
        Uid = Guid.NewGuid(),
        Definition = new() { DisplayName = "Saved " + name, RuntimeType = "microsoft-agent-framework" }
    };
    private static HttpResponseMessage Response(RuntimeProfileResource resource)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(resource) };
        response.Headers.ETag = new("\"preview\"");
        return response;
    }
    private static IRenderedComponent<RuntimeProfileResourcePreview> Render(BunitContext context, ScopedResourceAddress item) => context.Render<RuntimeProfileResourcePreview>(p => p
        .Add(c => c.Address, item).Add(c => c.OverviewHref, "/runtimeprofiles/" + item.Name + "#overview")
        .Add(c => c.DefinitionHref, "/runtimeprofiles/" + item.Name + "#definition/configuration"));
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
        context.Services.AddSingleton<IRuntimeProfilesClient>(new RuntimeProfilesApiClient(http));
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
            Assert.Contains("/api/runtimeprofiles/", LastPath);
            return respond(request, cancellationToken);
        }
    }
}
