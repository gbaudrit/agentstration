using Agentstration.Identity.Contracts;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

public sealed partial class FlowDetailsDesignerTests
{
    [TestMethod]
    public async Task RemovedFlowPageReturnsToFirstPageWithOneBoundedRequestAndRetainsCards()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = new BunitContext().WithCollectionPresentation().WithResourceListContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var requests = new List<(int, int)>();
        context.Services.AddSingleton<IFlowApiClient>(new FlowClientStub
        {
            PageLoader = (skip, take) =>
            {
                requests.Add((skip, take));
                return Task.FromResult(new FlowSummaryPage(requests.Count == 3 ? [] : [Summary($"flow-{skip}")], requests.Count == 1));
            }
        });
        var cut = context.Render<Agentstration.Web.Components.Pages.Flows>();
        Assert.IsEmpty(cut.FindAll(".section-tabs"), "The resource catalog has no separate Definitions/Runs/Deployments tabs.");
        Assert.IsEmpty(cut.FindAll(".search-box, [data-filter-key]"), "The API has no server filter contract; the pilot must not filter one loaded page.");
        await cut.FindAll(".collection-view-switch button")[1].ClickAsync(new());
        await cut.FindAll(".collection-pager button")[1].ClickAsync(new());
        await cut.Find("[data-testid=flow-list-refresh]").ClickAsync(new());
        Assert.HasCount(1, cut.FindAll(".collection-card"));
        Assert.AreEqual("default/flow-0", cut.Find(".collection-qualified-name code").TextContent);
        CollectionAssert.AreEqual(new[] { (0, 20), (20, 20), (20, 20), (0, 20) }, requests);
        Assert.IsEmpty(cut.FindAll(".collection-page-number, .collection-pager [role=status]"));
        StringAssert.Contains(cut.Find(".collection-result-count").TextContent, "1 loaded resources");
    }

    [TestMethod]
    public async Task LateFlowPageCannotReplaceTheNewContextAndPreviousRowsDisappearDuringItsLoad()
    {
        using var culture = new TestCultureScope("en-US");
        var provider = new ResourceListContextProvider();
        using var context = new BunitContext().WithCollectionPresentation().WithResourceListContext(provider);
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var oldPage = new TaskCompletionSource<FlowSummaryPage>();
        var newPage = new TaskCompletionSource<FlowSummaryPage>();
        var requests = new List<(int, int)>();
        context.Services.AddSingleton<IFlowApiClient>(new FlowClientStub
        {
            PageLoader = (skip, take) =>
            {
                requests.Add((skip, take));
                return requests.Count switch { 1 => Task.FromResult(new FlowSummaryPage([Summary("old-context")], true)), 2 => oldPage.Task, _ => newPage.Task };
            }
        });
        var cut = context.Render<Agentstration.Web.Components.Pages.Flows>();
        var next = cut.FindAll(".collection-pager button")[1].ClickAsync(new());
        provider.Scope = $"/workspaces/{Guid.NewGuid():D}";
        await cut.InvokeAsync(() => context.Services.GetRequiredService<ConsoleContextState>().LoadAsync(CancellationToken.None));
        cut.WaitForAssertion(() => Assert.IsEmpty(cut.FindAll("tbody tr, .collection-card")));
        newPage.SetResult(new([Summary("new-context")], false));
        cut.WaitForAssertion(() => Assert.AreEqual("default/new-context", cut.Find(".collection-qualified-name code").TextContent));
        oldPage.SetResult(new([Summary("late-context")], false));
        await next;
        Assert.AreEqual("default/new-context", cut.Find(".collection-qualified-name code").TextContent);
        CollectionAssert.AreEqual(new[] { (0, 20), (20, 20), (0, 20) }, requests);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void ReadPermissionControlsQueriesAndWritePermissionControlsCreation(bool canRead)
    {
        using var culture = new TestCultureScope("en-US");
        var provider = new ResourceListContextProvider { Permissions = canRead ? new HashSet<string> { AuthorizationPermissions.ResourcesRead } : new HashSet<string>() };
        using var context = new BunitContext().WithCollectionPresentation().WithResourceListContext(provider);
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var reads = 0;
        context.Services.AddSingleton<IFlowApiClient>(new FlowClientStub { PageLoader = (_, _) => { reads++; return Task.FromResult(new FlowSummaryPage([Summary("readable")], false)); } });
        var cut = context.Render<Agentstration.Web.Components.Pages.Flows>();
        Assert.IsEmpty(cut.FindAll("a[href='/flows/new']"));
        Assert.AreEqual(canRead ? 1 : 0, reads);
        Assert.HasCount(canRead ? 1 : 0, cut.FindAll("tbody tr"));
        if (!canRead) Assert.AreEqual("Access denied", cut.Find("[role=alert] h2").TextContent);
    }
    private static FlowSummary Summary(string id) => new(id, id, "Direct", "1.0.0", "Active", 0, 0, FlowClientStub.Now);
}
