using Agentstration.Identity.Contracts;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class AgentCollectionPermissionsTests
{
    [TestMethod]
    [DataRow(false, true)]
    [DataRow(true, true)]
    [DataRow(true, false)]
    public async Task CollectionKeepsReadAccessAndOnlyOffersAuthorizedCommands(bool canManage, bool showDelete)
    {
        var workspaceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var scope = $"/workspaces/{workspaceId:D}";
        using var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddAgentstrationWebComponents();
        context.Services.AddSingleton<IManagementApiClient>(new MockApiClient(TimeProvider.System));
        context.ComponentFactories.AddStub<AgentResourcePreview>();
        var permissions = canManage
            ? new HashSet<string> { AuthorizationPermissions.ResourcesRead, AuthorizationPermissions.ResourcesWrite, AuthorizationPermissions.ResourcesDelete, AuthorizationPermissions.RunsExecute }
            : new HashSet<string> { AuthorizationPermissions.ResourcesRead };
        context.Services.AddSingleton<IConsoleContextProvider>(new ContextProvider(new(
            Guid.NewGuid(), "Reader", Guid.NewGuid(), "tenant", "Tenant", workspaceId,
            "default", "Default workspace", permissions, [])));
        context.Services.GetRequiredService<NavigationManager>().NavigateTo($"/agents?context={Uri.EscapeDataString(scope)}");
        await context.Services.GetRequiredService<ConsoleContextState>().EnsureLoadedAsync(CancellationToken.None);
        var cut = context.Render<Agentstration.Web.Components.Pages.Agents>(p => p.Add(c => c.ShowDeleteAction, showDelete));
        var strings = context.Services.GetRequiredService<IStringLocalizer<AgentsStrings>>();
        var menu = cut.Find(".collection-actions-menu");
        Assert.AreEqual(canManage, menu.TextContent.Contains(strings["Run"].Value, StringComparison.Ordinal));
        Assert.AreEqual(canManage, menu.TextContent.Contains(strings["EditDefinition"].Value, StringComparison.Ordinal));
        Assert.AreEqual(canManage && showDelete, menu.TextContent.Contains(strings["Delete"].Value, StringComparison.Ordinal));
        StringAssert.Contains(menu.TextContent, strings["Open"].Value);
        StringAssert.Contains(menu.TextContent, strings["Preview"].Value);
        var commands = menu.QuerySelectorAll("a, button").Select(command => command.TextContent).ToArray();
        var expected = new List<string> { strings["Open"].Value };
        if (canManage) { expected.Add(strings["EditDefinition"].Value); expected.Add(strings["Run"].Value); }
        expected.Add(strings["Preview"].Value);
        if (canManage && showDelete) expected.Add(strings["Delete"].Value);
        CollectionAssert.AreEqual(expected.ToArray(), commands);
        Assert.HasCount(canManage && showDelete ? 1 : 0, menu.QuerySelectorAll("hr"));
        var destination = cut.Find(".collection-identity a").GetAttribute("href")!;
        StringAssert.Contains(destination, $"scopeRef={Uri.EscapeDataString(scope)}");
        StringAssert.Contains(destination, $"context={Uri.EscapeDataString(scope)}");
        Assert.EndsWith("#overview", destination);
        Assert.AreEqual(destination, menu.QuerySelector("a")!.GetAttribute("href"));
        if (canManage)
        {
            var run = menu.QuerySelectorAll("a").Single(link => link.TextContent == strings["Run"].Value).GetAttribute("href")!;
            StringAssert.Contains(run, "/run?scopeRef=");
            Assert.EndsWith("#definition/configuration", menu.QuerySelectorAll("a").Single(link => link.TextContent == strings["EditDefinition"].Value).GetAttribute("href")!);
        }
        Assert.IsEmpty(cut.FindAll("input[type=search], [data-filter-key], .collection-page-number"));
        Assert.HasCount(2, cut.FindAll("tbody tr"));
        await cut.FindAll(".collection-actions-menu button").First(button => button.TextContent == strings["Preview"].Value).ClickAsync(new());
        Assert.HasCount(1, cut.FindComponents<Bunit.TestDoubles.Stub<AgentResourcePreview>>());
        Assert.IsEmpty(cut.FindAll(".quick-inspector"));
        await cut.Find("dialog .icon-button").ClickAsync(new());
        Assert.IsEmpty(cut.FindAll("dialog"));
    }

    private sealed class ContextProvider(ConsoleContextSnapshot snapshot) : IConsoleContextProvider
    {
        public Task<ConsoleContextSnapshot> GetAsync(CancellationToken cancellationToken) => Task.FromResult(snapshot);
    }
}
