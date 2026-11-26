using System.Net;
using System.Net.Http.Json;
using Agentstration.Identity.Contracts;
using Agentstration.Packs.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using PacksPage = Agentstration.Web.Components.Pages.Packs;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class PacksCollectionTests
{
    private static readonly Guid WorkspaceId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    [TestMethod]
    [DataRow("en-US", "No matching results")]
    [DataRow("fr-FR", "Aucun résultat correspondant")]
    public async Task CompleteCatalogHasExactPagesClassificationFiltersAndUnfilteredMetrics(string culture, string noMatches)
    {
        using var locale = new TestCultureScope(culture);
        using var handler = new Handler(_ => Task.FromResult(Response(45)));
        using var context = Context(handler);
        var cut = context.Render<PacksPage>();
        Assert.HasCount(20, cut.FindAll("[data-testid=installed-pack-row]"));
        Assert.HasCount(20, cut.FindAll(".collection-actions"));
        await cut.FindAll(".collection-page-number")[2].ClickAsync(new());
        Assert.HasCount(5, cut.FindAll("[data-testid=installed-pack-row]"));
        await cut.Find("[data-filter-key=state] select").ChangeAsync(new() { Value = "Failed" });
        Assert.HasCount(15, cut.FindAll("[data-testid=installed-pack-row]"));
        await cut.Find("[data-filter-key=purpose] select").ChangeAsync(new() { Value = "Sample" });
        Assert.HasCount(8, cut.FindAll("[data-testid=installed-pack-row]"));
        await cut.Find(".resource-toolbar button[aria-controls]").ClickAsync(new());
        await cut.Find("[data-filter-key=audience] select").ChangeAsync(new() { Value = "Personal" });
        Assert.HasCount(8, cut.FindAll("[data-testid=installed-pack-row]"));
        await cut.Find("[data-filter-key=publisher] select").ChangeAsync(new() { Value = "contoso" });
        await cut.Find("[data-filter-key=scope] select").ChangeAsync(new() { Value = $"/workspaces/{WorkspaceId:D}" });
        Assert.AreEqual(noMatches, cut.Find(".empty-state h2").TextContent);
        StringAssert.Contains(cut.FindAll(".metric-card")[0].TextContent, "45");
        await cut.Find(".empty-state button").ClickAsync(new());
        await cut.Find("input[type=search]").InputAsync(new() { Value = "Pack 44" });
        await cut.FindAll(".collection-view-switch button")[1].ClickAsync(new());
        Assert.HasCount(1, cut.FindAll(".collection-card"));
        Assert.AreEqual(1, handler.Requests);
    }

    [TestMethod]
    public async Task CompatibleRefreshRetainsRowsAndSuccessfulShrinkClamps()
    {
        using var handler = new Handler(call => call == 2 ? throw new HttpRequestException("Private transport detail") : Task.FromResult(Response(call == 1 ? 45 : 3)));
        using var context = Context(handler);
        var cut = context.Render<PacksPage>();
        await cut.FindAll(".collection-page-number")[2].ClickAsync(new());
        await cut.Find("[data-testid=packs-collection-refresh]").ClickAsync(new());
        Assert.HasCount(5, cut.FindAll("[data-testid=installed-pack-row]"));
        Assert.IsFalse(cut.Markup.Contains("Private transport detail", StringComparison.Ordinal));
        await cut.Find("[role=alert] button").ClickAsync(new());
        Assert.HasCount(3, cut.FindAll("[data-testid=installed-pack-row]"));
        Assert.IsEmpty(cut.FindAll(".collection-page-number"));
    }

    [TestMethod]
    public async Task ContextNotificationClearsMetadataAndRejectsLateCatalogAndDeniedRefresh()
    {
        var old = new TaskCompletionSource<HttpResponseMessage>();
        var next = new TaskCompletionSource<HttpResponseMessage>();
        using var handler = new Handler(call => call switch { 1 => Task.FromResult(Response(1)), 2 => old.Task, 3 => next.Task, _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)) });
        using var context = Context(handler);
        var cut = context.Render<PacksPage>();
        var refresh = cut.Find("[data-testid=packs-collection-refresh]").ClickAsync(new());
        await cut.InvokeAsync(() => context.Services.GetRequiredService<ConsoleContextState>().LoadAsync(CancellationToken.None));
        cut.WaitForAssertion(() => Assert.IsEmpty(cut.FindAll("[data-testid=installed-pack-row], .metric-card")));
        next.SetResult(Response(2));
        cut.WaitForAssertion(() => Assert.HasCount(2, cut.FindAll("[data-testid=installed-pack-row]")));
        old.SetResult(Response(4));
        await refresh;
        Assert.HasCount(2, cut.FindAll("[data-testid=installed-pack-row]"));
        await cut.Find("[data-testid=packs-collection-refresh]").ClickAsync(new());
        Assert.IsEmpty(cut.FindAll("[data-testid=installed-pack-row], .metric-card"));
    }

    [TestMethod]
    public void ReadOnlyViewerPreservesTheCurrentContextAndExactOwnerWithoutReadingTheWrongPack()
    {
        using var handler = new Handler(_ => Task.FromResult(Response(2, sharedIdentity: true)));
        var provider = new ResourceListContextProvider { Permissions = new HashSet<string> { AuthorizationPermissions.ResourcesRead } };
        using var context = Context(handler, provider);
        context.Services.AddSingleton<IConsoleNavigationContext>(new NavigationContext());
        var cut = context.Render<PacksPage>();
        Assert.IsEmpty(cut.FindAll(".page-actions button, .page-actions a"));
        var row = cut.FindAll("[data-testid=installed-pack-row]")[1];
        var href = row.QuerySelector("a.resource-name-link")!.GetAttribute("href")!;
        StringAssert.Contains(href, "/packs/resources/7-contoso-shared?");
        StringAssert.Contains(href, $"scopeRef=%2Fworkspaces%2F{WorkspaceId:D}");
        StringAssert.Contains(href, "context=%2Finstance#overview");
        Assert.IsNull(row.QuerySelector("[data-testid=pack-manage]"));
        Assert.AreEqual(0, handler.ScopedReads);
        Assert.AreEqual(1, handler.Requests);
    }

    [TestMethod]
    public async Task InspectionUsesTheExplicitOwnerQueryAndPreservesTheInlinePanel()
    {
        using var handler = new Handler(_ => Task.FromResult(Response(1)));
        using var context = Context(handler);
        context.Services.AddSingleton<IConsoleNavigationContext>(new NavigationContext());
        var cut = context.Render<PacksPage>();
        await cut.Find("[data-testid=pack-manage]").ClickAsync(new());
        Assert.AreEqual(1, handler.ScopedReads);
        StringAssert.Contains(cut.Find(".pack-details").TextContent, "1.0.0");
    }

    [TestMethod]
    public async Task ManagementNavigationReadsOneExactTargetFromTheLoadedCatalog()
    {
        using var handler = new Handler(_ => Task.FromResult(Response(1)));
        using var context = Context(handler);
        context.Services.AddSingleton<IConsoleNavigationContext>(new NavigationContext());
        var cut = context.Render<PacksPage>();
        await cut.InvokeAsync(() => context.Services.GetRequiredService<NavigationManager>().NavigateTo(
            "/packs?publisher=contoso&name=pack-0&scopeRef=%2Finstance&context=%2Finstance#pack-management"));
        cut.WaitForAssertion(() => Assert.HasCount(1, cut.FindComponents<InstalledPackResourceSummary>()));
        Assert.AreEqual(1, handler.Requests, "Navigation must retain the loaded catalog.");
        Assert.AreEqual(1, handler.ScopedReads);
    }

    private sealed class NavigationContext : IConsoleNavigationContext
    {
        public string ScopeRef => "/instance";
        public string AddToUri(string uri) => Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(uri, "context", ScopeRef);
    }
    private static HttpResponseMessage Response(int count, bool sharedIdentity = false) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(Enumerable.Range(0, count).Select(index => new InstalledPackResource
        {
            Uid = Guid.NewGuid(),
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = PackKinds.InstalledPack,
            Metadata = new() { Name = new PackIdentity("contoso", sharedIdentity ? "shared" : $"pack-{index}").ResourceName },
            ScopeRef = index % 2 == 0 ? ResourceScopeRef.Instance : ResourceScopeRef.Workspace(WorkspaceId),
            Definition = new() { Publisher = "contoso", PackName = sharedIdentity ? "shared" : $"pack-{index}", Version = "1.0.0", DisplayName = $"Pack {index}", Source = "fixture.zip", InstalledAt = DateTimeOffset.UnixEpoch, State = index % 3 == 0 ? InstalledPackState.Failed : InstalledPackState.Installed, Purpose = index % 2 == 0 ? PackPurpose.Sample : PackPurpose.Standard, Audience = index % 2 == 0 ? PackAudience.Personal : PackAudience.Professional }
        }).ToArray())
    };
    private static BunitContext Context(Handler handler, ResourceListContextProvider? provider = null)
    {
        var context = new BunitContext().WithCollectionPresentation().WithResourceListContext(provider);
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IPacksClient>(new PacksApiClient(new HttpClient(handler, false) { BaseAddress = new("https://agentstration.test/") }));
        return context;
    }
    private sealed class Handler(Func<int, Task<HttpResponseMessage>> load) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public int ScopedReads { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            if (request.RequestUri!.PathAndQuery == "/api/pack-projects") return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Array.Empty<PackProjectResource>()) };
            if (request.RequestUri.AbsolutePath.StartsWith("/api/packs/", StringComparison.Ordinal))
            {
                Assert.AreEqual("/api/packs/contoso/pack-0?scopeRef=%2Finstance", request.RequestUri.PathAndQuery);
                using var fixture = Response(1);
                var items = await fixture.Content!.ReadFromJsonAsync<InstalledPackResource[]>(cancellationToken);
                ScopedReads++;
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(items![0]) };
                response.Headers.ETag = new("\"fixture\"");
                return response;
            }
            Assert.AreEqual("/api/packs", request.RequestUri.PathAndQuery);
            return await load(++Requests);
        }
    }
}
