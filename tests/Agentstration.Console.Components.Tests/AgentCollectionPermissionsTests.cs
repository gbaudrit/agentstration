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
    [DataRow(false)]
    [DataRow(true)]
    public async Task CollectionKeepsReadAccessAndOnlyOffersAuthorizedCommands(bool canManage)
    {
        var workspaceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var scope = $"/workspaces/{workspaceId:D}";
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddAgentstrationWebComponents();
        context.Services.AddSingleton<IManagementApiClient>(new MockApiClient(TimeProvider.System));
        var permissions = canManage
            ? new HashSet<string> { AuthorizationPermissions.ResourcesWrite, AuthorizationPermissions.ResourcesDelete, AuthorizationPermissions.RunsExecute }
            : [];
        context.Services.AddSingleton<IConsoleContextProvider>(new ContextProvider(new(
            Guid.NewGuid(), "Reader", Guid.NewGuid(), "tenant", "Tenant", workspaceId,
            "default", "Default workspace", permissions, [])));
        context.Services.GetRequiredService<NavigationManager>().NavigateTo($"/agents?context={Uri.EscapeDataString(scope)}");
        await context.Services.GetRequiredService<ConsoleContextState>().EnsureLoadedAsync(CancellationToken.None);
        var cut = context.Render<Agentstration.Web.Components.Pages.Agents>();
        var strings = context.Services.GetRequiredService<IStringLocalizer<AgentsStrings>>();
        var menu = cut.Find(".collection-actions-menu");
        Assert.AreEqual(canManage, menu.TextContent.Contains(strings["Run"].Value, StringComparison.Ordinal));
        Assert.AreEqual(canManage, menu.TextContent.Contains(strings["EditDefinition"].Value, StringComparison.Ordinal));
        Assert.AreEqual(canManage, menu.TextContent.Contains(strings["Delete"].Value, StringComparison.Ordinal));
        StringAssert.Contains(menu.TextContent, strings["Open"].Value);
        StringAssert.Contains(menu.TextContent, strings["QuickInspect"].Value);
        var destination = cut.Find(".collection-identity a").GetAttribute("href")!;
        StringAssert.Contains(destination, $"scopeRef={Uri.EscapeDataString(scope)}");
        StringAssert.Contains(destination, $"context={Uri.EscapeDataString(scope)}");
        if (canManage)
        {
            var run = menu.QuerySelectorAll("a").Single(link => link.TextContent == strings["Run"].Value).GetAttribute("href")!;
            StringAssert.Contains(run, "/run?scopeRef=");
        }
        Assert.AreEqual(string.Empty, cut.Find("input[type='search']").GetAttribute("value"));
        cut.Find("input[type='search']").Input("dotnet");
        Assert.HasCount(1, cut.FindAll("tbody tr"));
        cut.Find("input[type='search']").Input("no-such-agent");
        Assert.IsEmpty(cut.FindAll(".resource-collection"));
        cut.Find("input[type='search']").Input(string.Empty);
        Assert.HasCount(2, cut.FindAll("tbody tr"));
    }

    private sealed class ContextProvider(ConsoleContextSnapshot snapshot) : IConsoleContextProvider
    {
        public Task<ConsoleContextSnapshot> GetAsync(CancellationToken cancellationToken) => Task.FromResult(snapshot);
    }
}
