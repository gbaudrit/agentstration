using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Tools;
using Agentstration.Web.Components;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class ToolDefinitionResourcePreviewTests
{
    private static readonly ResourceScopeRef Scope = ResourceScopeRef.Workspace(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001"));

    [TestMethod]
    [DataRow("en-US", "Identity", "Configuration")]
    [DataRow("fr-FR", "Identité", "Configuration")]
    public async Task PreviewReusesSavedIdentityAndConfigurationWithOneExactRead(string locale, string identity, string configuration)
    {
        using var culture = new TestCultureScope(locale);
        using var handler = new Handler((_, _) => Task.FromResult(Response(Resource("tool-definition-a"))));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("tool-definition-a"));
        Assert.AreEqual(1, handler.Reads);
        Assert.AreEqual($"/api/tooldefinitions/tool-definition-a?namespace=default&scopeRef={Uri.EscapeDataString(Scope.Value)}", handler.LastPath);
        Assert.HasCount(1, cut.FindComponents<ToolDefinitionResourceSummary>());
        Assert.HasCount(1, cut.FindComponents<ResourceIdentitySummary>());
        Assert.AreEqual(identity, cut.Find("[data-testid=tool-definition-identity-summary] h2").TextContent);
        Assert.AreEqual(configuration, cut.Find("[data-testid=tool-definition-configuration-summary] h2").TextContent);
        Assert.Contains("Saved tool-definition-a", cut.Find("[data-testid=tool-definition-identity-summary]").TextContent);
        var identityToggle = cut.Find("[data-testid=tool-definition-identity-summary] .panel-disclosure");
        Assert.AreEqual("true", identityToggle.GetAttribute("aria-expanded"));
        await identityToggle.ClickAsync(new());
        Assert.AreEqual("false", identityToggle.GetAttribute("aria-expanded"));
        Assert.IsTrue(cut.Find("[data-testid=tool-definition-identity-summary] .form-section-body").HasAttribute("hidden"));
        await identityToggle.ClickAsync(new());
        Assert.AreEqual("true", identityToggle.GetAttribute("aria-expanded"));
        Assert.Contains("saved-flow", cut.Find("[data-testid=tool-definition-configuration-summary]").TextContent);
        Assert.AreEqual("true", cut.Find("[data-testid=tool-definition-configuration-summary] .panel-disclosure").GetAttribute("aria-expanded"));
        Assert.HasCount(1, cut.FindAll("[data-testid=tool-definition-preview-open]"));
        Assert.HasCount(1, cut.FindAll("[data-testid=tool-definition-preview-edit]"));
        Assert.AreEqual(locale == "fr-FR" ? "Modifier la définition" : "Edit definition", cut.Find("[data-testid=tool-definition-preview-edit]").TextContent.Trim());
        Assert.IsEmpty(cut.FindAll("form, [data-testid=tool-definition-summary-reconcile], .declaration-resolution-grid, .collection-view-switch"));
        cut.Render(p => p.Add(c => c.Address, Target("tool-definition-a")));
        Assert.AreEqual(1, handler.Reads, "A presentation rerender must not refetch the definition.");
    }

    [TestMethod]
    [DataRow("en-US", false)]
    [DataRow("fr-FR", false)]
    [DataRow("en-US", true)]
    [DataRow("fr-FR", true)]
    public async Task RelayAndAcquisitionSummariesNeedOnlyTheirSavedDefinition(string locale, bool acquisition)
    {
        using var culture = new TestCultureScope(locale);
        var saved = Resource("tool-definition-a") with
        {
            Definition = acquisition
                ? new AcquisitionToolDefinitionProperties { DisplayName = "Saved acquisition", Acquisition = new("saved-template", ResourceNamespace.Default, "saved-artifact", "durable") }
                : new RelayToolDefinitionProperties { DisplayName = "Saved relay", Target = new() { Name = "saved-source", ScopeRef = Scope, Uid = Guid.NewGuid(), Generation = 17 } }
        };
        using var handler = new Handler((_, _) => Task.FromResult(Response(saved)));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("tool-definition-a"));
        Assert.AreEqual(1, handler.Reads);
        Assert.Contains(acquisition ? "saved-template" : "saved-source", cut.Find("[data-testid=tool-definition-configuration-summary]").TextContent);
        if (!acquisition) Assert.Contains("17", cut.Find("[data-testid=tool-definition-configuration-summary]").TextContent);
        Assert.IsEmpty(cut.FindAll("form, .collection-view-switch"));
    }

    [TestMethod]
    public async Task OwnerMismatchNeverRendersTheOtherWorkspacesResource()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(Resource("tool-definition-a") with { ScopeRef = ResourceScopeRef.Instance })));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("tool-definition-a"));
        Assert.AreEqual(ResourceLoadStatus.Forbidden, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
        Assert.IsEmpty(cut.FindComponents<ToolDefinitionResourceSummary>());
        Assert.DoesNotContain("Saved tool-definition-a", cut.Markup);
    }

    [TestMethod]
    public async Task InheritedResourceOffersInspectionWithoutAnEditShortcut()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(Resource("tool-definition-a"))));
        using var context = await ContextAsync(handler, selectedScope: "/instance");
        var cut = Render(context, Target("tool-definition-a"));
        Assert.HasCount(1, cut.FindAll("[data-testid=tool-definition-preview-open]"));
        Assert.IsEmpty(cut.FindAll("[data-testid=tool-definition-preview-edit], [data-testid=tool-definition-preview-designer]"));
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
        var cut = Render(context, Target("tool-definition-a"));
        Assert.AreEqual(expected, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
        Assert.DoesNotContain("Private transport detail", cut.Markup);
        Assert.IsEmpty(cut.FindComponents<ToolDefinitionResourceSummary>());
    }

    [TestMethod]
    public async Task RetryRepeatsOnlyTheSelectedResourceRead()
    {
        var calls = 0;
        using var handler = new Handler((_, _) => Task.FromResult(++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : Response(Resource("tool-definition-a"))));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("tool-definition-a"));
        Assert.AreEqual(ResourceLoadStatus.Failed, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
        await cut.Find("[data-testid=tool-definition-preview-retry]").ClickAsync(new());
        Assert.HasCount(1, cut.FindComponents<ToolDefinitionResourceSummary>());
        Assert.AreEqual(2, handler.Reads);
    }

    [TestMethod]
    public async Task ReplacingSelectionCancelsAndRejectsThePreviousLateResponse()
    {
        var old = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler((request, _) => request.RequestUri!.AbsolutePath.EndsWith("tool-definition-a", StringComparison.Ordinal)
            ? old.Task : Task.FromResult(Response(Resource("tool-definition-b"))));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("tool-definition-a"));
        var oldToken = handler.LastToken;
        Assert.HasCount(1, cut.FindComponents<LoadingState>());
        cut.Render(p => p.Add(c => c.Address, Target("tool-definition-b")));
        Assert.IsTrue(oldToken.IsCancellationRequested);
        cut.WaitForAssertion(() => Assert.Contains("Saved tool-definition-b", cut.Markup));
        var renders = cut.RenderCount;
        old.SetResult(Response(Resource("tool-definition-a")));
        cut.WaitForState(() => cut.RenderCount > renders);
        Assert.Contains("Saved tool-definition-b", cut.Markup);
        Assert.DoesNotContain("Saved tool-definition-a", cut.Markup);
    }

    [TestMethod]
    public async Task ClosingCancelsThePendingRead()
    {
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler((_, _) => response.Task);
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("tool-definition-a"));
        var token = handler.LastToken;
        await context.DisposeComponentsAsync();
        Assert.IsTrue(token.IsCancellationRequested);
        response.SetResult(Response(Resource("tool-definition-a")));
    }

    [TestMethod]
    public async Task MissingReadPermissionPerformsNoRequest()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Unexpected read."));
        using var context = await ContextAsync(handler, permissions: new HashSet<string>());
        var cut = Render(context, Target("tool-definition-a"));
        Assert.AreEqual(0, handler.Reads);
        Assert.AreEqual(ResourceLoadStatus.Forbidden, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
    }

    [TestMethod]
    public async Task MissingOwnerNeverFallsBackToAnUnscopedRead()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Unexpected unscoped read."));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("tool-definition-a") with { ScopeRef = default });
        Assert.AreEqual(0, handler.Reads);
        Assert.AreEqual(ResourceLoadStatus.Failed, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
    }

    [TestMethod]
    public async Task NamespaceMismatchRejectsTheResponse()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(Resource("tool-definition-a") with { Metadata = new() { Name = "tool-definition-a", Namespace = ResourceNamespace.Parse("other") } })));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("tool-definition-a"));
        Assert.AreEqual(ResourceLoadStatus.Forbidden, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
        Assert.IsEmpty(cut.FindComponents<ToolDefinitionResourceSummary>());
    }

    private static ScopedResourceAddress Target(string name) => ScopedResourceAddress.Create(Scope, ResourceNamespace.Default, ToolResourceKinds.ToolDefinition, name);
    private static ToolDefinitionResource Resource(string name) => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = ToolResourceKinds.ToolDefinition,
        Metadata = new() { Name = name },
        ScopeRef = Scope,
        Uid = Guid.NewGuid(),
        Definition = new FlowToolDefinitionProperties { DisplayName = "Saved " + name, Flow = new() { Name = "saved-flow", ScopeRef = Scope } }
    };
    private static HttpResponseMessage Response(ToolDefinitionResource resource)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(resource) };
        response.Headers.ETag = new("\"preview\"");
        return response;
    }
    private static IRenderedComponent<ToolDefinitionResourcePreview> Render(BunitContext context, ScopedResourceAddress item) => context.Render<ToolDefinitionResourcePreview>(p => p
        .Add(c => c.Address, item).Add(c => c.OverviewHref, "/tooldefinitions/" + item.Name + "#overview")
        .Add(c => c.DefinitionHref, "/tooldefinitions/" + item.Name + "#definition/configuration"));
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
        context.Services.AddSingleton<IToolDefinitionsClient>(new ToolDefinitionsApiClient(http));
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
            Assert.Contains("/api/tooldefinitions/", LastPath);
            return respond(request, cancellationToken);
        }
    }
}
