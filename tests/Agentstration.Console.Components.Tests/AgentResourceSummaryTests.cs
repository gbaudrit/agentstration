using Agentstration.Agents;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components;
using Agentstration.Web.Components.State;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class AgentResourceSummaryTests
{
    [TestMethod]
    [DataRow("en-US", "Quick actions")]
    [DataRow("fr-FR", "Actions rapides")]
    public async Task QuickActionsUseSavedConfigurationAndKeepTheirOwnCommands(string locale, string title)
    {
        using var culture = new TestCultureScope(locale);
        using var context = Context(true);
        await context.Services.GetRequiredService<ConsoleContextState>().EnsureLoadedAsync(CancellationToken.None);
        var reconciliations = 0;
        var cut = context.Render<AgentResourceSummary>(p => p
            .Add(c => c.Resource, Resource()).Add(c => c.Identity, "<p>Identity supplied by the template</p>")
            .Add(c => c.RunHref, "/agents/example/run?scopeRef=%2Finstance&context=%2Finstance")
            .Add(c => c.CanReconcile, true).Add(c => c.OnReconcile, () => reconciliations++));
        Assert.AreEqual(title, cut.Find("[data-testid=agent-quick-actions] h2").TextContent);
        Assert.Contains("Saved instructions", cut.Find("[data-testid=agent-configuration-summary]").TextContent);
        Assert.AreEqual("/agents/example/run?scopeRef=%2Finstance&context=%2Finstance", cut.Find("[data-testid=agent-summary-run]").GetAttribute("href"));
        Assert.IsEmpty(cut.FindAll(".collection-actions-menu, [data-testid=resource-preview-content]"));
        await cut.Find("[data-testid=agent-summary-reconcile]").ClickAsync(new());
        Assert.AreEqual(1, reconciliations);
        cut.Render(p => p.Add(c => c.HasDraft, true));
        Assert.IsTrue(cut.Find("[data-testid=agent-summary-reconcile]").HasAttribute("disabled"));
        cut.Render(p => p.Add(c => c.HasDraft, false).Add(c => c.Busy, true));
        Assert.IsTrue(cut.Find("[data-testid=agent-summary-reconcile]").HasAttribute("disabled"));
        cut.Render(p => p.Add(c => c.Busy, false).Add(c => c.CanReconcile, false));
        Assert.IsEmpty(cut.FindAll("[data-testid=agent-summary-reconcile]"));
        Assert.HasCount(1, cut.FindAll("[data-testid=agent-summary-run]"));
    }

    [TestMethod]
    public async Task ReaderKeepsSummaryWithoutAnEmptyQuickActionPanel()
    {
        using var context = Context(false);
        await context.Services.GetRequiredService<ConsoleContextState>().EnsureLoadedAsync(CancellationToken.None);
        var cut = context.Render<AgentResourceSummary>(p => p.Add(c => c.Resource, Resource())
            .Add(c => c.Identity, "<p>Identity</p>").Add(c => c.RunHref, "/agents/example/run").Add(c => c.CanReconcile, true));
        Assert.IsEmpty(cut.FindAll("[data-testid=agent-quick-actions]"));
        Assert.Contains("Saved instructions", cut.Find("[data-testid=agent-configuration-summary]").TextContent);
    }

    private static AgentResource Resource() => new()
    {
        Uid = Guid.NewGuid(), ScopeRef = ResourceScopeRef.Instance, ApiVersion = ResourceApiVersions.CoreV1, Kind = AgentResourceKinds.Agent,
        Metadata = new() { Name = "example" },
        Definition = new() { DisplayName = "Example", Instructions = "Saved instructions", ModelProfile = new("default", ResourceScopeRef.Instance) }
    };

    private static BunitContext Context(bool commands)
    {
        var permissions = new HashSet<string> { AuthorizationPermissions.ResourcesRead };
        if (commands) { permissions.Add(AuthorizationPermissions.ResourcesWrite); permissions.Add(AuthorizationPermissions.RunsExecute); }
        var context = new BunitContext().WithCollectionPresentation().WithResourceListContext(new() { Permissions = permissions });
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        return context;
    }
}
