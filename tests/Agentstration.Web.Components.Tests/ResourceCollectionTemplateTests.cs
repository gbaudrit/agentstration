using Agentstration.Web.Components.Models;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Components.Tests;

[TestClass]
public sealed class ResourceCollectionTemplateTests
{
    private static CollectionColumn<string> Identity => new("name", "Name", Text);
    private static RenderFragment Text(string value) => builder => builder.AddContent(0, value);
    private static BunitContext Context()
    {
        var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        return context;
    }
    private static CollectionFilter TypeFilter => new("type", "Type", CollectionFilterKind.Choice)
    {
        Options = [new("http", "HTTP"), new("redis", "Redis")]
    };

    [TestMethod]
    public async Task ColumnAndAdditionalFiltersShareOneQueryAndResetPaginationWithoutMovingDataOwnership()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = Context();
        CollectionQuery? requested = null;
        var query = new CollectionQuery { Page = 3, PageSize = 50 };
        var cut = context.Render<ResourceCollectionTemplate<string>>(p => p
            .Add(c => c.Title, "Services").Add(c => c.IdentityColumn, Identity)
            .Add(c => c.Columns, [new("type", "Type", Text) { Filters = [TypeFilter] }, new("date", "Created", Text)])
            .Add(c => c.AdditionalFilters, [new("namespace", "Namespace")])
            .Add(c => c.Items, ["First"]).Add(c => c.Query, query)
            .Add(c => c.QueryChanged, value => requested = value));
        Assert.HasCount(1, cut.FindAll(".resource-toolbar"));
        Assert.HasCount(1, cut.FindAll(".collection-view-switch"));
        CollectionAssert.AreEqual(new[] { "Name", "Type", "Created" }, cut.FindAll("th").Select(element => element.TextContent).ToArray());
        Assert.IsEmpty(cut.FindAll("[data-filter-key=date]"));
        await cut.Find("[data-filter-key=type] select").ChangeAsync(new() { Value = "http" });
        Assert.AreEqual(1, requested!.Page);
        Assert.AreEqual(50, requested.PageSize);
        Assert.AreEqual("http", requested.Filter("type").Value);
        cut.Render(p => p.Add(c => c.Query, requested));
        await cut.Find("button[aria-controls]").ClickAsync(new());
        await cut.Find("[data-filter-key=namespace] input").InputAsync(new() { Value = "team" });
        Assert.AreEqual("team", requested!.Filter("namespace").Value);
        Assert.AreEqual("http", requested.Filter("type").Value);
        cut.Render(p => p.Add(c => c.Query, requested));
        Assert.HasCount(2, cut.FindAll(".collection-filter-chip"));
        await cut.FindAll(".collection-view-switch button")[1].ClickAsync(new());
        Assert.HasCount(1, cut.FindAll(".collection-card"));
        Assert.HasCount(2, cut.FindAll(".collection-filter-chip"));
        await cut.Find(".collection-filter-chip").ClickAsync(new());
        Assert.IsFalse(requested!.Filters.ContainsKey("type"));
        Assert.AreEqual("team", requested.Filter("namespace").Value);
    }

    [TestMethod]
    public async Task ExplicitApplyKeepsAppliedChipsUntilCriteriaAreSubmittedAndDatesRemainInvariant()
    {
        using var culture = new TestCultureScope("fr-FR");
        using var context = Context();
        CollectionQuery? requested = null;
        var cut = context.Render<ResourceCollectionTemplate<string>>(p => p
            .Add(c => c.Title, "Services").Add(c => c.IdentityColumn, Identity).Add(c => c.Items, ["First"])
            .Add(c => c.Columns, [new("type", "Type", Text) { Filters = [TypeFilter] }])
            .Add(c => c.AdditionalFilters, [new("created", "Création", CollectionFilterKind.DateRange), new("limit", "Limite", CollectionFilterKind.Number)])
            .Add(c => c.ApplyMode, CollectionFilterApplyMode.Explicit).Add(c => c.QueryChanged, value => requested = value));
        await cut.Find("[data-filter-key=type] select").ChangeAsync(new() { Value = "http" });
        Assert.IsNull(requested);
        Assert.IsEmpty(cut.FindAll(".collection-filter-chip"));
        await cut.Find("button[aria-controls]").ClickAsync(new());
        await cut.FindAll("input[type=date]")[0].InputAsync(new() { Value = "2026-11-01" });
        await cut.FindAll("input[type=date]")[1].InputAsync(new() { Value = "2026-11-19" });
        await cut.Find("input[type=number]").InputAsync(new() { Value = "12" });
        await cut.Find(".resource-toolbar .button-primary").ClickAsync(new());
        Assert.AreEqual("2026-11-01", requested!.Filter("created").Value);
        Assert.AreEqual("2026-11-19", requested.Filter("created").End);
        Assert.AreEqual("12", requested.Filter("limit").Value);
        Assert.AreEqual("http", requested.Filter("type").Value);
    }

    [TestMethod]
    [DataRow("en-US", "No matching results", "Clear filters")]
    [DataRow("fr-FR", "Aucun résultat correspondant", "Effacer les filtres")]
    public async Task EmptyAndNoMatchStatesAreDistinctAndDoNotRenderEmptyGridsOrPagers(string locale, string noMatches, string reset)
    {
        using var culture = new TestCultureScope(locale);
        using var context = Context();
        CollectionQuery? requested = null;
        var cut = context.Render<ResourceCollectionTemplate<string>>(p => p
            .Add(c => c.Title, "Services").Add(c => c.IdentityColumn, Identity)
            .Add(c => c.EmptyTitle, "Empty catalog").Add(c => c.EmptyActions, Text("Create"))
            .Add(c => c.Pagination, new(1, 20, false) { Total = 0 })
            .Add(c => c.QueryChanged, value => requested = value));
        Assert.AreEqual("Empty catalog", cut.Find(".empty-state h2").TextContent);
        StringAssert.Contains(cut.Find(".empty-state").TextContent, "Create");
        Assert.IsEmpty(cut.FindAll("table, .collection-cards, .collection-pager"));
        cut.Render(p => p.Add(c => c.Query, new() { Search = "missing", PageSize = 50 }));
        Assert.AreEqual(noMatches, cut.Find(".empty-state h2").TextContent);
        Assert.AreEqual(reset, cut.Find(".empty-state button").TextContent);
        Assert.DoesNotContain("Create", cut.Find(".empty-state").TextContent);
        await cut.Find(".empty-state button").ClickAsync(new());
        Assert.IsFalse(requested!.HasFilters);
        Assert.AreEqual(1, requested.Page);
        Assert.AreEqual(50, requested.PageSize);
    }

    [TestMethod]
    [DataRow("en-US", "Unable to update results", "Access denied")]
    [DataRow("fr-FR", "Impossible d’actualiser les résultats", "Accès refusé")]
    public async Task RefreshFailureRetainsViewAndRowsWhileForbiddenStateRemovesAllResults(string locale, string failure, string forbidden)
    {
        using var culture = new TestCultureScope(locale);
        using var context = Context();
        var cut = context.Render<ResourceCollectionTemplate<string>>(p => p
            .Add(c => c.Title, "Services").Add(c => c.IdentityColumn, Identity).Add(c => c.Items, ["Retained"])
            .Add(c => c.PrimaryActions, Text("Create")));
        await cut.FindAll(".collection-view-switch button")[1].ClickAsync(new());
        cut.Render(p => p.Add(c => c.LoadState, new(ResourceLoadStatus.Failed) { HasContent = true }));
        StringAssert.Contains(cut.Find("[role=alert]").TextContent, failure);
        Assert.AreEqual("Retained", cut.Find(".collection-card").TextContent.Trim());
        cut.Render(p => p.Add(c => c.LoadState, new(ResourceLoadStatus.Forbidden)));
        Assert.IsEmpty(cut.FindAll(".collection-card, table, .page-actions, .collection-pager"));
        Assert.AreEqual(forbidden, cut.Find("[role=alert] h2").TextContent);
        cut.Render(p => p.Add(c => c.LoadState, new(ResourceLoadStatus.Ready)));
        Assert.HasCount(1, cut.FindAll(".collection-card"), "Temporary states must not discard an explicitly selected view.");
    }

    [TestMethod]
    public async Task KnownTotalsAllowBoundedDirectNavigationAndContinuationAllowsOnlyAvailableNeighbours()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = Context();
        var requests = new List<int>();
        var cut = context.Render<CollectionPager>(p => p.Add(c => c.Page, 3).Add(c => c.PageSize, 20)
            .Add(c => c.Total, 2000).Add(c => c.HasMore, true).Add(c => c.SupportsDirectNavigation, true)
            .Add(c => c.PageChanged, page => requests.Add(page)));
        Assert.IsTrue(cut.FindAll(".collection-page-number").Count <= 7, "A large total must not render one button per page.");
        await cut.Find("button[aria-label='Page 100']").ClickAsync(new());
        CollectionAssert.AreEqual(new[] { 100 }, requests);
        cut.Render(p => p.Add(c => c.Total, (int?)null).Add(c => c.HasPrevious, false).Add(c => c.SupportsDirectNavigation, false));
        Assert.IsEmpty(cut.FindAll(".collection-page-number, [role=status]"));
        Assert.IsTrue(cut.FindAll("button")[0].HasAttribute("disabled"));
        await cut.FindAll("button")[1].ClickAsync(new());
        CollectionAssert.AreEqual(new[] { 100, 4 }, requests);
    }

    [TestMethod]
    public async Task EmptyContinuationWindowStillAllowsTheNextSupportedPageWithoutClaimingAnEmptyCatalog()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = Context();
        CollectionQuery? requested = null;
        var cut = context.Render<ResourceCollectionTemplate<string>>(p => p
            .Add(c => c.Title, "Services").Add(c => c.IdentityColumn, Identity).Add(c => c.Query, new() { Page = 3 })
            .Add(c => c.Pagination, new(3, 20, true) { HasPrevious = false, PageSizes = [] })
            .Add(c => c.EmptyActions, Text("Create")).Add(c => c.QueryChanged, value => requested = value));
        Assert.AreEqual("No records on this page", cut.Find(".empty-state h2").TextContent);
        Assert.DoesNotContain("Create", cut.Find(".empty-state").TextContent);
        Assert.IsEmpty(cut.FindAll("table, .collection-cards, .collection-page-number"));
        await cut.FindAll(".collection-pager button")[1].ClickAsync(new());
        Assert.AreEqual(4, requested!.Page);
    }
}
