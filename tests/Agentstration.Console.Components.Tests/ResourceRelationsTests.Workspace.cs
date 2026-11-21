using Agentstration.Resources;
using Agentstration.Web.Components;
using Agentstration.Web.SystemExplorer;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

public sealed partial class ResourceRelationsTests
{
    [TestMethod]
    [DataRow("en-US", "Full screen", "Exit full screen", "List", "Graph")]
    [DataRow("fr-FR", "Plein écran", "Quitter le plein écran", "Liste", "Graphe")]
    public async Task MaximizingAndRestoringPreservesTheObservationDirectionAndDepth(string culture, string enter, string exit, string list, string graph)
    {
        using var locale = new TestCultureScope(culture);
        var root = Node("root"); var neighbor = Node("neighbor");
        var client = new Client([root, neighbor], [new(root.Id, neighbor.Id, "exact-dependency")]);
        using var context = Context(client); var page = Render(context, root, ResourceScopeRef.Instance);
        page.WaitForAssertion(() => Assert.AreEqual("false", page.Find("section.resource-relations").GetAttribute("aria-busy")));
        Assert.DoesNotContain("C4", page.Find("h2").TextContent);
        Assert.IsEmpty(page.FindAll("aside"), "The full canvas is available before details are requested.");
        Assert.IsTrue(page.FindComponent<SystemGraphDiagram>().Instance.FillContainer);
        await page.Find("select").ChangeAsync("3");
        await page.Find(".relations-view-switch button:nth-child(2)").ClickAsync();
        Assert.Contains(list, page.Find(".relations-view-switch").TextContent);
        Assert.Contains(graph, page.Find(".relations-view-switch").TextContent);
        await page.FindAll(".relations-list button").Single(button => button.GetAttribute("aria-label") == "neighbor").ClickAsync();
        Assert.Contains("neighbor", page.Find("aside h3").TextContent);
        Assert.AreEqual(enter, page.Find(".relations-maximize").GetAttribute("aria-label"));
        await page.Find(".relations-maximize").ClickAsync();
        Assert.IsTrue(page.FindComponent<ModalFrame>().Instance.IsOpen);
        Assert.AreEqual(exit, page.Find(".relations-maximize").GetAttribute("aria-label"));
        Assert.AreEqual("true", page.Find("dialog").GetAttribute("aria-modal"));
        Assert.HasCount(1, client.Calls, "Maximizing reuses the authorized observation.");
        Assert.Contains("neighbor", page.Find("aside h3").TextContent);
        await page.InvokeAsync(() => page.FindComponent<ModalFrame>().Instance.OnClose.InvokeAsync());
        Assert.IsFalse(page.FindComponent<ModalFrame>().Instance.IsOpen);
        Assert.AreEqual("3", page.Find("select").GetAttribute("value"));
        Assert.AreEqual("true", page.Find(".relations-view-switch button:nth-child(2)").GetAttribute("aria-pressed"));
        Assert.Contains("neighbor", page.Find("aside h3").TextContent);
        Assert.HasCount(1, client.Calls);
    }

    [TestMethod]
    public async Task DiagramSelectionOpensDetailsAndClosingThemRestoresTheCanvas()
    {
        var root = Node("root"); var neighbor = Node("neighbor");
        var client = new Client([root, neighbor], [new(root.Id, neighbor.Id, "exact-dependency")]);
        using var context = Context(client); var page = Render(context, root, ResourceScopeRef.Instance);
        page.WaitForAssertion(() => Assert.AreEqual("false", page.Find("section.resource-relations").GetAttribute("aria-busy")));
        Assert.IsEmpty(page.FindAll("aside"));
        await page.InvokeAsync(() => page.FindComponent<SystemGraphDiagram>().Instance.Selected.InvokeAsync(neighbor.Id));
        Assert.Contains("neighbor", page.Find("aside h3").TextContent);
        Assert.AreEqual("true", page.Find(".relations-view-actions > button").GetAttribute("aria-expanded"));
        await page.Find("aside header button").ClickAsync();
        Assert.IsEmpty(page.FindAll("aside"));
        Assert.IsNotNull(page.FindComponent<SystemGraphDiagram>());
        Assert.HasCount(1, client.Calls);
    }

    [TestMethod]
    public async Task NavigationClosesTheFullscreenSurfaceWithoutChangingTheGraphCoordinates()
    {
        var root = Node("root"); var client = new Client([root], []);
        using var context = Context(client); var page = Render(context, root, ResourceScopeRef.Instance);
        page.WaitForAssertion(() => Assert.AreEqual("false", page.Find("section.resource-relations").GetAttribute("aria-busy")));
        await page.Find(".relations-maximize").ClickAsync();
        Assert.IsTrue(page.FindComponent<ModalFrame>().Instance.IsOpen);
        await page.InvokeAsync(() => context.Services.GetRequiredService<NavigationManager>().NavigateTo("/agents/example#overview"));
        Assert.IsFalse(page.FindComponent<ModalFrame>().Instance.IsOpen);
        Assert.AreEqual(root.Id, client.Calls.Single().Query.Root);
        Assert.AreEqual(ResourceScopeRef.Instance, client.Calls.Single().Context);
    }
}
