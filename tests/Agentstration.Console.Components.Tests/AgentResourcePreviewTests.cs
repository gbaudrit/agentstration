using System.Net;
using System.Net.Http.Json;
using Agentstration.Agents;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class AgentResourcePreviewTests
{
    private static readonly ResourceScopeRef Scope = ResourceScopeRef.Workspace(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001"));

    [TestMethod]
    [DataRow("en-US", "Identity", "Configuration")]
    [DataRow("fr-FR", "Identité", "Configuration")]
    public async Task PreviewReusesSavedSummaryWithOneExactReadAndNoModelOrEditorLoad(string locale, string identity, string configuration)
    {
        using var culture = new TestCultureScope(locale);
        using var handler = new Handler((_, _) => Task.FromResult(Response(Resource("agent-a"))));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("agent-a"));
        Assert.AreEqual(1, handler.Reads);
        Assert.AreEqual($"/api/agents/agent-a?scopeRef={Uri.EscapeDataString(Scope.Value)}", handler.LastPath);
        Assert.HasCount(1, cut.FindComponents<AgentResourceSummary>());
        Assert.HasCount(1, cut.FindComponents<ResourceIdentitySummary>());
        Assert.AreEqual(identity, cut.Find("[data-testid=agent-preview-identity] h2").TextContent);
        Assert.AreEqual(configuration, cut.Find("[data-testid=agent-configuration-summary] h2").TextContent);
        Assert.Contains("Saved agent-a", cut.Find("[data-testid=agent-preview-identity]").TextContent);
        var identityToggle = cut.Find("[data-testid=agent-preview-identity] .panel-disclosure");
        Assert.AreEqual("true", identityToggle.GetAttribute("aria-expanded"));
        await identityToggle.ClickAsync(new());
        Assert.AreEqual("false", identityToggle.GetAttribute("aria-expanded"));
        Assert.IsTrue(cut.Find("[data-testid=agent-preview-identity] .form-section-body").HasAttribute("hidden"));
        await identityToggle.ClickAsync(new());
        Assert.AreEqual("true", identityToggle.GetAttribute("aria-expanded"));
        Assert.Contains("Saved instructions agent-a", cut.Find("[data-testid=agent-configuration-summary]").TextContent);
        Assert.AreEqual("true", cut.Find("[data-testid=agent-configuration-summary] .panel-disclosure").GetAttribute("aria-expanded"));
        Assert.HasCount(1, cut.FindAll("[data-testid=agent-preview-open]"));
        Assert.HasCount(1, cut.FindAll("[data-testid=agent-preview-edit]"));
        Assert.HasCount(1, cut.FindAll("[data-testid=agent-summary-run]"));
        Assert.IsEmpty(cut.FindAll("form, [data-testid=agent-summary-reconcile], .declaration-resolution-grid, .collection-view-switch"));
        cut.Render(p => p.Add(c => c.Address, Target("agent-a")));
        Assert.AreEqual(1, handler.Reads, "A presentation rerender must not refetch the definition.");
    }

    [TestMethod]
    public async Task OwnerMismatchNeverRendersTheOtherWorkspacesResource()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(Resource("agent-a") with { ScopeRef = ResourceScopeRef.Instance })));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("agent-a"));
        Assert.AreEqual(ResourceLoadStatus.Forbidden, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
        Assert.IsEmpty(cut.FindComponents<AgentResourceSummary>());
        Assert.DoesNotContain("Saved instructions", cut.Markup);
    }

    [TestMethod]
    public async Task InheritedResourceOffersInspectionWithoutAnEditShortcut()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(Resource("agent-a"))));
        using var context = await ContextAsync(handler, selectedScope: "/instance");
        var cut = Render(context, Target("agent-a"));
        Assert.HasCount(1, cut.FindAll("[data-testid=agent-preview-open]"));
        Assert.IsEmpty(cut.FindAll("[data-testid=agent-preview-edit], [data-testid=agent-summary-reconcile]"));
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
        var cut = Render(context, Target("agent-a"));
        Assert.AreEqual(expected, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
        Assert.DoesNotContain("Private transport detail", cut.Markup);
        Assert.IsEmpty(cut.FindComponents<AgentResourceSummary>());
    }

    [TestMethod]
    public async Task RetryRepeatsOnlyTheSelectedResourceRead()
    {
        var calls = 0;
        using var handler = new Handler((_, _) => Task.FromResult(++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : Response(Resource("agent-a"))));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("agent-a"));
        Assert.AreEqual(ResourceLoadStatus.Failed, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
        await cut.Find("[data-testid=agent-preview-retry]").ClickAsync(new());
        Assert.HasCount(1, cut.FindComponents<AgentResourceSummary>());
        Assert.AreEqual(2, handler.Reads);
    }

    [TestMethod]
    public async Task ReplacingSelectionCancelsAndRejectsThePreviousLateResponse()
    {
        var old = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler((request, _) => request.RequestUri!.AbsolutePath.EndsWith("agent-a", StringComparison.Ordinal)
            ? old.Task : Task.FromResult(Response(Resource("agent-b"))));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("agent-a"));
        var oldToken = handler.LastToken;
        Assert.HasCount(1, cut.FindComponents<LoadingState>());
        cut.Render(p => p.Add(c => c.Address, Target("agent-b")));
        Assert.IsTrue(oldToken.IsCancellationRequested);
        cut.WaitForAssertion(() => Assert.Contains("Saved agent-b", cut.Markup));
        var renders = cut.RenderCount;
        old.SetResult(Response(Resource("agent-a")));
        cut.WaitForState(() => cut.RenderCount > renders);
        Assert.Contains("Saved agent-b", cut.Markup);
        Assert.DoesNotContain("Saved instructions agent-a", cut.Markup);
    }

    [TestMethod]
    public async Task ClosingCancelsThePendingRead()
    {
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler((_, _) => response.Task);
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("agent-a"));
        var token = handler.LastToken;
        await context.DisposeComponentsAsync();
        Assert.IsTrue(token.IsCancellationRequested);
        response.SetResult(Response(Resource("agent-a")));
    }

    [TestMethod]
    public async Task MissingReadPermissionPerformsNoRequest()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Unexpected read."));
        using var context = await ContextAsync(handler, permissions: new HashSet<string>());
        var cut = Render(context, Target("agent-a"));
        Assert.AreEqual(0, handler.Reads);
        Assert.AreEqual(ResourceLoadStatus.Forbidden, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
    }

    [TestMethod]
    public async Task MissingOwnerNeverFallsBackToAnUnscopedRead()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Unexpected unscoped read."));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("agent-a") with { ScopeRef = default });
        Assert.AreEqual(0, handler.Reads);
        Assert.AreEqual(ResourceLoadStatus.Failed, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
    }

    [TestMethod]
    public async Task LosingReadPermissionClearsTheSnapshotWithoutAnotherRead()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(Resource("agent-a"))));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("agent-a"));
        var provider = (ResourceListContextProvider)context.Services.GetRequiredService<IConsoleContextProvider>();
        provider.Permissions = new HashSet<string>();
        await cut.InvokeAsync(() => context.Services.GetRequiredService<ConsoleContextState>().LoadAsync(default));
        Assert.IsEmpty(cut.FindComponents<AgentResourceSummary>());
        Assert.AreEqual(ResourceLoadStatus.Forbidden, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
        Assert.AreEqual(1, handler.Reads);
    }

    [TestMethod]
    public async Task WrongKindCannotMasqueradeAsTheSelectedAgent()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(Resource("agent-a") with { Kind = "Other" })));
        using var context = await ContextAsync(handler);
        var cut = Render(context, Target("agent-a"));
        Assert.IsEmpty(cut.FindComponents<AgentResourceSummary>());
        Assert.AreEqual(ResourceLoadStatus.Forbidden, cut.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
    }

    private static ScopedResourceAddress Target(string name) => ScopedResourceAddress.Create(Scope, ResourceNamespace.Default, AgentResourceKinds.Agent, name);
    private static AgentResource Resource(string name) => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = AgentResourceKinds.Agent,
        Uid = Guid.NewGuid(),
        ScopeRef = Scope,
        Metadata = new() { Name = name, Tags = new Dictionary<string, string> { ["environment"] = "poc" } },
        Generation = 1,
        Definition = new() { DisplayName = "Saved " + name, Instructions = "Saved instructions " + name, ModelProfile = new("profile", ResourceScopeRef.Instance) }
    };
    private static HttpResponseMessage Response(AgentResource resource)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(resource) };
        response.Headers.ETag = new("\"preview\"");
        return response;
    }
    private static IRenderedComponent<AgentResourcePreview> Render(BunitContext context, ScopedResourceAddress item) => context.Render<AgentResourcePreview>(p => p
        .Add(c => c.Address, item).Add(c => c.OverviewHref, "/agents/" + item.Name + "#overview")
        .Add(c => c.DefinitionHref, "/agents/" + item.Name + "#definition/configuration").Add(c => c.RunHref, "/agents/" + item.Name + "/run"));
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
        context.Services.AddSingleton<IManagementApiClient>(new ManagementApiClient(http));
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
            Assert.Contains("/api/agents/", LastPath);
            return respond(request, cancellationToken);
        }
    }
}
