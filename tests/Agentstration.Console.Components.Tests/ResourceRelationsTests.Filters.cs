using Agentstration.Resources;
using Agentstration.Web.Components;
using Agentstration.Web.SystemExplorer;
using Bunit;

namespace Agentstration.Web.Tests;

public sealed partial class ResourceRelationsTests
{
    [TestMethod]
    [DataRow("en-US", "Types", "Tool")]
    [DataRow("fr-FR", "Types", "Outil")]
    public async Task CompactToolbarFiltersWithoutRefetchingAndPreservesFiltersAcrossFullscreen(string culture, string filterLabel, string toolLabel)
    {
        using var locale = new TestCultureScope(culture);
        var root = Node("root");
        var tool = Node("tool"); tool = tool with { Identity = tool.Identity with { Kind = "Tool" } };
        var provider = Node("provider"); provider = provider with { Identity = provider.Identity with { Kind = "ModelProvider" } };
        var extension = Node("extension"); extension = extension with { Identity = extension.Identity with { Kind = "ExtensionRegistration" } };
        var client = new Client([root, tool, provider, extension], [new(root.Id, tool.Id, "declared-reference"),
            new(tool.Id, provider.Id, "declared-reference"), new(provider.Id, extension.Id, "declared-reference")]);
        using var context = Context(client); var page = Render(context, root, ResourceScopeRef.Instance);
        page.WaitForAssertion(() => Assert.AreEqual("false", page.Find("section.resource-relations").GetAttribute("aria-busy")));
        Assert.IsEmpty(page.FindAll(".relations-header, .relations-focus-name"));
        Assert.HasCount(1, page.FindAll(".relations-toolbar .relations-maximize"));
        Assert.HasCount(1, page.FindAll(".relations-toolbar .relations-count"));
        await page.Find("select").ChangeAsync("3");
        await page.InvokeAsync(() => page.FindComponent<SystemGraphDiagram>().Instance.Selected.InvokeAsync(tool.Id));
        Assert.HasCount(1, page.FindAll(".relations-inspector"));
        Assert.Contains(filterLabel, page.Find(".relations-filter-toggle").TextContent);
        await page.Find(".relations-filter-toggle").ClickAsync();
        await page.FindAll(".relations-kind-option").Single(label => label.TextContent.Trim() == toolLabel).QuerySelector("input")!.ChangeAsync(false);
        Assert.HasCount(3, page.FindComponent<SystemGraphDiagram>().Instance.Page.Nodes);
        Assert.IsTrue(page.FindComponent<SystemGraphDiagram>().Instance.Page.Nodes.Any(node => node.Id == extension.Id));
        Assert.IsEmpty(page.FindAll(".relations-inspector"));
        Assert.Contains("3 / 4", page.Find(".relations-count").TextContent);
        Assert.HasCount(1, client.Calls);
        await page.Find(".relations-maximize").ClickAsync();
        Assert.IsTrue(page.FindComponent<ModalFrame>().Instance.IsOpen);
        Assert.IsFalse(page.FindAll(".relations-kind-option").Single(label => label.TextContent.Trim() == toolLabel).QuerySelector("input")!.HasAttribute("checked"));
        Assert.HasCount(3, page.FindComponent<SystemGraphDiagram>().Instance.Page.Nodes);
        Assert.HasCount(1, client.Calls);
        await page.Find(".relations-kind-filters > button").ClickAsync();
        Assert.HasCount(4, page.FindComponent<SystemGraphDiagram>().Instance.Page.Nodes);
        await page.FindAll(".relations-kind-option").Single(label => label.TextContent.Trim() == (culture == "fr-FR" ? "Source de données" : "Data source")).QuerySelector("input")!.ChangeAsync(false);
        Assert.IsTrue(page.FindComponent<SystemGraphDiagram>().Instance.Page.Nodes.Any(node => node.Id == root.Id));
        page.Render(p => p.Add(c => c.Context, ResourceScopeRef.Workspace(Guid.NewGuid())));
        page.WaitForAssertion(() => Assert.HasCount(2, client.Calls));
        Assert.IsEmpty(page.FindAll(".relations-kind-filters, .relations-filter-count"));
        Assert.IsFalse(page.FindComponent<ModalFrame>().Instance.IsOpen);
    }
}
