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
            ("Design", [("Agents", "/agents"), ("Flows", "/flows"), ("Schemas", "/schemas"), ("Entries", "/entries"), ("Resource plans", "/resource-plans"), ("Model profiles", "/modelprofiles"), ("Data Source Profiles", "/data-source-profiles")]),
            ("Automate", [("Triggers", "/triggers")]),
            ("Operate", [("Conversations", "/conversations"), ("Tasks", "/tasks")]),
            ("Observe", [("Agent instances", "/agent-instances"), ("Agent runs", "/agent-runs"), ("Flow runs", "/flow-runs"), ("Run events", "/run-events")]),
            ("Workplace", [("Configuration", "/workspaces")]),
            ("Resources", [("Packs", "/packs"), ("Data Sources", "/data-sources"), ("Knowledge Sources", "/knowledge-sources"), ("Artifacts", "/artifacts"), ("Sources", "/settings/sources"), ("Source registries", "/settings/source-registries")]),
            ("Integrations", [("MCP & Tools", "/tools"), ("Extensions", "/extensions"), ("Model providers", "/modelproviders"), ("Source providers", "/sourceproviders")]),
            ("Configuration", [("Runtime profiles", "/runtimeprofiles"), ("Secrets", "/secrets"), ("Resource scopes", "/settings/resource-scopes")]),
            ("System", [("Workers", "/settings/runtime-workers"), ("Organization", "/settings/organization"), ("Bootstrap", "/settings/bootstrap"), ("Cleanup", "/cleanup"), ("Settings", "/settings")]));

        Assert.AreEqual(0, rendered.FindAll(".side-nav a[href='/settings/profile']").Count);
        Assert.AreEqual("Profile", rendered.Find(".topbar-actions a[href='/settings/profile']").GetAttribute("aria-label"));
        Assert.AreEqual("Resource plans", rendered.Find(".side-nav a[href='/resource-plans']").GetAttribute("data-nav-tooltip"));
        Assert.IsTrue(rendered.Find(".side-nav a[href='/resource-plans']").Closest(".navigation-group")?.ClassList.Contains("navigation-group-design"));
        Assert.IsTrue(rendered.Find(".side-nav a[href='/triggers']").Closest(".navigation-group")?.ClassList.Contains("navigation-group-automate"));
        Assert.IsTrue(rendered.Find(".side-nav a[href='/flow-runs']").Closest(".navigation-group")?.ClassList.Contains("navigation-group-observe"));
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
        Assert.AreEqual("Profils de source de données", groups[1].QuerySelector("a[href='/data-source-profiles']")?.TextContent.Trim());
        Assert.AreEqual("Sources de données", groups[6].QuerySelector("a[href='/data-sources']")?.TextContent.Trim());
        Assert.AreEqual("Schémas", groups[1].QuerySelector("a[href='/schemas']")?.TextContent.Trim());
        Assert.AreEqual("Profil", rendered.Find(".topbar-actions a[href='/settings/profile']").GetAttribute("aria-label"));
    }

    [TestMethod]
    public void NavigationDistinguishesProfilesAndKnowledgeSourcesFromTheirRelatedResources()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext(AllNavigationPermissions);

        var rendered = RenderLayout(context);

        Assert.AreEqual(
            "_content/Agentstration.Web.Components/tabler/tabler-sprite.svg#tabler-adjustments",
            rendered.Find(".side-nav a[href='/modelprofiles'] use").GetAttribute("href"));
        Assert.AreEqual(
            "_content/Agentstration.Web.Components/tabler/tabler-sprite.svg#tabler-database-cog",
            rendered.Find(".side-nav a[href='/data-source-profiles'] use").GetAttribute("href"));
        Assert.AreEqual(
            "_content/Agentstration.Web.Components/tabler/tabler-sprite.svg#tabler-brain",
            rendered.Find(".side-nav a[href='/knowledge-sources'] use").GetAttribute("href"));
        Assert.AreEqual(
            "_content/Agentstration.Web.Components/tabler/tabler-sprite.svg#tabler-plug-connected",
            rendered.Find(".side-nav a[href='/sourceproviders'] use").GetAttribute("href"));
        Assert.AreEqual(
            "_content/Agentstration.Web.Components/tabler/tabler-sprite.svg#tabler-binary-tree",
            rendered.Find(".side-nav a[href='/settings/resource-scopes'] use").GetAttribute("href"));
        Assert.AreEqual(
            "_content/Agentstration.Web.Components/tabler/tabler-sprite.svg#tabler-database-search",
            rendered.Find(".side-nav a[href='/settings/source-registries'] use").GetAttribute("href"));
        Assert.AreNotEqual(
            rendered.Find(".side-nav a[href='/resource-plans'] svg").InnerHtml,
            rendered.Find(".side-nav a[href='/modelprofiles'] svg").InnerHtml);
        Assert.AreNotEqual(
            rendered.Find(".side-nav a[href='/data-sources'] svg").InnerHtml,
            rendered.Find(".side-nav a[href='/data-source-profiles'] svg").InnerHtml);
        Assert.AreNotEqual(
            rendered.Find(".side-nav a[href='/settings/sources'] svg").InnerHtml,
            rendered.Find(".side-nav a[href='/knowledge-sources'] svg").InnerHtml);
        Assert.AreNotEqual(
            rendered.Find(".side-nav a[href='/data-sources'] svg").InnerHtml,
            rendered.Find(".side-nav a[href='/sourceproviders'] svg").InnerHtml);
        Assert.AreNotEqual(
            rendered.Find(".side-nav a[href='/resource-plans'] svg").InnerHtml,
            rendered.Find(".side-nav a[href='/settings/resource-scopes'] svg").InnerHtml);
        Assert.AreNotEqual(
            rendered.Find(".side-nav a[href='/data-sources'] svg").InnerHtml,
            rendered.Find(".side-nav a[href='/settings/source-registries'] svg").InnerHtml);
    }

    [TestMethod]
    public void NavigationPreservesPermissionBasedVisibility()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext(new HashSet<string>(StringComparer.Ordinal));

        var rendered = RenderLayout(context);

        Assert.AreEqual(0, rendered.FindAll(".side-nav a[href='/resource-plans']").Count);
        Assert.AreEqual(0, rendered.FindAll(".side-nav a[href='/knowledge-source-profiles']").Count);
        Assert.AreEqual(0, rendered.FindAll(".side-nav a[href='/data-source-profiles']").Count);
        Assert.AreEqual(0, rendered.FindAll(".side-nav a[href='/data-sources']").Count);
        Assert.AreEqual(0, rendered.FindAll(".side-nav a[href='/schemas']").Count);
        Assert.AreEqual(0, rendered.FindAll(".side-nav a[href='/conversations']").Count);
        Assert.AreEqual(0, rendered.FindAll(".side-nav a[href='/settings/resource-scopes']").Count);
        Assert.AreEqual(0, rendered.FindAll(".side-nav a[href='/cleanup']").Count);
        Assert.AreEqual(1, rendered.FindAll(".side-nav a[href='/agent-instances']").Count);
    }

    [TestMethod]
    public void ContextSelectorPresentsOneSearchableInstanceTenantWorkspaceHierarchy()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext(AllNavigationPermissions);
        var rendered = RenderLayout(context);

        rendered.Find("[data-testid='context-selector-trigger']").Click();

        Assert.AreEqual(1, rendered.FindAll("[data-testid='context-selector']").Count);
        Assert.AreEqual(1, rendered.FindAll(".context-option-instance").Count);
        Assert.AreEqual(1, rendered.FindAll(".context-option-tenant").Count);
        Assert.AreEqual(1, rendered.FindAll(".context-option-workspace").Count);
        Assert.AreEqual(1, rendered.FindAll(".context-tenant-group").Count);
        Assert.AreEqual(1, rendered.FindAll(".context-tenant-group .context-tenant-workspaces .context-option-workspace").Count);
        Assert.AreEqual("Workspace", rendered.Find("[data-testid='context-selector-trigger'] .context-kind").TextContent.Trim());
        Assert.AreEqual("Test organization / Default workspace",
            rendered.Find("[data-testid='console-breadcrumb'] span").TextContent.Trim());

        rendered.Find("[data-testid='context-selector-search']").Input("Instance");
        Assert.AreEqual(1, rendered.FindAll(".context-option-instance").Count);
        Assert.AreEqual(0, rendered.FindAll(".context-option-tenant").Count);
        Assert.AreEqual(0, rendered.FindAll(".context-option-workspace").Count);
    }

    [TestMethod]
    public void ContextSelectorKeepsAuthorizedWorkspaceVisibleWithoutTenantAdministration()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext(AllNavigationPermissions, workspaceOnly: true);
        var rendered = RenderLayout(context);

        rendered.Find("[data-testid='context-selector-trigger']").Click();

        Assert.AreEqual(0, rendered.FindAll(".context-option-instance").Count);
        Assert.AreEqual(0, rendered.FindAll(".context-option-tenant").Count);
        Assert.AreEqual(1, rendered.FindAll(".context-tenant-label").Count);
        Assert.AreEqual("Test organization", rendered.Find(".context-tenant-label").TextContent.Trim());
        Assert.AreEqual(1, rendered.FindAll(".context-option-workspace").Count);
    }

    [TestMethod]
    public void NavigationContextChangeRefreshesTheCircuitWithoutForcingAPageReload()
    {
        using var culture = new CultureScope("en-US");
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddAgentstrationWebComponents();
        var provider = new SwitchingContextProvider(AllNavigationPermissions);
        context.Services.AddScoped<IConsoleContextProvider>(_ => provider);
        var rendered = RenderLayout(context);
        rendered.WaitForAssertion(() => Assert.AreEqual(ConsoleContextKind.Instance,
            context.Services.GetRequiredService<ConsoleContextState>().Current?.SelectedKind));

        var workspaceScope = $"/workspaces/{SwitchingContextProvider.WorkspaceId:D}";
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(
            $"/agents/assistant?context={Uri.EscapeDataString(workspaceScope)}");

        rendered.WaitForAssertion(() =>
        {
            var current = context.Services.GetRequiredService<ConsoleContextState>().Current;
            Assert.AreEqual(ConsoleContextKind.Workspace, current?.SelectedKind);
            Assert.AreEqual(workspaceScope, current?.SelectedScopeRef);
            Assert.AreEqual(2, provider.CallCount);
        });
    }

    private static readonly IReadOnlySet<string> AllNavigationPermissions = new HashSet<string>(
        ["resources/read", "resources/delete", "runs/read", "runs/delete"],
        StringComparer.Ordinal);

    private static BunitContext CreateContext(IReadOnlySet<string> permissions, bool workspaceOnly = false)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddAgentstrationWebComponents();
        context.Services.AddScoped<IConsoleContextProvider>(_ => new StubContextProvider(permissions, workspaceOnly));
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

    private sealed class StubContextProvider(IReadOnlySet<string> permissions, bool workspaceOnly) : IConsoleContextProvider
    {
        public Task<ConsoleContextSnapshot> GetAsync(CancellationToken cancellationToken)
        {
            var tenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            var workspaceId = Guid.Parse("22222222-2222-2222-2222-222222222222");
            return Task.FromResult(new ConsoleContextSnapshot(
                Guid.Parse("33333333-3333-3333-3333-333333333333"),
                "Test User",
                $"/workspaces/{workspaceId:D}",
                ConsoleContextKind.Workspace,
                tenantId,
                "test",
                "Test organization",
                workspaceId,
                "default",
                "Default workspace",
                permissions,
                [new(workspaceId, tenantId, "test", "Test organization", "default", "Default workspace")],
                workspaceOnly
                    ? [new($"/workspaces/{workspaceId:D}", ConsoleContextKind.Workspace, tenantId, "test", "Test organization", workspaceId, "default", "Default workspace")]
                    :
                    [
                        new("/instance", ConsoleContextKind.Instance, null, null, null, null, null, "Instance"),
                        new($"/tenants/{tenantId:D}", ConsoleContextKind.Tenant, tenantId, "test", "Test organization", null, null, "Test organization"),
                        new($"/workspaces/{workspaceId:D}", ConsoleContextKind.Workspace, tenantId, "test", "Test organization", workspaceId, "default", "Default workspace")
                    ]));
        }
    }

    private sealed class SwitchingContextProvider(IReadOnlySet<string> permissions) : IConsoleContextProvider
    {
        public static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        public static readonly Guid WorkspaceId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        public int CallCount { get; private set; }

        public async Task<ConsoleContextSnapshot> GetAsync(CancellationToken cancellationToken)
        {
            await Task.Yield();
            CallCount++;
            var workspaceScope = $"/workspaces/{WorkspaceId:D}";
            var selectedScope = CallCount == 1 ? "/instance" : workspaceScope;
            var selectedKind = CallCount == 1 ? ConsoleContextKind.Instance : ConsoleContextKind.Workspace;
            return new ConsoleContextSnapshot(
                Guid.Parse("33333333-3333-3333-3333-333333333333"),
                "Test User",
                selectedScope,
                selectedKind,
                TenantId,
                "test",
                "Test organization",
                WorkspaceId,
                "default",
                "Default workspace",
                permissions,
                [new(WorkspaceId, TenantId, "test", "Test organization", "default", "Default workspace")],
                [
                    new("/instance", ConsoleContextKind.Instance, null, null, null, null, null, "Instance"),
                    new(workspaceScope, ConsoleContextKind.Workspace, TenantId, "test", "Test organization", WorkspaceId, "default", "Default workspace")
                ]);
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
