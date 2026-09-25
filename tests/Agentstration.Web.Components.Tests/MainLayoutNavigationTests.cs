using System.Globalization;
using Agentstration.Web.Components.State;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Components.Tests;

[TestClass]
[DoNotParallelize]
public sealed class MainLayoutNavigationTests
{
    [TestMethod]
    public void NavigationMatchesTargetEnglishGroupingLabelsAndRoutes()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext(AllNavigationPermissions);

        var rendered = RenderLayout(context);

        AssertNavigation(rendered,
            (string.Empty, [("Overview", "/")]),
            ("Design", [("Agents", "/agents"), ("Flows", "/flows"), ("Entries", "/entries"), ("Resource plans", "/resource-plans"), ("Model profiles", "/modelprofiles")]),
            ("Automate", [("Triggers", "/triggers")]),
            ("Operate", [("Conversations", "/conversations"), ("Tasks", "/tasks")]),
            ("Observe", [("Deployments", "/deployments"), ("Agent runs", "/agent-runs"), ("Flow runs", "/flow-runs"), ("Run events", "/run-events")]),
            ("Workplace", [("Configuration", "/workspaces")]),
            ("Resources", [("Packs", "/packs"), ("Sources", "/settings/sources"), ("Source registries", "/settings/source-registries")]),
            ("Integrations", [("MCP & Tools", "/tools"), ("Extensions", "/extensions"), ("Model providers", "/modelproviders"), ("Source providers", "/sourceproviders")]),
            ("Configuration", [("Runtime profiles", "/runtimeprofiles"), ("Secrets", "/secrets"), ("Resource scopes", "/settings/resource-scopes")]),
            ("System", [("Organization", "/settings/organization"), ("Bootstrap", "/settings/bootstrap"), ("Cleanup", "/cleanup"), ("Settings", "/settings")]));

        Assert.AreEqual(0, rendered.FindAll(".side-nav a[href='/settings/profile']").Count);
        Assert.AreEqual("Profile", rendered.Find(".topbar-actions a[href='/settings/profile']").GetAttribute("aria-label"));
    }

    [TestMethod]
    public void NavigationUsesTargetFrenchLabels()
    {
        using var culture = new CultureScope("fr-FR");
        using var context = CreateContext(AllNavigationPermissions);

        var rendered = RenderLayout(context);
        var groups = rendered.FindAll(".navigation-group");

        CollectionAssert.AreEqual(
            new[] { "", "Concevoir", "Automatiser", "Exploiter", "Observer", "Workplace", "Ressources", "Intégrations", "Configuration", "Système" },
            groups.Select(GroupLabel).ToArray());
        Assert.AreEqual("Configuration", groups[5].QuerySelector("a")?.TextContent.Trim());
        Assert.AreEqual("MCP & Outils", groups[7].QuerySelector("a")?.TextContent.Trim());
        Assert.AreEqual("Profil", rendered.Find(".topbar-actions a[href='/settings/profile']").GetAttribute("aria-label"));
    }

    [TestMethod]
    public void NavigationPreservesPermissionBasedVisibility()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext(new HashSet<string>(StringComparer.Ordinal));

        var rendered = RenderLayout(context);

        Assert.AreEqual(0, rendered.FindAll(".side-nav a[href='/resource-plans']").Count);
        Assert.AreEqual(0, rendered.FindAll(".side-nav a[href='/conversations']").Count);
        Assert.AreEqual(0, rendered.FindAll(".side-nav a[href='/settings/resource-scopes']").Count);
        Assert.AreEqual(0, rendered.FindAll(".side-nav a[href='/cleanup']").Count);
        Assert.AreEqual(1, rendered.FindAll(".side-nav a[href='/deployments']").Count);
    }

    [TestMethod]
    public void NotificationFragmentOpensNotificationPanel()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext(AllNavigationPermissions);
        var rendered = RenderLayout(context);

        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/#notifications");

        rendered.WaitForAssertion(() => Assert.IsNotNull(rendered.Find("[data-testid='console-notifications-panel']")));
    }

    private static readonly IReadOnlySet<string> AllNavigationPermissions = new HashSet<string>(
        ["resources/read", "resources/delete", "runs/read", "runs/delete"],
        StringComparer.Ordinal);

    private static BunitContext CreateContext(IReadOnlySet<string> permissions)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddAgentstrationWebComponents();
        context.Services.AddScoped<IConsoleContextProvider>(_ => new StubContextProvider(permissions));
        return context;
    }

    private static IRenderedComponent<MainLayout> RenderLayout(BunitContext context) =>
        context.Render<MainLayout>(parameters => parameters.Add(
            component => component.Body,
            (RenderFragment)(builder => builder.AddMarkupContent(0, "<p>Current page</p>"))));

    private static void AssertNavigation(
        IRenderedComponent<MainLayout> rendered,
        params (string Group, (string Label, string Url)[] Items)[] expected)
    {
        var groups = rendered.FindAll(".navigation-group");
        Assert.AreEqual(expected.Length, groups.Count);

        for (var groupIndex = 0; groupIndex < expected.Length; groupIndex++)
        {
            Assert.AreEqual(expected[groupIndex].Group, GroupLabel(groups[groupIndex]));
            var actualItems = groups[groupIndex].QuerySelectorAll("a")
                .Select(link => (link.TextContent.Trim(), link.GetAttribute("href")))
                .ToArray();
            CollectionAssert.AreEqual(expected[groupIndex].Items, actualItems);
        }
    }

    private static string GroupLabel(AngleSharp.Dom.IElement group) =>
        group.QuerySelector("h2")?.TextContent.Trim() ?? string.Empty;

    private sealed class StubContextProvider(IReadOnlySet<string> permissions) : IConsoleContextProvider
    {
        public Task<ConsoleContextSnapshot> GetAsync(CancellationToken cancellationToken)
        {
            var tenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            var workspaceId = Guid.Parse("22222222-2222-2222-2222-222222222222");
            return Task.FromResult(new ConsoleContextSnapshot(
                Guid.Parse("33333333-3333-3333-3333-333333333333"),
                "Test User",
                tenantId,
                "test",
                "Test organization",
                workspaceId,
                "default",
                "Default workspace",
                permissions,
                [new(workspaceId, tenantId, "test", "Test organization", "default", "Default workspace")]));
        }
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo originalCulture = CultureInfo.CurrentCulture;
        private readonly CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;

        public CultureScope(string cultureName)
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultureName);
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }
}
