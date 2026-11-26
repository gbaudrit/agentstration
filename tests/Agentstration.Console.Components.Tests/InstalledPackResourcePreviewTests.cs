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
public sealed class InstalledPackResourcePreviewTests
{
    private static readonly ResourceScopeRef Scope = ResourceScopeRef.Workspace(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001"));

    [TestMethod]
    [DataRow("en-US", "Identity", "Configuration")]
    [DataRow("fr-FR", "Identité", "Configuration")]
    public async Task PreviewReusesSavedIdentityAndConfigurationWithOneExactRead(string locale, string identity, string configuration)
    {
        using var culture = new TestCultureScope(locale);
        using var handler = new Handler((_, _) => Task.FromResult(Response(Resource("pack-a"))));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("pack-a"));
        Assert.AreEqual(1, handler.Reads);
        Assert.AreEqual($"/api/packs/preview/pack-a?scopeRef={Uri.EscapeDataString(Scope.Value)}", handler.LastPath);
        Assert.HasCount(1, cut.FindComponents<InstalledPackResourceSummary>());
        Assert.HasCount(1, cut.FindComponents<ResourceIdentitySummary>());
        Assert.AreEqual(identity, cut.Find("[data-testid=pack-identity-summary] h2").TextContent);
        Assert.AreEqual(configuration, cut.Find("[data-testid=pack-configuration-summary] h2").TextContent);
        Assert.Contains("Saved pack-a", cut.Find("[data-testid=pack-identity-summary]").TextContent);
        var identityToggle = cut.Find("[data-testid=pack-identity-summary] .panel-disclosure");
        Assert.AreEqual("true", identityToggle.GetAttribute("aria-expanded"));
        await identityToggle.ClickAsync(new());
        Assert.AreEqual("false", identityToggle.GetAttribute("aria-expanded"));
        Assert.IsTrue(cut.Find("[data-testid=pack-identity-summary] .form-section-body").HasAttribute("hidden"));
        await identityToggle.ClickAsync(new());
        Assert.AreEqual("true", identityToggle.GetAttribute("aria-expanded"));
        Assert.Contains("saved-fixture.zip", cut.Find("[data-testid=pack-configuration-summary]").TextContent);
        Assert.AreEqual("true", cut.Find("[data-testid=pack-configuration-summary] .panel-disclosure").GetAttribute("aria-expanded"));
        Assert.HasCount(1, cut.FindAll("[data-testid=pack-preview-open]"));
        Assert.IsEmpty(cut.FindAll("[data-testid=pack-preview-edit]"));
        Assert.HasCount(1, cut.FindAll("[data-testid=pack-preview-manage]"));
        Assert.IsEmpty(cut.FindAll("form, [data-testid=pack-summary-reconcile], .declaration-resolution-grid, .collection-view-switch"));
        cut.Render(p => p.Add(c => c.Address, Target("pack-a")));
        Assert.AreEqual(1, handler.Reads, "A presentation rerender must not refetch the definition.");
    }

    [TestMethod]
    [DataRow("not-canonical")]
    [DataRow("03-foo-name")]
    [DataRow("999-foo-name")]
    public async Task MalformedCanonicalNamesNeverReadOrScanForAHomonym(string name)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(Resource("pack-a"))));
        using var context = await ContextAsync(handler);
        var cut = Render(context, ScopedResourceAddress.Create(Scope, ResourceNamespace.Default, PackKinds.InstalledPack, name));
        Assert.AreEqual(0, handler.Reads);
        Assert.AreEqual(ResourceLoadStatus.NotFound, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
        Assert.IsEmpty(cut.FindComponents<InstalledPackResourceSummary>());
    }

    [TestMethod]
    [DataRow("13-agentstration-starter", "agentstration", "starter")]
    [DataRow("7-foo-bar-nested-name", "foo-bar", "nested-name")]
    public void CanonicalNamesPreservePublisherAndPackDelimiters(string name, string publisher, string pack)
    {
        Assert.IsTrue(PackIdentity.TryParseResourceName(name, out var identity));
        Assert.AreEqual(publisher, identity.Publisher);
        Assert.AreEqual(pack, identity.Name);
    }

    [TestMethod]
    [DataRow("en-US")]
    [DataRow("fr-FR")]
    public async Task ReadOnlyDetailsUseTheSavedSummaryAndSeparateInstallationManagement(string locale)
    {
        using var culture = new TestCultureScope(locale);
        using var handler = new Handler((_, _) => Task.FromResult(Response(Resource("pack-a"))));
        using var context = await ContextAsync(handler);
        context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo(
            $"/packs/resources/{new PackIdentity("preview", "pack-a").ResourceName}?scopeRef={Uri.EscapeDataString(Scope.Value)}#overview");
        var cut = context.Render<Agentstration.Web.Components.Pages.InstalledPackDetails>(p => p.Add(c => c.Name, new PackIdentity("preview", "pack-a").ResourceName));
        Assert.IsNotNull(cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.OverviewSummary);
        Assert.Contains("saved-fixture.zip", cut.Markup);
        Assert.Contains("#pack-management", cut.Markup);
        Assert.AreEqual(1, handler.Reads);
        Assert.IsEmpty(cut.FindAll("form, [data-testid=pack-preview-edit]"));
    }

    [TestMethod]
    public async Task OwnerMismatchNeverRendersTheOtherWorkspacesResource()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(Resource("pack-a") with { ScopeRef = ResourceScopeRef.Instance })));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("pack-a"));
        Assert.AreEqual(ResourceLoadStatus.Forbidden, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
        Assert.IsEmpty(cut.FindComponents<InstalledPackResourceSummary>());
        Assert.DoesNotContain("Saved pack-a", cut.Markup);
    }

    [TestMethod]
    public async Task InheritedResourceOffersInspectionWithoutAnEditShortcut()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(Resource("pack-a"))));
        using var context = await ContextAsync(handler, selectedScope: "/instance");
        var cut = Render(context, Target("pack-a"));
        Assert.HasCount(1, cut.FindAll("[data-testid=pack-preview-open]"));
        Assert.IsEmpty(cut.FindAll("[data-testid=pack-preview-edit], [data-testid=pack-preview-designer]"));
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
        var cut = Render(context, Target("pack-a"));
        Assert.AreEqual(expected, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
        Assert.DoesNotContain("Private transport detail", cut.Markup);
        Assert.IsEmpty(cut.FindComponents<InstalledPackResourceSummary>());
    }

    [TestMethod]
    public async Task RetryRepeatsOnlyTheSelectedResourceRead()
    {
        var calls = 0;
        using var handler = new Handler((_, _) => Task.FromResult(++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : Response(Resource("pack-a"))));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("pack-a"));
        Assert.AreEqual(ResourceLoadStatus.Failed, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
        await cut.Find("[data-testid=pack-preview-retry]").ClickAsync(new());
        Assert.HasCount(1, cut.FindComponents<InstalledPackResourceSummary>());
        Assert.AreEqual(2, handler.Reads);
    }

    [TestMethod]
    public async Task ReplacingSelectionCancelsAndRejectsThePreviousLateResponse()
    {
        var old = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler((request, _) => request.RequestUri!.AbsolutePath.EndsWith("pack-a", StringComparison.Ordinal)
            ? old.Task : Task.FromResult(Response(Resource("pack-b"))));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("pack-a"));
        var oldToken = handler.LastToken;
        Assert.HasCount(1, cut.FindComponents<LoadingState>());
        cut.Render(p => p.Add(c => c.Address, Target("pack-b")));
        Assert.IsTrue(oldToken.IsCancellationRequested);
        cut.WaitForAssertion(() => Assert.Contains("Saved pack-b", cut.Markup));
        var renders = cut.RenderCount;
        old.SetResult(Response(Resource("pack-a")));
        cut.WaitForState(() => cut.RenderCount > renders);
        Assert.Contains("Saved pack-b", cut.Markup);
        Assert.DoesNotContain("Saved pack-a", cut.Markup);
    }

    [TestMethod]
    public async Task ClosingCancelsThePendingRead()
    {
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler((_, _) => response.Task);
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("pack-a"));
        var token = handler.LastToken;
        await context.DisposeComponentsAsync();
        Assert.IsTrue(token.IsCancellationRequested);
        response.SetResult(Response(Resource("pack-a")));
    }

    [TestMethod]
    public async Task MissingReadPermissionPerformsNoRequest()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Unexpected read."));
        using var context = await ContextAsync(handler, permissions: new HashSet<string>());
        var cut = Render(context, Target("pack-a"));
        Assert.AreEqual(0, handler.Reads);
        Assert.AreEqual(ResourceLoadStatus.Forbidden, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
    }

    [TestMethod]
    public async Task MissingOwnerNeverFallsBackToAnUnscopedRead()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Unexpected unscoped read."));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("pack-a") with { ScopeRef = default });
        Assert.AreEqual(0, handler.Reads);
        Assert.AreEqual(ResourceLoadStatus.Failed, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
    }

    [TestMethod]
    public async Task NamespacedAddressNeverFallsBackToTheDefaultNamespace()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Unexpected default read."));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("pack-a") with { Namespace = ResourceNamespace.Parse("pack-a") });
        Assert.AreEqual(0, handler.Reads);
        Assert.AreEqual(ResourceLoadStatus.Failed, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
    }

    [TestMethod]
    public async Task InheritedManagedResourceLinksKeepTheCurrentContextAndDoNotGuessEntryExecutionOwnership()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Pure Summary must not read."));
        using var context = await ContextAsync(handler, selectedScope: "/instance", navigation: new NavigationContext("/instance"));
        var pack = Resource("pack-a");
        pack = pack with { Definition = pack.Definition with { ManagedResources = [Managed("Agent"), Managed("Flow"), Managed("Entry")] } };
        var cut = context.Render<InstalledPackResourceSummary>(p => p.Add(c => c.Resource, pack));
        var links = cut.FindAll(".pack-resource-list a.resource-name-link");
        Assert.HasCount(2, links);
        foreach (var link in links)
        {
            var href = link.GetAttribute("href")!;
            Assert.Contains("context=%2Finstance", href);
            Assert.Contains("scopeRef=" + Uri.EscapeDataString(Scope.Value), href);
            Assert.IsTrue(href.EndsWith("#overview", StringComparison.Ordinal));
        }
        Assert.AreEqual(0, handler.Reads);
    }

    [TestMethod]
    public async Task KnownManagedEntryUsesItsExecutionWorkspaceAndKeepsLogicalAdministration()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Pure Summary must not read."));
        using var context = await ContextAsync(handler, selectedScope: "/instance", navigation: new NavigationContext("/instance"));
        var executionOwner = ResourceScopeRef.Workspace(Guid.NewGuid());
        var pack = Resource("pack-a") with { ScopeRef = ResourceScopeRef.Instance };
        pack = pack with { Definition = pack.Definition with { ManagedResources = [Managed("Entry")] } };
        var cut = context.Render<InstalledPackResourceSummary>(p => p.Add(c => c.Resource, pack).Add(c => c.EntryExecutionOwner, executionOwner));
        var href = cut.Find(".pack-resource-list a.resource-name-link").GetAttribute("href")!;
        Assert.Contains("scopeRef=" + Uri.EscapeDataString(executionOwner.Value), href);
        Assert.Contains("context=%2Finstance", href);
        Assert.DoesNotContain(executionOwner.TargetId!.Value.ToString("D"), cut.Find(".pack-resource-list").TextContent);
        Assert.AreEqual(0, handler.Reads);
    }
    private static ManagedPackResource Managed(string kind) => new() { Kind = kind, Name = "saved-" + kind.ToLowerInvariant(), Namespace = new("preview.pack"), Path = "fixture.yaml", VersionToken = "1" };
    private sealed class NavigationContext(string scope) : IConsoleNavigationContext
    {
        public string ScopeRef => scope;
        public string AddToUri(string uri) => Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(uri, "context", scope);
    }

    private static ScopedResourceAddress Target(string name) => ScopedResourceAddress.Create(Scope, ResourceNamespace.Default, PackKinds.InstalledPack, new PackIdentity("preview", name).ResourceName);
    private static InstalledPackResource Resource(string name) => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = PackKinds.InstalledPack,
        Metadata = new() { Name = new PackIdentity("preview", name).ResourceName },
        ScopeRef = Scope,
        Uid = Guid.NewGuid(),
        Definition = new() { DisplayName = "Saved " + name, Publisher = "preview", PackName = name, Version = "1.0.0", Source = "saved-fixture.zip", InstalledAt = DateTimeOffset.UnixEpoch }
    };
    private static HttpResponseMessage Response(InstalledPackResource resource)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(resource) };
        response.Headers.ETag = new("\"preview\"");
        return response;
    }
    private static IRenderedComponent<InstalledPackResourcePreview> Render(BunitContext context, ScopedResourceAddress item) => context.Render<InstalledPackResourcePreview>(p => p
        .Add(c => c.Address, item).Add(c => c.OverviewHref, "/packs/" + item.Name + "#overview")
        .Add(c => c.DefinitionHref, "/packs/" + item.Name + "#definition/configuration"));
    private static async Task<BunitContext> ContextAsync(Handler handler, string? selectedScope = null, IReadOnlySet<string>? permissions = null, IConsoleNavigationContext? navigation = null)
    {
        var context = new BunitContext().WithCollectionPresentation().WithResourceListContext(new()
        {
            Scope = selectedScope ?? Scope.Value,
            Permissions = permissions ?? new HashSet<string> { AuthorizationPermissions.ResourcesRead, AuthorizationPermissions.ResourcesWrite, AuthorizationPermissions.RunsExecute }
        });
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        if (navigation is not null) context.Services.AddSingleton(navigation);
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
            Assert.Contains("/api/packs/", LastPath);
            return respond(request, cancellationToken);
        }
    }
}
