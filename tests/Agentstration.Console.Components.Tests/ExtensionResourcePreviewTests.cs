using System.Net;
using System.Net.Http.Json;
using Agentstration.Extensions.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class ExtensionResourcePreviewTests
{
    private static readonly ResourceScopeRef Scope = ResourceScopeRef.Tenant(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001"));

    [TestMethod]
    [DataRow("en-US", "Identity")]
    [DataRow("fr-FR", "Identité")]
    public async Task PreviewReadsOnlyTheExactRegistrationAndKeepsEnrollmentAnIndependentAction(string locale, string identity)
    {
        using var culture = new TestCultureScope(locale);
        using var handler = new Handler((_, _) => Task.FromResult(Response(Resource("extension-a"))));
        using var context = await ContextAsync(handler);
        var enrollments = 0;
        var cut = Render(context, Target("extension-a"), p => p.Add(c => c.CanManageEnrollment, true).Add(c => c.OnManageEnrollment, () => enrollments++));
        Assert.AreEqual(1, handler.Reads);
        Assert.AreEqual($"/api/extensionregistrations/extension-a?resourceNamespace=default&scopeRef={Uri.EscapeDataString(Scope.Value)}", handler.LastPath);
        Assert.HasCount(1, cut.FindComponents<ExtensionResourceSummary>());
        Assert.AreEqual(identity, cut.Find("[data-testid=extension-identity-summary] h2").TextContent);
        Assert.Contains("Saved extension-a", cut.Find("[data-testid=extension-identity-summary]").TextContent);
        Assert.Contains("https://extension.example.test/extension-a", cut.Find("[data-testid=extension-configuration-summary]").TextContent);
        Assert.AreEqual("true", cut.Find("[data-testid=extension-identity-summary] .panel-disclosure").GetAttribute("aria-expanded"));
        Assert.HasCount(1, cut.FindAll("[data-testid=extension-preview-edit]"));
        Assert.IsEmpty(cut.FindAll("form, .collection-actions-menu, .collection-view-switch, [data-testid=enrollment-card]"));
        await cut.Find("[data-testid=extension-preview-enrollment]").ClickAsync(new());
        Assert.AreEqual(1, enrollments);
        cut.Render(p => p.Add(c => c.Address, Target("extension-a")));
        Assert.AreEqual(1, handler.Reads, "Quick actions and presentation rerenders must not fetch the inventory or private enrollment records.");
    }

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public async Task ManagedOrInheritedRegistrationsOfferInspectionWithoutAnEditShortcut(bool managed, bool inherited)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(Resource("extension-a") with
        { Definition = Resource("extension-a").Definition with { Source = managed ? ExtensionRegistrationSource.Configuration : ExtensionRegistrationSource.Manual } })));
        using var context = await ContextAsync(handler, inherited ? "/instance" : Scope.Value);
        var cut = Render(context, Target("extension-a"));
        Assert.HasCount(1, cut.FindAll("[data-testid=extension-preview-open]"));
        Assert.IsEmpty(cut.FindAll("[data-testid=extension-preview-edit], [data-testid=extension-preview-enrollment]"));
    }

    [TestMethod]
    [DataRow(403, ResourceLoadStatus.Forbidden)]
    [DataRow(404, ResourceLoadStatus.NotFound)]
    public async Task ExpectedFailuresUseSharedFeedbackWithoutTransportDetails(int status, ResourceLoadStatus expected)
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        { Content = JsonContent.Create(new { title = "Private transport detail", status }) }));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("extension-a"));
        Assert.AreEqual(expected, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
        Assert.DoesNotContain("Private transport detail", cut.Markup);
        Assert.IsEmpty(cut.FindComponents<ExtensionResourceSummary>());
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task AddressMismatchNeverExposesAnotherRegistration(bool ownerMismatch)
    {
        var resource = Resource("extension-a");
        using var handler = new Handler((_, _) => Task.FromResult(Response(ownerMismatch ? resource with { ScopeRef = ResourceScopeRef.Instance }
            : resource with { Metadata = resource.Metadata with { Namespace = ResourceNamespace.Parse("other") } })));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("extension-a"));
        Assert.AreEqual(ResourceLoadStatus.Forbidden, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
        Assert.IsEmpty(cut.FindComponents<ExtensionResourceSummary>());
    }

    [TestMethod]
    public async Task ReadOnlyPreviewNeverOffersEnrollmentAndMissingReadPermissionDoesNotFetch()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(Resource("extension-a"))));
        using var context = await ContextAsync(handler, permissions: new HashSet<string> { AuthorizationPermissions.ResourcesRead });
        var cut = Render(context, Target("extension-a"), p => p.Add(c => c.CanManageEnrollment, true));
        Assert.IsEmpty(cut.FindAll("[data-testid=extension-preview-edit], [data-testid=extension-preview-enrollment]"));
        using var denied = await ContextAsync(handler, permissions: new HashSet<string>());
        var forbidden = Render(denied, Target("extension-a"));
        Assert.AreEqual(ResourceLoadStatus.Forbidden, forbidden.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
        Assert.AreEqual(1, handler.Reads);
    }

    [TestMethod]
    public async Task RetryRepeatsOnlyTheSelectedExactRead()
    {
        var reads = 0;
        using var handler = new Handler((_, _) => Task.FromResult(++reads == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Response(Resource("extension-a"))));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("extension-a"));
        Assert.AreEqual(ResourceLoadStatus.Failed, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
        await cut.Find("[data-testid=extension-preview-retry]").ClickAsync(new());
        Assert.HasCount(1, cut.FindComponents<ExtensionResourceSummary>());
        Assert.AreEqual(2, handler.Reads);
    }

    [TestMethod]
    public async Task ReplacingSelectionCancelsAndRejectsThePreviousLateResponse()
    {
        var old = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler((request, _) => request.RequestUri!.AbsolutePath.EndsWith("extension-a", StringComparison.Ordinal)
            ? old.Task : Task.FromResult(Response(Resource("extension-b"))));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("extension-a"));
        var oldToken = handler.LastToken;
        cut.Render(p => p.Add(c => c.Address, Target("extension-b")));
        Assert.IsTrue(oldToken.IsCancellationRequested);
        cut.WaitForAssertion(() => Assert.Contains("Saved extension-b", cut.Markup));
        old.SetResult(Response(Resource("extension-a")));
        await cut.InvokeAsync(() => Task.CompletedTask);
        Assert.DoesNotContain("Saved extension-a", cut.Markup);
    }

    [TestMethod]
    public async Task UnmountCancelsTheReadAndMissingOwnerNeverFallsBackToAnUnscopedRead()
    {
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler((_, _) => response.Task);
        using var context = await ContextAsync(handler);
        var invalid = Render(context, Target("extension-a") with { ScopeRef = default });
        Assert.AreEqual(ResourceLoadStatus.Failed, invalid.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
        Assert.AreEqual(0, handler.Reads);
        var cut = Render(context, Target("extension-a"));
        var token = handler.LastToken;
        await context.DisposeComponentsAsync();
        Assert.IsTrue(token.IsCancellationRequested);
        response.SetResult(Response(Resource("extension-a")));
    }

    [TestMethod]
    [DataRow("uid")]
    [DataRow("etag")]
    [DataRow("generation")]
    [DataRow("match")]
    public async Task GraphPreviewRequiresTheObservedUidAndSavedState(string change)
    {
        var saved = Resource("graph-preview");
        using var handler = new Handler((_, _) => Task.FromResult(Response(saved)));
        using var context = await ContextAsync(handler);
        var expectation = new ResourcePreviewExpectation(Scope, change == "uid" ? Guid.NewGuid() : saved.Uid,
            change == "generation" ? 999 : saved.Generation, change == "etag" ? "stale" : "\"preview\"");
        var cut = context.Render<ExtensionResourcePreview>(p => p.Add(c => c.Address, Target("graph-preview"))
            .Add(c => c.Expectation, expectation).Add(c => c.OverviewHref, "/overview").Add(c => c.DefinitionHref, "/definition"));
        if (change == "match") Assert.HasCount(1, cut.FindComponents<ExtensionResourceSummary>());
        else
        {
            Assert.IsEmpty(cut.FindComponents<ExtensionResourceSummary>());
            Assert.AreEqual(ResourceLoadStatus.NotFound, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
            Assert.DoesNotContain("Saved graph-preview", cut.Markup);
        }
    }

    [TestMethod]
    public async Task GraphPreviewDoesNotReadInAnotherActorContext()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Unexpected read."));
        using var context = await ContextAsync(handler);
        var cut = context.Render<ExtensionResourcePreview>(p => p.Add(c => c.Address, Target("graph-preview"))
            .Add(c => c.Expectation, new ResourcePreviewExpectation(ResourceScopeRef.Instance, Guid.NewGuid()))
            .Add(c => c.OverviewHref, "/overview").Add(c => c.DefinitionHref, "/definition"));
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
        var cut = context.Render<ExtensionResourcePreview>(p => p.Add(c => c.Address, Target("graph-preview"))
            .Add(c => c.Expectation, new ResourcePreviewExpectation(Scope, saved.Uid))
            .Add(c => c.OverviewHref, "/overview").Add(c => c.DefinitionHref, "/definition"));
        Assert.HasCount(1, cut.FindComponents<ExtensionResourceSummary>());
        var provider = (ResourceListContextProvider)context.Services.GetRequiredService<Agentstration.Web.Components.State.IConsoleContextProvider>();
        if (changeActor) provider.Scope = ResourceScopeRef.Instance.Value;
        else provider.Permissions = new HashSet<string>();
        await cut.InvokeAsync(() => context.Services.GetRequiredService<ConsoleContextState>().LoadAsync(CancellationToken.None));
        cut.WaitForAssertion(() => Assert.IsEmpty(cut.FindComponents<ExtensionResourceSummary>()));
        Assert.DoesNotContain("Saved graph-preview", cut.Markup);
        Assert.AreEqual(1, handler.Reads);
    }

    private static ScopedResourceAddress Target(string name) => ScopedResourceAddress.Create(Scope, ResourceNamespace.Default, ExtensionKinds.ExtensionRegistration, name);
    private static ExtensionRegistrationResource Resource(string name) => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = ExtensionKinds.ExtensionRegistration,
        Uid = Guid.NewGuid(),
        ScopeRef = Scope,
        Metadata = new() { Name = name },
        Definition = new() { DisplayName = "Saved " + name, Endpoint = new("https://extension.example.test/" + name), Source = ExtensionRegistrationSource.Manual }
    };
    private static HttpResponseMessage Response(ExtensionRegistrationResource resource)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(resource) };
        response.Headers.ETag = new("\"preview\""); return response;
    }
    private static IRenderedComponent<ExtensionResourcePreview> Render(BunitContext context, ScopedResourceAddress target, Action<ComponentParameterCollectionBuilder<ExtensionResourcePreview>>? configure = null) =>
        context.Render<ExtensionResourcePreview>(p =>
        {
            p.Add(c => c.Address, target).Add(c => c.OverviewHref, "/extensions/" + target.Name + "#overview")
                .Add(c => c.DefinitionHref, "/extensions/" + target.Name + "#definition/configuration"); configure?.Invoke(p);
        });
    private static async Task<BunitContext> ContextAsync(Handler handler, string? selectedScope = null, IReadOnlySet<string>? permissions = null)
    {
        var context = new BunitContext().WithCollectionPresentation().WithResourceListContext(new()
        { Scope = selectedScope ?? Scope.Value, Permissions = permissions ?? new HashSet<string> { AuthorizationPermissions.ResourcesRead, AuthorizationPermissions.ResourcesWrite } });
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IExtensionsClient>(new ExtensionsApiClient(new HttpClient(handler, false) { BaseAddress = new("https://agentstration.test/") }));
        await context.Services.GetRequiredService<ConsoleContextState>().EnsureLoadedAsync(CancellationToken.None); return context;
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Reads { get; private set; }
        public string? LastPath { get; private set; }
        public CancellationToken LastToken { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Reads++; LastPath = request.RequestUri!.PathAndQuery; LastToken = cancellationToken;
            Assert.AreEqual(HttpMethod.Get, request.Method); Assert.Contains("/api/extensionregistrations/", LastPath); return respond(request, cancellationToken);
        }
    }
}
