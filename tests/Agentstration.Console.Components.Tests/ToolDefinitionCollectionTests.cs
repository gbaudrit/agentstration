using System.Net;
using System.Net.Http.Json;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Tools;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class ToolDefinitionCollectionTests
{
    private static readonly ResourceScopeRef WorkspaceScope = ResourceScopeRef.Workspace(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));

    [TestMethod]
    [DataRow("en-US", "No matching results")]
    [DataRow("fr-FR", "Aucun résultat correspondant")]
    public async Task FiltersUseTheWholeCatalogAndCardsSurviveNoMatches(string cultureName, string noMatches)
    {
        using var culture = new TestCultureScope(cultureName);
        using var handler = new Handler(_ => Task.FromResult(Response(45)));
        using var context = Context(handler);
        var cut = context.Render<ToolDefinitions>();
        cut.WaitForAssertion(() => Assert.HasCount(20, cut.FindAll("tbody tr")));
        Assert.HasCount(20, cut.FindAll(".collection-actions-menu a[href*=\"definition/configuration\"]"));
        Assert.AreEqual(cultureName == "fr-FR" ? "Modifier la définition" : "Edit definition", cut.Find(".collection-actions-menu a[href*=\"definition/configuration\"]").TextContent.Trim());
        Assert.HasCount(20, cut.FindAll(".collection-actions-menu"));
        var originalUrl = cut.Find(".resource-name-link").GetAttribute("href");
        StringAssert.Contains(originalUrl!, "/tools/definitions/definition-0?namespace=team&scopeRef=%2Fworkspaces%2Faaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa&context=%2Fworkspaces%2Faaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        Assert.HasCount(5, cut.FindAll(".resource-tabs a"));
        await cut.FindAll(".collection-page-number")[2].ClickAsync(new());
        Assert.HasCount(5, cut.FindAll("tbody tr"));
        await cut.Find("[data-filter-key=kind] select").ChangeAsync(new() { Value = "Flow" });
        Assert.HasCount(20, cut.FindAll("tbody tr"));
        await cut.FindAll(".collection-view-switch button")[1].ClickAsync(new());
        await cut.Find(".resource-toolbar button[aria-controls]").ClickAsync(new());
        await cut.Find("[data-filter-key=namespace] select").ChangeAsync(new() { Value = "team" });
        Assert.HasCount(8, cut.FindAll(".collection-card"));
        await cut.Find("input[type=search]").InputAsync(new() { Value = "absent" });
        Assert.AreEqual(noMatches, cut.Find(".empty-state h2").TextContent);
        Assert.IsEmpty(cut.FindAll(".collection-card, .collection-pager"));
        await cut.Find(".empty-state button").ClickAsync(new());
        Assert.HasCount(20, cut.FindAll(".collection-card"));
        Assert.AreEqual(1, handler.Requests);
    }

    [TestMethod]
    public async Task RefreshFailureKeepsRowsAndShrinkClampsTheSuccessfulPage()
    {
        using var handler = new Handler(call => call == 2 ? throw new HttpRequestException("Offline") : Task.FromResult(Response(call == 1 ? 45 : 3)));
        using var context = Context(handler);
        var cut = context.Render<ToolDefinitions>();
        await cut.FindAll(".collection-page-number")[2].ClickAsync(new());
        await cut.Find("[data-testid=resource-tool-definitions-refresh]").ClickAsync(new());
        Assert.HasCount(5, cut.FindAll("tbody tr"));
        Assert.IsNotNull(cut.Find("[role=alert]"));
        await cut.Find("[role=alert] button").ClickAsync(new());
        Assert.HasCount(3, cut.FindAll("tbody tr"));
        Assert.IsEmpty(cut.FindAll(".collection-page-number"));
        Assert.AreEqual(3, handler.Requests);
    }

    [TestMethod]
    public async Task ContextReplacementRejectsLateResultsAndForbiddenRefreshClearsCachedRows()
    {
        var old = new TaskCompletionSource<HttpResponseMessage>();
        var replacement = new TaskCompletionSource<HttpResponseMessage>();
        var provider = new ResourceListContextProvider { Scope = WorkspaceScope.Value };
        using var handler = new Handler(call => call switch
        {
            1 => Task.FromResult(Response(1)),
            2 => old.Task,
            3 => replacement.Task,
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden))
        });
        using var context = Context(handler, provider);
        var cut = context.Render<ToolDefinitions>();
        var refresh = cut.Find("[data-testid=resource-tool-definitions-refresh]").ClickAsync(new());
        provider.Scope = $"/workspaces/{Guid.NewGuid():D}";
        await cut.InvokeAsync(() => context.Services.GetRequiredService<ConsoleContextState>().LoadAsync(CancellationToken.None));
        cut.WaitForAssertion(() => Assert.IsEmpty(cut.FindAll(".resource-name-link")));
        replacement.SetResult(Response(2));
        cut.WaitForAssertion(() => Assert.HasCount(2, cut.FindAll(".resource-name-link")));
        old.SetResult(Response(4));
        await refresh;
        Assert.HasCount(2, cut.FindAll(".resource-name-link"));
        await cut.Find("[data-testid=resource-tool-definitions-refresh]").ClickAsync(new());
        Assert.IsEmpty(cut.FindAll(".resource-name-link, a[href*='/tools/definitions/new']"));
    }

    [TestMethod]
    public void DeniedReadMakesNoRequestAndReaderHasNoCreateCommand()
    {
        var provider = new ResourceListContextProvider { Scope = WorkspaceScope.Value, Permissions = new HashSet<string>() };
        using var handler = new Handler(_ => Task.FromResult(Response(1)));
        using (var context = Context(handler, provider))
        {
            var cut = context.Render<ToolDefinitions>();
            Assert.IsEmpty(cut.FindAll(".resource-name-link, a[href*='/tools/definitions/new']"));
            Assert.AreEqual(0, handler.Requests);
        }
        provider.Permissions = new HashSet<string> { AuthorizationPermissions.ResourcesRead };
        using var reader = Context(handler, provider);
        var list = reader.Render<ToolDefinitions>();
        Assert.HasCount(1, list.FindAll(".resource-name-link"));
        Assert.IsEmpty(list.FindAll("a[href*='/tools/definitions/new']"));
    }

    [TestMethod]
    public async Task MixedKindsKeepTargetsApprovalStateAndOverviewCommands()
    {
        using var culture = new TestCultureScope("en-US");
        using var handler = new Handler(_ => Task.FromResult(Response(45)));
        using var context = Context(handler);
        var cut = context.Render<ToolDefinitions>();
        await cut.Find("[data-filter-key=kind] select").ChangeAsync(new() { Value = "Acquisition" });
        Assert.HasCount(1, cut.FindAll("tbody tr"));
        StringAssert.Contains(cut.Find("tbody tr").TextContent, "acquisition-ns/template-1");
        await cut.Find("[data-filter-key=kind] select").ChangeAsync(new() { Value = "Relay" });
        await cut.Find("[data-filter-key=approval] select").ChangeAsync(new() { Value = "false" });
        await cut.Find("[data-filter-key=enabled] select").ChangeAsync(new() { Value = "false" });
        Assert.HasCount(7, cut.FindAll("tbody tr"));
        StringAssert.Contains(cut.Find("tbody tr").TextContent, "relay-ns/tool-3 · 17");
        Assert.AreEqual(cut.Find(".resource-name-link").GetAttribute("href"), cut.Find(".collection-actions-menu a[href$=\"#overview\"]").GetAttribute("href"));
        Assert.AreEqual(1, handler.Requests);
    }

    private static HttpResponseMessage Response(int count) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new
        {
            Value = Enumerable.Range(0, count).Select(index => new ToolDefinitionResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = ToolResourceKinds.ToolDefinition,
                Metadata = new() { Name = $"definition-{index}", Namespace = ResourceNamespace.Parse(index % 3 == 0 ? "team" : "other") },
                ScopeRef = WorkspaceScope,
                Definition = Definition(index)
            }).ToArray()
        })
    };
    private static ToolDefinitionProperties Definition(int index)
    {
        ToolDefinitionProperties definition = index == 1
            ? new AcquisitionToolDefinitionProperties { DisplayName = $"Definition {index}", Acquisition = new($"template-{index}", ResourceNamespace.Parse("acquisition-ns"), null, "durable") }
            : index % 2 == 0
                ? new FlowToolDefinitionProperties { DisplayName = $"Definition {index}", Flow = new() { Name = $"flow-{index}", Namespace = ResourceNamespace.Parse("flow-ns"), Version = "2.4.0", UseActiveVersion = false } }
                : new RelayToolDefinitionProperties { DisplayName = $"Definition {index}", Target = new() { Name = $"tool-{index}", Namespace = ResourceNamespace.Parse("relay-ns"), ScopeRef = WorkspaceScope, Uid = Guid.NewGuid(), Generation = 17 } };
        return definition with { Enabled = index % 3 != 0, RequiresApproval = index % 2 == 0 };
    }
    private static BunitContext Context(Handler handler, ResourceListContextProvider? provider = null)
    {
        var context = new BunitContext().WithCollectionPresentation().WithResourceListContext(provider ?? new ResourceListContextProvider { Scope = WorkspaceScope.Value });
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddSingleton<IConsoleNavigationContext>(new NavigationContext());
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IToolDefinitionsClient>(new ToolDefinitionsApiClient(new HttpClient(handler, false) { BaseAddress = new("https://agentstration.test/") }));
        return context;
    }
    private sealed class NavigationContext : IConsoleNavigationContext
    {
        public string? ScopeRef => WorkspaceScope.Value;
        public string AddToUri(string uri) => uri + "?context=" + Uri.EscapeDataString(WorkspaceScope.Value);
    }
    private sealed class Handler(Func<int, Task<HttpResponseMessage>> load) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => load(++Requests);
    }
}
