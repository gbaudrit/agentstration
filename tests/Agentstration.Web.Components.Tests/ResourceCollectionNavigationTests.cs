using Agentstration.Web.Components;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Components.Tests;

[TestClass]
public sealed class ResourceCollectionNavigationTests
{
    [TestMethod]
    [DataRow("/flows", "/flows?context=%2Ftenants%2Fteam")]
    [DataRow("/flows/runs?scopeRef=%2Fworkspaces%2Fexecution#recent", "/flows/runs?scopeRef=%2Fworkspaces%2Fexecution&context=%2Ftenants%2Fteam#recent")]
    [DataRow("/flows?context=%2Finstance", "/flows?context=%2Finstance")]
    public void DestinationKeepsExplicitCoordinatesAndAddsOnlyLogicalContext(string href, string expected)
    {
        using var context = Context();
        context.Services.AddAgentstrationWebComponents();
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/flows/example?namespace=private&scopeRef=%2Finstance&context=%2Ftenants%2Fteam#definition");
        var cut = context.Render<ResourceCollectionLink>(p => p.Add(c => c.Href, href).Add(c => c.Label, "Flow list"));
        Assert.AreEqual(expected, cut.Find("a").GetAttribute("href"));
        Assert.AreEqual("Flow list", cut.Find("a").GetAttribute("aria-label"));
        Assert.AreEqual("Flow list", cut.Find("a").GetAttribute("title"));
        Assert.AreEqual(string.Empty, cut.Find("a").TextContent);
    }

    [TestMethod]
    public void StandaloneConsumersNeedNoConsoleServiceAndIncompleteDestinationsAreOmitted()
    {
        using var context = Context();
        var cut = context.Render<ResourceCollectionLink>(p => p.Add(c => c.Href, "/agents"));
        Assert.IsEmpty(cut.FindAll("a"));
        cut.Render(p => p.Add(c => c.Label, "Agents"));
        Assert.AreEqual("/agents", cut.Find("a").GetAttribute("href"));
        cut.Render(p => p.Add(c => c.Href, (string?)null));
        Assert.IsEmpty(cut.FindAll("a"));
    }

    [TestMethod]
    public void HeaderPlacesCollectionAfterOverflowAndDoesNotCreateAnEmptyActionBar()
    {
        using var context = Context();
        var cut = context.Render<ResourceHeader>(p => p.Add(c => c.Name, "Resource").Add(c => c.ResourceType, "Agent")
            .Add(c => c.CollectionHref, "/agents").Add(c => c.CollectionLabel, "Agents"));
        Assert.IsEmpty(cut.FindAll(".resource-action-bar"));
        cut.Render(p => p.Add(c => c.PrimaryActions, "<button>Run</button>").Add(c => c.MoreActions, "<button>Delete</button>"));
        Assert.IsNotNull(cut.Find(".page-actions > .resource-action-bar + .resource-collection-link:last-child"));
        Assert.IsNotNull(cut.Find(".resource-more-actions"));
    }

    [TestMethod]
    public void CreationHeaderUsesTheSameFinalCollectionLink()
    {
        using var context = Context();
        var cut = context.Render<PageHeader>(p => p.Add(c => c.Title, "Create")
            .Add(c => c.Actions, "<button>Import</button>").Add(c => c.CollectionHref, "/agents").Add(c => c.CollectionLabel, "Agents"));
        Assert.IsNotNull(cut.Find(".page-actions > button + .resource-collection-link:last-child"));
    }

    [TestMethod]
    [DataRow(ResourceLoadStatus.Loading)]
    [DataRow(ResourceLoadStatus.Failed)]
    [DataRow(ResourceLoadStatus.NotFound)]
    [DataRow(ResourceLoadStatus.Forbidden)]
    public void BlockingStatesRetainTheKnownListDestinationWithoutPrivateContentOrCommands(ResourceLoadStatus status)
    {
        using var context = Context();
        var cut = context.Render<ResourceDetailsTemplate>(p => p.Add(c => c.Name, "Resource").Add(c => c.ResourceType, "Agent")
            .Add(c => c.CollectionHref, "/agents").Add(c => c.CollectionLabel, "Agents")
            .Add(c => c.ResourceIdentity, "default/resource|/instance")
            .Add(c => c.LoadState, new ResourceLoadState(status))
            .Add(c => c.Actions, "<button data-private>Delete</button>")
            .Add(c => c.HasDraft, true).Add(c => c.DraftActions, "<button data-private>Save</button>"));
        Assert.AreEqual("/agents", cut.Find(".resource-collection-link").GetAttribute("href"));
        Assert.IsEmpty(cut.FindAll("[data-private], .resource-details-navigation, .resource-identity-popover"));
    }

    private static BunitContext Context()
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        return context;
    }
}
