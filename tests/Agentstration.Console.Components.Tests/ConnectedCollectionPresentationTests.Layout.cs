using System.Net;
using System.Net.Http.Json;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

public sealed partial class ConnectedCollectionPresentationTests
{
    [TestMethod]
    public async Task ColumnAndExtraFiltersApplyToTheWholeCatalogAndRetainCardsAcrossEmptyStates()
    {
        using var culture = new TestCultureScope("en-US");
        var resources = Enumerable.Range(0, 45).Select(index => CatalogService(index)).ToArray();
        using var handler = new CatalogHandler(_ => Task.FromResult(CatalogResponse(resources)));
        using var context = CatalogContext(handler);
        var cut = context.Render<Agentstration.Web.Components.Pages.ConnectedServices>();
        cut.WaitForAssertion(() => Assert.HasCount(20, cut.FindAll("[data-testid=connected-service-row]")));
        await cut.Find(".collection-page-number[aria-label='Page 3']").ClickAsync(new());
        Assert.HasCount(5, cut.FindAll("[data-testid=connected-service-row]"));
        await cut.Find("[data-filter-key=type] select").ChangeAsync(new() { Value = "http" });
        Assert.HasCount(20, cut.FindAll("[data-testid=connected-service-row]"));
        StringAssert.Contains(cut.Find(".collection-pager").TextContent, "23 results");
        Assert.AreEqual("page", cut.Find("button[aria-label='Page 1']").GetAttribute("aria-current"));
        await cut.FindAll(".collection-view-switch button")[1].ClickAsync(new());
        await cut.Find(".resource-toolbar button[aria-controls]").ClickAsync(new());
        await cut.Find("[data-filter-key=namespace] select").ChangeAsync(new() { Value = "team" });
        Assert.HasCount(8, cut.FindAll("li[data-testid=connected-service-row]"));
        Assert.HasCount(2, cut.FindAll(".collection-filter-chip"));
        await cut.Find(".search-box input").InputAsync(new() { Value = "absent" });
        Assert.IsEmpty(cut.FindAll("table, .collection-card, .collection-pager"));
        Assert.AreEqual("No matching results", cut.Find(".empty-state h2").TextContent);
        await cut.Find(".empty-state button").ClickAsync(new());
        Assert.HasCount(20, cut.FindAll(".collection-card"));
        Assert.IsEmpty(cut.FindAll(".collection-filter-chip"));
        Assert.AreEqual(1, handler.Requests, "Local filtering and paging must not introduce catalog refetching.");
    }

    [TestMethod]
    public async Task FailedRefreshRetainsFiltersAndRowsAndSuccessfulShrinkCorrectsThePage()
    {
        using var culture = new TestCultureScope("en-US");
        var resources = Enumerable.Range(0, 45).Select(index => CatalogService(index)).ToArray();
        using var handler = new CatalogHandler(call => call == 2
            ? throw new HttpRequestException("Offline") : Task.FromResult(CatalogResponse(call == 1 ? resources : resources.Take(5).ToArray())));
        using var context = CatalogContext(handler);
        var cut = context.Render<Agentstration.Web.Components.Pages.ConnectedServices>();
        await cut.Find("[data-filter-key=type] select").ChangeAsync(new() { Value = "http" });
        await cut.Find(".collection-page-number[aria-label='Page 2']").ClickAsync(new());
        Assert.HasCount(3, cut.FindAll("[data-testid=connected-service-row]"));
        await cut.Find("[data-testid=connected-services-refresh]").ClickAsync(new());
        Assert.HasCount(3, cut.FindAll("[data-testid=connected-service-row]"));
        Assert.HasCount(1, cut.FindAll(".collection-filter-chip"));
        StringAssert.Contains(cut.Find("[role=alert]").TextContent, "Unable to update results");
        await cut.Find("[role=alert] button").ClickAsync(new());
        Assert.HasCount(3, cut.FindAll("[data-testid=connected-service-row]"));
        Assert.IsEmpty(cut.FindAll(".collection-page-number"));
        StringAssert.Contains(cut.Find(".collection-pager").TextContent, "Page 1");
        Assert.AreEqual(3, handler.Requests);
    }

    [TestMethod]
    public async Task ContextChangeImmediatelyClearsRowsAndLateResponsesCannotRestoreAnotherScope()
    {
        using var culture = new TestCultureScope("en-US");
        var oldResponse = new TaskCompletionSource<HttpResponseMessage>();
        var newResponse = new TaskCompletionSource<HttpResponseMessage>();
        var provider = new ResourceListContextProvider();
        using var handler = new CatalogHandler(call => call switch
        {
            1 => Task.FromResult(CatalogResponse([CatalogService(0)])),
            2 => oldResponse.Task,
            _ => newResponse.Task
        });
        using var context = CatalogContext(handler, provider);
        var cut = context.Render<Agentstration.Web.Components.Pages.ConnectedServices>();
        var refresh = cut.Find("[data-testid=connected-services-refresh]").ClickAsync(new());
        cut.WaitForAssertion(() => Assert.AreEqual(2, handler.Requests));
        provider.Scope = $"/workspaces/{Guid.NewGuid():D}";
        await cut.InvokeAsync(() => context.Services.GetRequiredService<ConsoleContextState>().LoadAsync(CancellationToken.None));
        cut.WaitForAssertion(() => Assert.IsEmpty(cut.FindAll("[data-testid=connected-service-row]")));
        newResponse.SetResult(CatalogResponse([CatalogService(8) with { ScopeRef = ResourceScopeRef.Parse(provider.Scope) }]));
        cut.WaitForAssertion(() => Assert.AreEqual("Service 8", cut.Find(".resource-name-link").TextContent));
        oldResponse.SetResult(CatalogResponse([CatalogService(2)]));
        await refresh;
        Assert.AreEqual("Service 8", cut.Find(".resource-name-link").TextContent);
        Assert.AreEqual(3, handler.Requests);
    }

    [TestMethod]
    public async Task ForbiddenRefreshRemovesCachedRowsAndCreationCommands()
    {
        using var culture = new TestCultureScope("en-US");
        using var handler = new CatalogHandler(call => Task.FromResult(call == 1 ? CatalogResponse([CatalogService(0)]) : new HttpResponseMessage(HttpStatusCode.Forbidden)));
        using var context = CatalogContext(handler);
        var cut = context.Render<Agentstration.Web.Components.Pages.ConnectedServices>();
        await cut.Find("[data-testid=connected-services-refresh]").ClickAsync(new());
        Assert.IsEmpty(cut.FindAll("[data-testid=connected-service-row], a[href='/connected-services/new']"));
        Assert.AreEqual("Access denied", cut.Find("[role=alert] h2").TextContent);
    }

    private static ConnectedServiceResource CatalogService(int index)
    {
        var resource = Service(ResourceScopeRef.Instance, index % 3 == 0 ? "team" : "other", false);
        return resource with
        {
            Metadata = resource.Metadata with { Name = $"service-{index}" },
            Definition = resource.Definition with
            {
                DisplayName = $"Service {index}",
                Type = resource.Definition.Type with { Type = new(index % 2 == 0 ? "http" : "redis", ResourceScopeRef.Instance) }
            }
        };
    }
    private static HttpResponseMessage CatalogResponse(IReadOnlyList<ConnectedServiceResource> resources) => new(HttpStatusCode.OK) { Content = JsonContent.Create(resources) };
    private static BunitContext CatalogContext(CatalogHandler handler, ResourceListContextProvider? provider = null)
    {
        var context = new BunitContext().WithCollectionPresentation().WithResourceListContext(provider);
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IConnectedServicesClient>(new ConnectedServicesApiClient(new HttpClient(handler, false) { BaseAddress = new("https://agentstration.test/") }));
        return context;
    }
    private sealed class CatalogHandler(Func<int, Task<HttpResponseMessage>> load) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => load(++Requests);
    }
}
