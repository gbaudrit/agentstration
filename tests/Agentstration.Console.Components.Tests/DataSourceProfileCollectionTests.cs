using System.Net;
using System.Net.Http.Json;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.DataSources.Contracts;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class DataSourceProfileCollectionTests
{
    [TestMethod]
    [DataRow("en-US", "No matching results")]
    [DataRow("fr-FR", "Aucun résultat correspondant")]
    public async Task FiltersUseTheWholeCatalogAndCardsSurviveNoMatches(string cultureName, string noMatches)
    {
        using var culture = new TestCultureScope(cultureName);
        using var handler = new Handler(_ => Task.FromResult(Response(45)));
        using var context = Context(handler);
        var cut = context.Render<DataSourceProfiles>();
        cut.WaitForAssertion(() => Assert.HasCount(20, cut.FindAll("tbody tr")));
        Assert.IsEmpty(cut.FindAll(".collection-actions"));
        var originalUrl = cut.Find(".resource-name-link").GetAttribute("href");
        StringAssert.Contains(originalUrl!, "/data-source-profiles/profile-0?namespace=team&scopeRef=%2Finstance&context=%2Finstance");
        await cut.FindAll(".collection-page-number")[2].ClickAsync(new());
        Assert.HasCount(5, cut.FindAll("tbody tr"));
        await cut.FindAll(".collection-view-switch button")[1].ClickAsync(new());
        await cut.Find(".resource-toolbar button[aria-controls]").ClickAsync(new());
        await cut.Find("[data-filter-key=namespace] select").ChangeAsync(new() { Value = "team" });
        Assert.HasCount(15, cut.FindAll(".collection-card"));
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
        var cut = context.Render<DataSourceProfiles>();
        await cut.FindAll(".collection-page-number")[2].ClickAsync(new());
        await cut.Find("[data-testid=resource-data-source-profiles-refresh]").ClickAsync(new());
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
        var provider = new ResourceListContextProvider();
        using var handler = new Handler(call => call switch
        {
            1 => Task.FromResult(Response(1)),
            2 => old.Task,
            3 => replacement.Task,
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden))
        });
        using var context = Context(handler, provider);
        var cut = context.Render<DataSourceProfiles>();
        var refresh = cut.Find("[data-testid=resource-data-source-profiles-refresh]").ClickAsync(new());
        provider.Scope = $"/workspaces/{Guid.NewGuid():D}";
        await cut.InvokeAsync(() => context.Services.GetRequiredService<ConsoleContextState>().LoadAsync(CancellationToken.None));
        cut.WaitForAssertion(() => Assert.IsEmpty(cut.FindAll(".resource-name-link")));
        replacement.SetResult(Response(2));
        cut.WaitForAssertion(() => Assert.HasCount(2, cut.FindAll(".resource-name-link")));
        old.SetResult(Response(4));
        await refresh;
        Assert.HasCount(2, cut.FindAll(".resource-name-link"));
        await cut.Find("[data-testid=resource-data-source-profiles-refresh]").ClickAsync(new());
        Assert.IsEmpty(cut.FindAll(".resource-name-link, a[href*='/data-source-profiles/new']"));
    }

    [TestMethod]
    public void DeniedReadMakesNoRequestAndReaderHasNoCreateCommand()
    {
        var provider = new ResourceListContextProvider { Permissions = new HashSet<string>() };
        using var handler = new Handler(_ => Task.FromResult(Response(1)));
        using (var context = Context(handler, provider))
        {
            var cut = context.Render<DataSourceProfiles>();
            Assert.IsEmpty(cut.FindAll(".resource-name-link, a[href*='/data-source-profiles/new']"));
            Assert.AreEqual(0, handler.Requests);
        }
        provider.Permissions = new HashSet<string> { AuthorizationPermissions.ResourcesRead };
        using var reader = Context(handler, provider);
        var list = reader.Render<DataSourceProfiles>();
        Assert.HasCount(1, list.FindAll(".resource-name-link"));
        Assert.IsEmpty(list.FindAll("a[href*='/data-source-profiles/new']"));
    }

    [TestMethod]
    public void OptionalInventoryFailureKeepsPublishedFactsAndDoesNotInventADraftPublication()
    {
        using var profiles = new Handler(_ => Task.FromResult(Response(2)));
        using var inventory = new Handler(_ => throw new HttpRequestException("Inventory unavailable"));
        using var context = Context(profiles);
        context.Services.AddSingleton<IExtensionsClient>(new ExtensionsApiClient(new HttpClient(inventory, false) { BaseAddress = new("https://agentstration.test/") }));
        var cut = context.Render<DataSourceProfiles>();
        Assert.HasCount(2, cut.FindAll(".resource-name-link"));
        StringAssert.Contains(cut.Markup, "2.0.0");
        StringAssert.Contains(cut.Markup, "source-extension");
        Assert.IsFalse(cut.Markup.Contains("9.0.0", StringComparison.Ordinal), "The draft version is not a publication.");
        Assert.IsEmpty(cut.FindAll("[role=alert]"));
        Assert.AreEqual(1, profiles.Requests);
        Assert.AreEqual(1, inventory.Requests, "One shared optional inventory request, never one per row.");
    }

    private static HttpResponseMessage Response(int count) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(Enumerable.Range(0, count).Select(index => new DataSourceProfileResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = DataSourceResourceKinds.DataSourceProfile,
            Metadata = new() { Name = $"profile-{index}", Namespace = ResourceNamespace.Parse(index % 3 == 0 ? "team" : "other") },
            ScopeRef = ResourceScopeRef.Instance,
            Governance = ResourceGovernance.Extension("installation", "source-extension", "1.0.0"),
            PublishedVersion = index == 0 ? "2.0.0" : null,
            Definition = new() { DisplayName = $"Profile {index}", Version = "9.0.0", AcquisitionFlow = new() { Name = "acquire", Version = "1.0.0" } }
        }).ToArray())
    };
    private static BunitContext Context(Handler handler, ResourceListContextProvider? provider = null)
    {
        var context = new BunitContext().WithCollectionPresentation().WithResourceListContext(provider);
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IDataSourceProfilesClient>(new DataSourceProfilesApiClient(new HttpClient(handler, false) { BaseAddress = new("https://agentstration.test/") }));
        return context;
    }
    private sealed class Handler(Func<int, Task<HttpResponseMessage>> load) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => load(++Requests);
    }
}
