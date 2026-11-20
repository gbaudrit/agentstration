using System.Net;
using System.Net.Http.Json;
using Agentstration.Extensions.Contracts;
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
public sealed class ToolCollectionTests
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
        var cut = context.Render<Agentstration.Web.Components.Pages.Tools>();
        cut.WaitForAssertion(() => Assert.HasCount(20, cut.FindAll("tbody tr")));
        Assert.IsEmpty(cut.FindAll(".collection-actions, header a"));
        Assert.HasCount(2, cut.FindAll("th"));
        Assert.IsFalse(cut.FindAll("th").Any(heading => heading.TextContent == (cultureName == "fr-FR" ? "Activé" : "Enabled")));
        var originalUrl = cut.Find(".resource-name-link").GetAttribute("href");
        StringAssert.Contains(originalUrl!, "/tools/tool-0?namespace=team&scopeRef=%2Fworkspaces%2Faaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa&context=%2Fworkspaces%2Faaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        Assert.HasCount(5, cut.FindAll(".resource-tabs a"));
        await cut.FindAll(".collection-page-number")[2].ClickAsync(new());
        Assert.HasCount(5, cut.FindAll("tbody tr"));
        await cut.Find(".resource-toolbar button[aria-controls]").ClickAsync(new());
        await cut.Find("[data-filter-key=enabled] select").ChangeAsync(new() { Value = "true" });
        Assert.HasCount(20, cut.FindAll("tbody tr"));
        await cut.FindAll(".collection-view-switch button")[1].ClickAsync(new());
        Assert.HasCount(1, cut.FindAll(".collection-card").First().QuerySelectorAll(".collection-value"));
        await cut.Find("[data-filter-key=namespace] select").ChangeAsync(new() { Value = "team" });
        Assert.HasCount(8, cut.FindAll(".collection-card"));
        await cut.Find("[data-filter-key=availability] select").ChangeAsync(new() { Value = "available" });
        await cut.Find("[data-filter-key=scope] select").ChangeAsync(new() { Value = WorkspaceScope.Value });
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
        var cut = context.Render<Agentstration.Web.Components.Pages.Tools>();
        await cut.FindAll(".collection-page-number")[2].ClickAsync(new());
        await cut.Find("[data-testid=resource-tools-refresh]").ClickAsync(new());
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
        var cut = context.Render<Agentstration.Web.Components.Pages.Tools>();
        var refresh = cut.Find("[data-testid=resource-tools-refresh]").ClickAsync(new());
        provider.Scope = $"/workspaces/{Guid.NewGuid():D}";
        await cut.InvokeAsync(() => context.Services.GetRequiredService<ConsoleContextState>().LoadAsync(CancellationToken.None));
        cut.WaitForAssertion(() => Assert.IsEmpty(cut.FindAll(".resource-name-link")));
        replacement.SetResult(Response(2));
        cut.WaitForAssertion(() => Assert.HasCount(2, cut.FindAll(".resource-name-link")));
        old.SetResult(Response(4));
        await refresh;
        Assert.HasCount(2, cut.FindAll(".resource-name-link"));
        await cut.Find("[data-testid=resource-tools-refresh]").ClickAsync(new());
        Assert.IsEmpty(cut.FindAll(".resource-name-link, a[href*='/tools/new']"));
    }

    [TestMethod]
    public void DeniedReadMakesNoRequestAndReaderCanInspectWithoutInventedCreation()
    {
        var provider = new ResourceListContextProvider { Scope = WorkspaceScope.Value, Permissions = new HashSet<string>() };
        using var handler = new Handler(_ => Task.FromResult(Response(1)));
        using (var context = Context(handler, provider))
        {
            var cut = context.Render<Agentstration.Web.Components.Pages.Tools>();
            Assert.IsEmpty(cut.FindAll(".resource-name-link, a[href*='/tools/new']"));
            Assert.AreEqual(0, handler.Requests);
        }
        provider.Permissions = new HashSet<string> { AuthorizationPermissions.ResourcesRead };
        using var reader = Context(handler, provider);
        var list = reader.Render<Agentstration.Web.Components.Pages.Tools>();
        Assert.HasCount(1, list.FindAll(".resource-name-link"));
        Assert.IsEmpty(list.FindAll("a[href*='/tools/new']"));
    }

    [TestMethod]
    public void OptionalInventoryFailureDoesNotHideToolsOrInventProvenanceFilters()
    {
        using var tools = new Handler(_ => Task.FromResult(Response(2)));
        using var inventory = new Handler(_ => throw new HttpRequestException("Inventory unavailable"));
        using var context = Context(tools);
        context.Services.AddSingleton<IExtensionsClient>(new ExtensionsApiClient(new HttpClient(inventory, false) { BaseAddress = new("https://agentstration.test/") }));
        var cut = context.Render<Agentstration.Web.Components.Pages.Tools>();
        Assert.HasCount(2, cut.FindAll(".resource-name-link"));
        Assert.IsEmpty(cut.FindAll("[role=alert], [data-filter-key=provider], [data-filter-key=extension]"));
        Assert.AreEqual(1, tools.Requests);
        Assert.AreEqual(1, tools.ProviderRequests);
        Assert.AreEqual(1, inventory.Requests, "One optional inventory request for the complete list.");
    }

    private static HttpResponseMessage Response(int count) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new
        {
            Value = Enumerable.Range(0, count).Select(index => new ToolResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = ToolResourceKinds.Tool,
                Metadata = new() { Name = $"tool-{index}", Namespace = ResourceNamespace.Parse(index % 3 == 0 ? "team" : "other") },
                ScopeRef = WorkspaceScope,
                Definition = new()
                {
                    DisplayName = $"Tool {index}",
                    Enabled = index % 2 == 0,
                    ExternalId = $"external-{index}",
                    Discovery = new() { Available = index % 3 == 0, FirstSeenAt = DateTimeOffset.UnixEpoch, LastSeenAt = DateTimeOffset.UnixEpoch }
                }
            }).ToArray()
        })
    };
    private static BunitContext Context(Handler handler, ResourceListContextProvider? provider = null)
    {
        var context = new BunitContext().WithCollectionPresentation().WithResourceListContext(provider ?? new ResourceListContextProvider { Scope = WorkspaceScope.Value });
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IToolsClient>(new ToolsApiClient(new HttpClient(handler, false) { BaseAddress = new("https://agentstration.test/") }));
        return context;
    }
    private sealed class Handler(Func<int, Task<HttpResponseMessage>> load) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public int ProviderRequests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/api/toolproviders")
            {
                ProviderRequests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { Value = Array.Empty<ToolProviderResource>() }) });
            }
            return load(++Requests);
        }
    }
}
