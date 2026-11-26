using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.DataSources.Contracts;

using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;

using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class DataSourceProfileResourcePreviewTests
{
    private static readonly ResourceScopeRef Scope = ResourceScopeRef.Workspace(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001"));

    [TestMethod]
    [DataRow("en-US", "Identity", "Configuration")]
    [DataRow("fr-FR", "Identité", "Configuration")]
    public async Task PreviewReusesSavedIdentityAndConfigurationWithOneExactRead(string locale, string identity, string configuration)
    {
        using var culture = new TestCultureScope(locale);
        using var handler = new Handler((_, _) => Task.FromResult(Response(Resource("data-source-profile-a"))));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("data-source-profile-a"));
        Assert.AreEqual(1, handler.Reads);
        Assert.AreEqual($"/api/datasourceprofiles/data-source-profile-a?namespace=default&scopeRef={Uri.EscapeDataString(Scope.Value)}", handler.LastPath);
        Assert.HasCount(1, cut.FindComponents<DataSourceProfileResourceSummary>());
        Assert.HasCount(1, cut.FindComponents<ResourceIdentitySummary>());
        Assert.AreEqual(identity, cut.Find("[data-testid=data-source-profile-identity-summary] h2").TextContent);
        Assert.AreEqual(configuration, cut.Find("[data-testid=data-source-profile-configuration-summary] h2").TextContent);
        Assert.Contains("Saved data-source-profile-a", cut.Find("[data-testid=data-source-profile-identity-summary]").TextContent);
        var identityToggle = cut.Find("[data-testid=data-source-profile-identity-summary] .panel-disclosure");
        Assert.AreEqual("true", identityToggle.GetAttribute("aria-expanded"));
        await identityToggle.ClickAsync(new());
        Assert.AreEqual("false", identityToggle.GetAttribute("aria-expanded"));
        Assert.IsTrue(cut.Find("[data-testid=data-source-profile-identity-summary] .form-section-body").HasAttribute("hidden"));
        await identityToggle.ClickAsync(new());
        Assert.AreEqual("true", identityToggle.GetAttribute("aria-expanded"));
        Assert.Contains("acquire", cut.Find("[data-testid=data-source-profile-configuration-summary]").TextContent);
        Assert.AreEqual("true", cut.Find("[data-testid=data-source-profile-configuration-summary] .panel-disclosure").GetAttribute("aria-expanded"));
        Assert.HasCount(1, cut.FindAll("[data-testid=data-source-profile-preview-open]"));
        Assert.HasCount(1, cut.FindAll("[data-testid=data-source-profile-preview-edit]"));
        Assert.AreEqual(locale == "fr-FR" ? "Modifier la définition" : "Edit definition", cut.Find("[data-testid=data-source-profile-preview-edit]").TextContent.Trim());
        Assert.IsEmpty(cut.FindAll("form, [data-testid=data-source-profile-summary-reconcile], .declaration-resolution-grid, .collection-view-switch"));
        cut.Render(p => p.Add(c => c.Address, Target("data-source-profile-a")));
        Assert.AreEqual(1, handler.Reads, "A presentation rerender must not refetch the definition.");
    }

    [TestMethod]
    public async Task OwnerMismatchNeverRendersTheOtherWorkspacesResource()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(Resource("data-source-profile-a") with { ScopeRef = ResourceScopeRef.Instance })));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("data-source-profile-a"));
        Assert.AreEqual(ResourceLoadStatus.Forbidden, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
        Assert.IsEmpty(cut.FindComponents<DataSourceProfileResourceSummary>());
        Assert.DoesNotContain("Saved data-source-profile-a", cut.Markup);
    }

    [TestMethod]
    public async Task InheritedResourceOffersInspectionWithoutAnEditShortcut()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(Resource("data-source-profile-a"))));
        using var context = await ContextAsync(handler, selectedScope: "/instance");
        var cut = Render(context, Target("data-source-profile-a"));
        Assert.HasCount(1, cut.FindAll("[data-testid=data-source-profile-preview-open]"));
        Assert.IsEmpty(cut.FindAll("[data-testid=data-source-profile-preview-edit], [data-testid=data-source-profile-preview-designer]"));
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
        var cut = Render(context, Target("data-source-profile-a"));
        Assert.AreEqual(expected, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
        Assert.DoesNotContain("Private transport detail", cut.Markup);
        Assert.IsEmpty(cut.FindComponents<DataSourceProfileResourceSummary>());
    }

    [TestMethod]
    public async Task RetryRepeatsOnlyTheSelectedResourceRead()
    {
        var calls = 0;
        using var handler = new Handler((_, _) => Task.FromResult(++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : Response(Resource("data-source-profile-a"))));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("data-source-profile-a"));
        Assert.AreEqual(ResourceLoadStatus.Failed, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
        await cut.Find("[data-testid=data-source-profile-preview-retry]").ClickAsync(new());
        Assert.HasCount(1, cut.FindComponents<DataSourceProfileResourceSummary>());
        Assert.AreEqual(2, handler.Reads);
    }

    [TestMethod]
    public async Task ReplacingSelectionCancelsAndRejectsThePreviousLateResponse()
    {
        var old = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler((request, _) => request.RequestUri!.AbsolutePath.EndsWith("data-source-profile-a", StringComparison.Ordinal)
            ? old.Task : Task.FromResult(Response(Resource("data-source-profile-b"))));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("data-source-profile-a"));
        var oldToken = handler.LastToken;
        Assert.HasCount(1, cut.FindComponents<LoadingState>());
        cut.Render(p => p.Add(c => c.Address, Target("data-source-profile-b")));
        Assert.IsTrue(oldToken.IsCancellationRequested);
        cut.WaitForAssertion(() => Assert.Contains("Saved data-source-profile-b", cut.Markup));
        var renders = cut.RenderCount;
        old.SetResult(Response(Resource("data-source-profile-a")));
        cut.WaitForState(() => cut.RenderCount > renders);
        Assert.Contains("Saved data-source-profile-b", cut.Markup);
        Assert.DoesNotContain("Saved data-source-profile-a", cut.Markup);
    }

    [TestMethod]
    public async Task ClosingCancelsThePendingRead()
    {
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler((_, _) => response.Task);
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("data-source-profile-a"));
        var token = handler.LastToken;
        await context.DisposeComponentsAsync();
        Assert.IsTrue(token.IsCancellationRequested);
        response.SetResult(Response(Resource("data-source-profile-a")));
    }

    [TestMethod]
    public async Task MissingReadPermissionPerformsNoRequest()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Unexpected read."));
        using var context = await ContextAsync(handler, permissions: new HashSet<string>());
        var cut = Render(context, Target("data-source-profile-a"));
        Assert.AreEqual(0, handler.Reads);
        Assert.AreEqual(ResourceLoadStatus.Forbidden, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
    }

    [TestMethod]
    public async Task MissingOwnerNeverFallsBackToAnUnscopedRead()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Unexpected unscoped read."));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("data-source-profile-a") with { ScopeRef = default });
        Assert.AreEqual(0, handler.Reads);
        Assert.AreEqual(ResourceLoadStatus.Failed, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
    }

    [TestMethod]
    public async Task NamespaceMismatchRejectsTheResponse()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(Resource("data-source-profile-a") with { Metadata = new() { Name = "data-source-profile-a", Namespace = ResourceNamespace.Parse("other") } })));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("data-source-profile-a"));
        Assert.AreEqual(ResourceLoadStatus.Forbidden, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
        Assert.IsEmpty(cut.FindComponents<DataSourceProfileResourceSummary>());
    }

    private static ScopedResourceAddress Target(string name) => ScopedResourceAddress.Create(Scope, ResourceNamespace.Default, DataSourceResourceKinds.DataSourceProfile, name);
    private static DataSourceProfileResource Resource(string name) => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = DataSourceResourceKinds.DataSourceProfile,
        Metadata = new() { Name = name },
        ScopeRef = Scope,
        Uid = Guid.NewGuid(),
        Definition = new() { DisplayName = "Saved " + name, AcquisitionFlow = new() { Name = "acquire", Version = "1.0.0" } }
    };
    private static HttpResponseMessage Response(DataSourceProfileResource resource)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(resource) };
        response.Headers.ETag = new("\"preview\"");
        return response;
    }
    private static IRenderedComponent<DataSourceProfileResourcePreview> Render(BunitContext context, ScopedResourceAddress item) => context.Render<DataSourceProfileResourcePreview>(p => p
        .Add(c => c.Address, item).Add(c => c.OverviewHref, "/data-source-profiles/" + item.Name + "#overview")
        .Add(c => c.DefinitionHref, "/data-source-profiles/" + item.Name + "#definition/configuration"));
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
        context.Services.AddSingleton<IDataSourceProfilesClient>(new DataSourceProfilesApiClient(http));
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
            Assert.Contains("/api/datasourceprofiles/", LastPath);
            return respond(request, cancellationToken);
        }
    }
}
