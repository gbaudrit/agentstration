using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class EntryResourcePreviewTests
{
    private static readonly ResourceScopeRef Scope = ResourceScopeRef.Workspace(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001"));

    [TestMethod]
    [DataRow("en-US", "Identity", "Configuration")]
    [DataRow("fr-FR", "Identité", "Configuration")]
    public async Task PreviewReusesSavedIdentityAndConfigurationWithOneExactRead(string locale, string identity, string configuration)
    {
        using var culture = new TestCultureScope(locale);
        using var handler = new Handler((_, _) => Task.FromResult(Response(Resource("entry-a"))));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("entry-a"));
        Assert.AreEqual(1, handler.Reads);
        Assert.AreEqual($"/api/management/entries/entry-a/summary?scopeRef={Uri.EscapeDataString(Scope.Value)}&namespace=default", handler.LastPath);
        Assert.HasCount(1, cut.FindComponents<EntryResourceSummary>());
        Assert.HasCount(1, cut.FindComponents<ResourceIdentitySummary>());
        Assert.AreEqual(identity, cut.Find("[data-testid=entry-identity-summary] h2").TextContent);
        Assert.AreEqual(configuration, cut.Find("[data-testid=entry-configuration-summary] h2").TextContent);
        Assert.Contains("Saved entry-a", cut.Find("[data-testid=entry-identity-summary]").TextContent);
        var identityToggle = cut.Find("[data-testid=entry-identity-summary] .panel-disclosure");
        Assert.AreEqual("true", identityToggle.GetAttribute("aria-expanded"));
        await identityToggle.ClickAsync(new());
        Assert.AreEqual("false", identityToggle.GetAttribute("aria-expanded"));
        Assert.IsTrue(cut.Find("[data-testid=entry-identity-summary] .form-section-body").HasAttribute("hidden"));
        await identityToggle.ClickAsync(new());
        Assert.AreEqual("true", identityToggle.GetAttribute("aria-expanded"));
        Assert.Contains("saved-value", cut.Find("[data-testid=entry-configuration-summary]").TextContent);
        Assert.AreEqual("true", cut.Find("[data-testid=entry-configuration-summary] .panel-disclosure").GetAttribute("aria-expanded"));
        Assert.HasCount(1, cut.FindAll("[data-testid=entry-preview-open]"));
        Assert.HasCount(1, cut.FindAll("[data-testid=entry-preview-edit]"));
        Assert.AreEqual(locale == "fr-FR" ? "Modifier la définition" : "Edit definition", cut.Find("[data-testid=entry-preview-edit]").TextContent.Trim());
        Assert.IsEmpty(cut.FindAll("form, [data-testid=entry-summary-reconcile], .declaration-resolution-grid, .collection-view-switch"));
        cut.Render(p => p.Add(c => c.Address, Target("entry-a")));
        Assert.AreEqual(1, handler.Reads, "A presentation rerender must not refetch the definition.");
    }

    [TestMethod]
    public async Task OwnerMismatchNeverRendersTheOtherWorkspacesResource()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(Resource("entry-a") with { WorkspaceId = new(Guid.NewGuid()) })));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("entry-a"));
        Assert.AreEqual(ResourceLoadStatus.Forbidden, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
        Assert.IsEmpty(cut.FindComponents<EntryResourceSummary>());
        Assert.DoesNotContain("Saved entry-a", cut.Markup);
    }

    [TestMethod]
    public async Task UnknownManagementOffersInspectionWithoutAnEditShortcut()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(Resource("entry-a"))));
        using var context = await ContextAsync(handler, selectedScope: "/instance");
        var cut = Render(context, Target("entry-a"));
        cut.Render(p => p.Add(c => c.ManagedByPack, null));
        Assert.HasCount(1, cut.FindAll("[data-testid=entry-preview-open]"));
        Assert.IsEmpty(cut.FindAll("[data-testid=entry-preview-edit], [data-testid=entry-preview-designer]"));
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
        var cut = Render(context, Target("entry-a"));
        Assert.AreEqual(expected, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
        Assert.DoesNotContain("Private transport detail", cut.Markup);
        Assert.IsEmpty(cut.FindComponents<EntryResourceSummary>());
    }

    [TestMethod]
    public async Task RetryRepeatsOnlyTheSelectedResourceRead()
    {
        var calls = 0;
        using var handler = new Handler((_, _) => Task.FromResult(++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : Response(Resource("entry-a"))));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("entry-a"));
        Assert.AreEqual(ResourceLoadStatus.Failed, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
        await cut.Find("[data-testid=entry-preview-retry]").ClickAsync(new());
        Assert.HasCount(1, cut.FindComponents<EntryResourceSummary>());
        Assert.AreEqual(2, handler.Reads);
    }

    [TestMethod]
    public async Task ReplacingSelectionCancelsAndRejectsThePreviousLateResponse()
    {
        var old = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler((request, _) => request.RequestUri!.AbsolutePath.EndsWith("entry-a/summary", StringComparison.Ordinal)
            ? old.Task : Task.FromResult(Response(Resource("entry-b"))));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("entry-a"));
        var oldToken = handler.LastToken;
        Assert.HasCount(1, cut.FindComponents<LoadingState>());
        cut.Render(p => p.Add(c => c.Address, Target("entry-b")));
        Assert.IsTrue(oldToken.IsCancellationRequested);
        cut.WaitForAssertion(() => Assert.Contains("Saved entry-b", cut.Markup));
        var renders = cut.RenderCount;
        old.SetResult(Response(Resource("entry-a")));
        cut.WaitForState(() => cut.RenderCount > renders);
        Assert.Contains("Saved entry-b", cut.Markup);
        Assert.DoesNotContain("Saved entry-a", cut.Markup);
    }

    [TestMethod]
    public async Task ClosingCancelsThePendingRead()
    {
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler((_, _) => response.Task);
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("entry-a"));
        var token = handler.LastToken;
        await context.DisposeComponentsAsync();
        Assert.IsTrue(token.IsCancellationRequested);
        response.SetResult(Response(Resource("entry-a")));
    }

    [TestMethod]
    public async Task MissingReadPermissionPerformsNoRequest()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Unexpected read."));
        using var context = await ContextAsync(handler, permissions: new HashSet<string>());
        var cut = Render(context, Target("entry-a"));
        Assert.AreEqual(0, handler.Reads);
        Assert.AreEqual(ResourceLoadStatus.Forbidden, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
    }

    [TestMethod]
    public async Task MissingOwnerNeverFallsBackToAnUnscopedRead()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Unexpected unscoped read."));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("entry-a") with { ScopeRef = default });
        Assert.AreEqual(0, handler.Reads);
        Assert.AreEqual(ResourceLoadStatus.Failed, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
    }

    [TestMethod]
    public async Task NamespaceMismatchRejectsTheResponse()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(Resource("entry-a") with { Id = new("entry-a", ResourceNamespace.Parse("other")) })));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("entry-a"));
        Assert.AreEqual(ResourceLoadStatus.Forbidden, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
        Assert.IsEmpty(cut.FindComponents<EntryResourceSummary>());
    }

    [TestMethod]
    [DataRow("en-US")]
    [DataRow("fr-FR")]
    public async Task OperationalOwnerIsKeptInTheAddressButPresentedAsItsLogicalContext(string locale)
    {
        using var culture = new TestCultureScope(locale);
        using var handler = new Handler((_, _) => Task.FromResult(Response(Resource("entry-a"))));
        using var context = await ContextAsync(handler, selectedScope: "/instance");
        var cut = Render(context, Target("entry-a"));
        Assert.AreEqual(1, handler.Reads);
        Assert.DoesNotContain(Scope.TargetId!.Value.ToString("D"), cut.Find("[data-testid=entry-identity-summary]").TextContent);
        Assert.IsTrue(cut.FindAll("[data-testid=entry-preview-edit]").Count == 1);
    }

    private static ScopedResourceAddress Target(string name) => ScopedResourceAddress.Create(Scope, ResourceNamespace.Default, EntryResourceKinds.Entry, name);
    private static EntryDraft Resource(string name) => new()
    {
        WorkspaceId = new(Scope.TargetId!.Value),
        Id = new(name),
        Name = name,
        DisplayName = "Saved " + name,
        Presentation = new() { Fields = [new EntryFieldDefinition { Name = "request", Label = "Request", Type = EntryFieldType.Prompt, Required = true, Role = EntryFieldRole.PrimaryInput }] },
        Binding = new(EntryBindingKind.Agent, "saved-value")
    };
    private static HttpResponseMessage Response(EntryDraft resource)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new EntrySavedSummaryResponse(resource, null)) };
        response.Headers.ETag = new("\"preview\"");
        return response;
    }
    private static IRenderedComponent<EntryResourcePreview> Render(BunitContext context, ScopedResourceAddress item) => context.Render<EntryResourcePreview>(p => p
        .Add(c => c.Address, item).Add(c => c.ManagedByPack, false).Add(c => c.OverviewHref, "/management/" + item.Name + "#overview")
        .Add(c => c.DefinitionHref, "/management/" + item.Name + "#definition/configuration"));
    private static async Task<BunitContext> ContextAsync(Handler handler, string? selectedScope = null, IReadOnlySet<string>? permissions = null)
    {
        var context = new BunitContext().WithCollectionPresentation().WithResourceListContext(new()
        {
            Scope = selectedScope ?? Scope.Value,
            PhysicalWorkspaceId = Scope.TargetId!.Value,
            Permissions = permissions ?? new HashSet<string> { AuthorizationPermissions.ResourcesRead, AuthorizationPermissions.ResourcesWrite, AuthorizationPermissions.RunsExecute }
        });
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var http = new HttpClient(handler, false) { BaseAddress = new("https://agentstration.test/") };
        context.Services.AddSingleton(http);
        context.Services.AddSingleton<IEntryAdministrationApiClient>(new EntryAdministrationApiClient(http));
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
            Assert.Contains("/api/management/entries/", LastPath);
            return respond(request, cancellationToken);
        }
    }
}
