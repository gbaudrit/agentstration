using Agentstration.Resources;
using Agentstration.SystemGraph.Contracts;
using Agentstration.Web.SystemExplorer;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

public sealed partial class SystemExplorerTests
{
    [TestMethod]
    [DataRow("en-US", "Link details", "Show technical details")]
    [DataRow("fr-FR", "Détails de la liaison", "Afficher les détails techniques")]
    public async Task ToolSetsMoveToSelectableLinksWhileTheTechnicalViewAndExactListRemainAvailable(string culture, string details, string technical)
    {
        using var locale = new TestCultureScope(culture);
        var client = new StreamClient(); var agent = StreamNode(client.Owner, "Agent");
        var tool = StreamNode(client.Owner, "Read") with { Identity = StreamNode(client.Owner, "Read").Identity with { Kind = "Tool" } };
        var set = StreamNode(client.Owner, "Nantes access") with { State = "publication", Evidence = new(Publication: "2.0.0") };
        set = set with { Identity = set.Identity with { Kind = "ToolSet" } };
        var definition = StreamNode(client.Owner, "Relay") with { State = "publication", Evidence = new(Publication: "1.0.0") };
        definition = definition with { Identity = definition.Identity with { Kind = "ToolDefinition" } };
        var pin = tool with { Id = SystemGraphIds.Generation(tool.Identity.ResourceUid, 1), State = "recorded-generation", Evidence = new(1) };
        var pageData = new SystemGraphPage([agent, tool, pin, set, definition], [new(agent.Id, pin.Id, "toolset-access") { ViaNodeId = set.Id },
            new(pin.Id, tool.Id, "generation-of"), new(tool.Id, definition.Id, "exact-dependency", "materialization.publication")],
            "s", DateTimeOffset.UnixEpoch, "partial", false, false, null);
        client.Photo = pageData;
        using var context = StreamContext(client);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/system-explorer?root=" + Uri.EscapeDataString(agent.Id));
        var page = context.Render<Agentstration.Web.Components.Pages.SystemExplorer>();
        page.WaitForAssertion(() => Assert.HasCount(5, page.FindAll("[data-testid=explorer-node]")));
        page.WaitForAssertion(() => Assert.HasCount(2, page.FindComponent<SystemGraphDiagram>().Instance.Page.Nodes));
        var diagram = page.FindComponent<SystemGraphDiagram>(); var instance = diagram.Instance; var id = instance.Page.Relations.Single().Kind;
        Assert.Contains("Nantes access", diagram.Instance.RelationshipLabels[id]);
        Assert.Contains("2.0.0", diagram.Instance.RelationshipLabels[id]);
        await page.InvokeAsync(() => instance.SelectLink(id));
        page.WaitForAssertion(() => Assert.Contains("Relay", page.Find("section[aria-label='" + details + "']").TextContent));
        Assert.Contains(set.Id, page.Find("section[aria-label='" + details + "']").TextContent);
        await page.FindAll("label").Single(label => label.TextContent.Contains(technical, StringComparison.Ordinal)).QuerySelector("input")!.ChangeAsync(true);
        page.WaitForAssertion(() => Assert.HasCount(5, page.FindComponent<SystemGraphDiagram>().Instance.Page.Nodes));
        Assert.IsEmpty(page.FindAll("section[aria-label='" + details + "']"));
        await page.InvokeAsync(() => instance.SelectLink(id));
        Assert.IsEmpty(page.FindAll("section[aria-label='" + details + "']"), "A callback from the disposed summary cannot restore old link details.");
    }
}
