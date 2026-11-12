using Agentstration.Resources;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

public sealed partial class FlowDetailsDesignerTests
{
    [TestMethod]
    public async Task CompactFlowsKeepExactIdentityAndAlignedVersionSlotsAcrossPagesAndViews()
    {
        using var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var owner = ResourceScopeRef.Tenant(Guid.NewGuid());
        var rows = Enumerable.Range(1, 40).Select(index => new FlowSummary($"flow-{index}", "Same display name", "Workflow", "1.0.0", "Active", 0, 0, FlowClientStub.Now)
        { ScopeRef = owner, Namespace = new("team"), ActiveVersion = "1.0.0", DraftRevision = index % 2 == 0 ? 17 : null, IsManaged = true }).ToArray();
        var requests = new List<(int Skip, int Take)>();
        context.Services.AddSingleton<IFlowApiClient>(new FlowClientStub { PageLoader = (skip, take) =>
        {
            requests.Add((skip, take));
            return Task.FromResult(new FlowSummaryPage(rows.Skip(skip).Take(take).ToArray(), rows.Length > skip + take));
        }});
        var cut = context.Render<Agentstration.Web.Components.Pages.Flows>();
        cut.WaitForAssertion(() => Assert.HasCount(20, cut.FindAll("tbody tr")));
        Assert.HasCount(4, cut.FindAll("th"));
        Assert.AreEqual("Resource", cut.FindAll("th")[0].TextContent);
        Assert.AreEqual("Version", cut.FindAll("th")[1].QuerySelector(".flow-version-heading")!.ChildNodes[0].TextContent.Trim());
        Assert.IsEmpty(cut.FindAll(".collection-qualified-name > svg"));
        StringAssert.Contains(cut.Find("a.resource-name-link").GetAttribute("href")!, "scopeRef=");
        Assert.AreEqual("team/flow-1", cut.Find(".collection-qualified-name code").TextContent);
        Assert.HasCount(2, cut.FindAll("tbody tr")[1].QuerySelectorAll(".collection-version-compact"));
        Assert.AreEqual("r17", cut.Find(".collection-version-draft code").TextContent);
        Assert.AreEqual("Draft, revision 17", cut.Find(".collection-version-draft .disclosure-trigger").GetAttribute("aria-label"));
        Assert.HasCount(1, cut.FindAll("tbody tr")[0].QuerySelectorAll(".collection-version-compact"));
        Assert.IsFalse(cut.FindAll(".collection-pager button")[1].HasAttribute("disabled"));
        Assert.IsTrue(cut.FindAll(".collection-pager button")[0].HasAttribute("disabled"));
        await cut.FindAll(".collection-view-switch button")[1].ClickAsync(new());
        await cut.FindAll(".collection-pager button")[1].ClickAsync(new());
        cut.WaitForAssertion(() => Assert.AreEqual("team/flow-21", cut.Find(".collection-qualified-name code").TextContent));
        Assert.HasCount(20, cut.FindAll(".collection-card"));
        Assert.IsTrue(cut.FindAll(".collection-pager button")[1].HasAttribute("disabled"));
        await cut.Find("#flow-page-size").ChangeAsync(new() { Value = "50" });
        cut.WaitForAssertion(() => Assert.HasCount(40, cut.FindAll(".collection-card")));
        Assert.IsTrue(cut.FindAll(".collection-pager button")[0].HasAttribute("disabled"));
        Assert.IsEmpty(cut.FindAll(".collection-pager [role=status]"), "The API does not expose a total.");
        CollectionAssert.AreEqual(new[] { (0, 20), (20, 20), (0, 50) }, requests);
    }

    [TestMethod]
    public async Task FailedFlowPageRetainsPreviousRowsAndRetriesTheFailedRequest()
    {
        using var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var requests = new List<(int, int)>();
        context.Services.AddSingleton<IFlowApiClient>(new FlowClientStub { PageLoader = (skip, take) =>
        {
            requests.Add((skip, take));
            if (requests.Count == 2) throw new HttpRequestException("Offline");
            return Task.FromResult(new FlowSummaryPage([new($"flow-{skip}", "Flow", "Direct", "1.0.0", "Unknown", 0, 0, FlowClientStub.Now)], true));
        }});
        var cut = context.Render<Agentstration.Web.Components.Pages.Flows>();
        await cut.FindAll(".collection-pager button")[1].ClickAsync(new());
        Assert.AreEqual("default/flow-0", cut.Find(".collection-qualified-name code").TextContent);
        Assert.IsNotNull(cut.Find("[role=alert]"));
        await cut.Find("[role=alert] button").ClickAsync(new());
        cut.WaitForAssertion(() => Assert.AreEqual("default/flow-20", cut.Find(".collection-qualified-name code").TextContent));
        CollectionAssert.AreEqual(new[] { (0, 20), (20, 20), (20, 20) }, requests);
        Assert.IsEmpty(cut.FindAll(".collection-version-draft"));
        Assert.AreEqual("Definition version", cut.Find(".collection-version .sr-only").TextContent);
    }
}
