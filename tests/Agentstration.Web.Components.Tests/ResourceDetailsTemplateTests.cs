using Agentstration.Resources;
using Agentstration.Web.Components;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Components.Tests;

[TestClass]
public sealed class ResourceDetailsTemplateTests
{
    [TestMethod]
    [DataRow("en-US", "Required fields", "Section actions")]
    [DataRow("fr-FR", "Champs obligatoires", "Actions de la section")]
    public async Task SectionDeclarationsControlTheLegendAndActionRegionWithoutOwningCommands(string culture, string required, string actionLabel)
    {
        var previous = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo(culture);
            using var context = Context();
            var module = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-navigation.js");
            foreach (var path in new[] { "overview", "definition/configuration", "yaml" })
            {
                var target = "http://localhost/#" + path;
                module.Setup<string>("navigate", call => Equals(call.Arguments[0], target)).SetResult(target);
            }
            var calls = 0;
            RenderFragment action = builder =>
            {
                builder.OpenElement(0, "button"); builder.AddAttribute(1, "type", "button");
                builder.AddAttribute(2, "onclick", EventCallback.Factory.Create(this, () => calls++));
                builder.AddContent(3, "Inspect"); builder.CloseElement();
            };
            var presentations = new Dictionary<string, ResourceSectionPresentation>
            {
                ["definition/configuration"] = new() { HasRequiredFields = true, Actions = action },
                ["yaml"] = new() { Actions = action }
            };
            var cut = context.Render<ResourceDetailsTemplate>(p => p
                .Add(c => c.Name, "Example").Add(c => c.ResourceType, "Agent").Add(c => c.TechnicalName, "example")
                .Add(c => c.ScopeRef, ResourceScopeRef.Instance).Add(c => c.ResourceIdentity, "example|instance")
                .Add(c => c.Metadata, "<span>Generation 1</span>").Add(c => c.SectionPresentations, presentations)
                .Add(c => c.OverviewConfiguration, "<p>Saved configuration</p>")
                .Add(c => c.DefinitionContent, _ => builder => builder.AddContent(0, "Fields"))
                .Add(c => c.YamlContent, "<textarea></textarea>")
                .Add(c => c.HasDraft, true).Add(c => c.DraftActions, "<button>Save draft</button>"));
            Assert.IsEmpty(cut.FindAll(".required-fields-note, .resource-section-actions"));
            var navigation = cut.FindComponent<ResourceNavigation>().Instance;
            await cut.InvokeAsync(() => navigation.NavigateSection("definition/configuration"));
            Assert.Contains(required, cut.Find(".resource-details-guidance .required-fields-note").TextContent);
            Assert.Contains("Generation 1", cut.Find(".resource-header").TextContent);
            var actions = cut.Find(".resource-details-main > .resource-section-actions");
            Assert.AreEqual(actionLabel, actions.GetAttribute("aria-label"));
            Assert.AreEqual("group", actions.GetAttribute("role"));
            actions.QuerySelector("button")!.Click();
            Assert.AreEqual(1, calls);
            Assert.HasCount(1, cut.FindAll(".resource-details-main > .unsaved-changes-bar"));
            cut.Render(p => p.Add(c => c.Busy, true));
            Assert.AreEqual("true", cut.Find(".resource-section-actions").GetAttribute("aria-busy"));
            presentations["definition/configuration"] = presentations["definition/configuration"] with { ReadOnly = true };
            cut.Render(p => p.Add(c => c.SectionPresentations, presentations));
            Assert.IsEmpty(cut.FindAll(".required-fields-note"));
            Assert.IsEmpty(cut.FindAll(".resource-section-actions"), "Mutation commands are not available in a read-only section.");
            Assert.IsNotNull(cut.Find(".resource-readonly-notice"));
            presentations["definition/configuration"] = presentations["definition/configuration"] with { ReadOnlyActions = action };
            cut.Render(p => p.Add(c => c.SectionPresentations, presentations));
            Assert.HasCount(1, cut.FindAll(".resource-section-actions button"), "Read-only inspection actions are explicit.");
            await cut.InvokeAsync(() => navigation.NavigateSection("yaml"));
            Assert.AreEqual("yaml", cut.Instance.SectionState.CurrentPath);
            Assert.IsEmpty(cut.FindAll(".required-fields-note"));
            Assert.HasCount(1, cut.FindAll(".resource-section-actions button"));
            cut.Render(p => p.Add(c => c.State, "<p>Loading</p>"));
            Assert.IsEmpty(cut.FindAll(".required-fields-note, .resource-section-actions, .unsaved-changes-bar"));
        }
        finally { System.Globalization.CultureInfo.CurrentUICulture = previous; }
    }

    [TestMethod]
    [DataRow("en-US", "Overview", "Definition", "Identity", "Configuration", "Access")]
    [DataRow("fr-FR", "Vue d’ensemble", "Définition", "Identité", "Configuration", "Accès")]
    public async Task StandardSectionsOwnOrderingIdentityAndRetainedEditors(string culture, string overview, string definition, string identity, string configuration, string access)
    {
        var previous = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo(culture);
            using var context = Context();
            var module = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-navigation.js");
            foreach (var path in new[] { "overview", "definition/identity", "definition/configuration", "definition/tools", "history", "access", "yaml" })
            {
                var target = "http://localhost/#" + path;
                module.Setup<string>("navigate", call => Equals(call.Arguments[0], target)).SetResult(target);
            }
            RenderFragment probe = builder => { builder.OpenComponent<DraftProbe>(0); builder.CloseComponent(); };
            var sections = new ResourceSectionState();
            var cut = context.Render<ResourceDetailsTemplate>(p => p
                .Add(c => c.Name, "Saved name").Add(c => c.ResourceType, "Agent")
                .Add(c => c.TechnicalName, "example").Add(c => c.ScopeRef, ResourceScopeRef.Instance)
                .Add(c => c.ResourceIdentity, "example|instance").Add(c => c.SectionTestIdPrefix, "example")
                .Add(c => c.SectionState, sections)
                .Add(c => c.OverviewMetrics, "<article class='metric-card'>1</article>")
                .Add(c => c.OverviewConfiguration, "<p>Saved configuration</p>")
                .Add(c => c.DefinitionContent, _ => probe)
                .Add(c => c.DefinitionSections, new[] { new ResourceNavigationItem("definition/tools", "Tools", "tool") })
                .Add(c => c.AdditionalSections, new[] { new ResourceNavigationItem("history", "History", "clock") })
                .Add(c => c.AdditionalContent, _ => probe)
                .Add(c => c.AccessContent, probe).Add(c => c.YamlContent, probe));
            var navigation = cut.FindComponent<ResourceNavigation>().Instance;
            CollectionAssert.AreEqual(new[] { overview, definition, identity, configuration, "Tools", "History", access, "YAML" },
                cut.FindAll(".resource-navigation-rail .resource-navigation-entry span").Select(item => item.TextContent).ToArray());
            CollectionAssert.AreEqual(new[] { "resource-overview-metrics", "panel form-section resource-identity-summary", "panel form-section resource-configuration-summary" },
                cut.Find("[data-testid=example-overview]").Children.Take(3).Select(item => item.ClassName).ToArray());
            Assert.IsEmpty(cut.FindComponents<DraftProbe>(), "Access, definition and YAML must mount only on first visit.");
            await cut.InvokeAsync(() => navigation.NavigateSection("definition/identity"));
            Assert.AreEqual("definition/identity", sections.CurrentPath);
            var editor = cut.FindComponent<DraftProbe>().Instance;
            cut.Find(".resource-details-section input").Input("Incomplete definition");
            await cut.InvokeAsync(() => navigation.NavigateSection("definition/configuration"));
            Assert.AreSame(editor, cut.FindComponent<DraftProbe>().Instance);
            await cut.InvokeAsync(() => navigation.NavigateSection("access"));
            var policy = cut.FindComponents<DraftProbe>().Last().Instance;
            cut.FindAll(".resource-details-section input").Last().Input("Pending policy");
            await cut.InvokeAsync(() => navigation.NavigateSection("yaml"));
            var yaml = cut.FindComponents<DraftProbe>().Last().Instance;
            cut.Find("[data-testid=example-yaml-editor] input").Input("invalid: [yaml");
            await cut.InvokeAsync(() => navigation.NavigateSection("definition/tools"));
            Assert.AreEqual("definition/tools", sections.DefinitionPath);
            Assert.AreSame(editor, cut.FindComponents<DraftProbe>().First().Instance);
            await cut.InvokeAsync(() => navigation.NavigateSection("overview"));
            Assert.Contains("Saved name", cut.Find("[data-testid=example-identity-summary]").TextContent);
            Assert.AreSame(policy, cut.FindComponents<DraftProbe>()[1].Instance);
            Assert.AreSame(yaml, cut.FindComponents<DraftProbe>()[2].Instance);
            CollectionAssert.AreEqual(new[] { "Incomplete definition", "Pending policy", "invalid: [yaml" },
                cut.FindAll(".resource-details-section input").Select(item => item.GetAttribute("value")).ToArray());
            cut.Render(p => p.Add(c => c.AccessContent, (RenderFragment?)null));
            Assert.IsEmpty(cut.FindAll("[data-resource-section=access]"));
            Assert.AreEqual("yaml", cut.FindAll(".resource-navigation-rail a").Last().GetAttribute("data-resource-section"));
            cut.Render(p => p.Add(c => c.ResourceIdentity, "different|instance").Add(c => c.Name, "Different resource").Add(c => c.TechnicalName, "different"));
            Assert.IsEmpty(cut.FindComponents<DraftProbe>(), "Accepted resource changes must not reuse the previous resource's editors.");
            Assert.Contains("Different resource", cut.Find("[data-testid=example-identity-summary]").TextContent);
        }
        finally { System.Globalization.CultureInfo.CurrentUICulture = previous; }
    }

    [TestMethod]
    [DataRow("definition/configuration")]
    [DataRow("definition/identity")]
    [DataRow("yaml")]
    public void StandardSectionsRejectReplacingReservedDestinations(string path)
    {
        using var context = Context();
        Assert.Throws<ArgumentException>(() => context.Render<ResourceDetailsTemplate>(p => p
            .Add(c => c.Name, "Example").Add(c => c.ResourceType, "Agent").Add(c => c.TechnicalName, "example")
            .Add(c => c.ScopeRef, ResourceScopeRef.Instance).Add(c => c.ResourceIdentity, "example|instance")
            .Add(c => c.OverviewConfiguration, "<p>Configuration</p>")
            .Add(c => c.DefinitionContent, _ => builder => builder.AddContent(0, "Fields"))
            .Add(c => c.YamlContent, "<textarea></textarea>")
            .Add(c => c.DefinitionSections, new[] { new ResourceNavigationItem(path, "Replacement", "code") })));
    }

    [TestMethod]
    public void ResourceStateSuppressesEditorsAndDraftActionsAndActivationRemainsOptional()
    {
        using var context = Context();
        var cut = context.Render<ResourceDetailsTemplate>(p => p
            .Add(c => c.Name, "Inventory").Add(c => c.ResourceType, "Storage")
            .Add(c => c.ScopeRef, ResourceScopeRef.Instance)
            .Add(c => c.Content, "<input data-testid='editor' />")
            .Add(c => c.HasDraft, true).Add(c => c.DraftActions, "<button>Save</button>")
            .Add(c => c.State, "<p role='status'>Loading</p>"));
        Assert.IsEmpty(cut.FindAll("input, .unsaved-changes-bar, .resource-details-navigation"));
        Assert.AreEqual("/instance", cut.Find(".resource-owner-scope").GetAttribute("data-scope-ref"));
        cut.Render(p => p.Add(c => c.State, (RenderFragment?)null));
        Assert.IsNotNull(cut.Find(".resource-details-content input"));
        Assert.IsNotNull(cut.Find(".resource-details-main > .unsaved-changes-bar"));
        Assert.IsEmpty(cut.FindAll("[role=switch]"));
        cut.Render(p => p.Add(c => c.HasDraft, false));
        Assert.IsEmpty(cut.FindAll(".unsaved-changes-bar"));
    }

    [TestMethod]
    public async Task HeaderChangesAndOptionalNavigationDoNotRemountTheCallerEditor()
    {
        using var context = Context();
        RenderFragment editor = builder => { builder.OpenComponent<DraftProbe>(0); builder.CloseComponent(); };
        bool? activation = null;
        var cut = context.Render<ResourceDetailsTemplate>(p => p
            .Add(c => c.Name, "Service").Add(c => c.ResourceType, "Connected service")
            .Add(c => c.TechnicalName, "endpoint").Add(c => c.Namespace, "team")
            .Add(c => c.Content, editor).Add(c => c.Enabled, true)
            .Add(c => c.EnabledTestId, "service-activation")
            .Add(c => c.EnabledChanged, value => activation = value)
            .Add(c => c.ResourceIdentity, "endpoint|team|instance")
            .Add(c => c.Sections, new[] { new ResourceNavigationItem("overview", "Overview", "home") }));
        var instance = cut.FindComponent<DraftProbe>().Instance;
        await cut.Find(".resource-details-content input").InputAsync("Incomplete local draft");
        var toggle = cut.Find("[data-testid=service-activation]");
        Assert.AreEqual("switch", toggle.GetAttribute("role"));
        toggle.Change(false);
        Assert.AreEqual(false, activation);
        cut.Render(p => p.Add(c => c.Name, "Updated saved service").Add(c => c.Enabled, false)
            .Add(c => c.EnabledDisabled, true));
        Assert.AreSame(instance, cut.FindComponent<DraftProbe>().Instance);
        Assert.AreEqual("Incomplete local draft", cut.Find(".resource-details-content input").GetAttribute("value"));
        Assert.IsTrue(cut.Find("[data-testid=service-activation]").HasAttribute("disabled"));
        cut.Render(p => p.Add(c => c.EnabledTestId, (string?)null));
        Assert.IsFalse(cut.Find("[role=switch]").HasAttribute("data-testid"));
        Assert.AreEqual("team/endpoint", cut.Find(".resource-header-technical code").TextContent);
        Assert.IsNotNull(cut.Find(".resource-header .page-actions .resource-navigation-trigger"));
        await cut.Find(".resource-header .resource-navigation-trigger").ClickAsync();
        Assert.AreEqual("true", cut.Find(".resource-header .resource-navigation-trigger").GetAttribute("aria-expanded"));
        await cut.InvokeAsync(() => cut.FindComponent<ModalFrame>().Instance.OnClose.InvokeAsync());
        Assert.AreEqual("false", cut.Find(".resource-header .resource-navigation-trigger").GetAttribute("aria-expanded"));
        var oldNavigation = cut.FindComponent<ResourceNavigation>().Instance;
        var manager = context.Services.GetRequiredService<NavigationManager>();
        await cut.InvokeAsync(() => manager.NavigateTo("http://localhost/?context=%2Finstance#overview"));
        Assert.AreNotSame(oldNavigation, cut.FindComponent<ResourceNavigation>().Instance);
        Assert.AreSame(instance, cut.FindComponent<DraftProbe>().Instance);
        Assert.AreEqual("Incomplete local draft", cut.Find(".resource-details-content input").GetAttribute("value"));
    }

    [TestMethod]
    [DataRow("en-US", "Unable to load resource", "Retry", "Resource not found", "Access denied")]
    [DataRow("fr-FR", "Impossible de charger la ressource", "Réessayer", "Ressource introuvable", "Accès refusé")]
    public async Task TypedBlockingStatesKeepTheHeaderAndLocalizeRetry(string culture, string failed, string retry, string notFound, string forbidden)
    {
        var previous = System.Globalization.CultureInfo.CurrentUICulture;
        System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo(culture);
        try
        {
            using var context = Context();
            var retries = 0;
            var cut = context.Render<ResourceDetailsTemplate>(p => p
                .Add(c => c.Name, "example").Add(c => c.ResourceType, "Agent").Add(c => c.ResourceIdentity, "route|context")
                .Add(c => c.LoadState, new ResourceLoadState(ResourceLoadStatus.Loading))
                .Add(c => c.OnRetry, () => retries++)
                .Add(c => c.Actions, "<button>Reconcile</button>").Add(c => c.HasDraft, true).Add(c => c.DraftActions, "<button>Save</button>"));
            Assert.AreEqual("example", cut.Find(".resource-header h1").TextContent);
            Assert.AreEqual("true", cut.Find(".resource-details-body").GetAttribute("aria-busy"));
            Assert.IsNotNull(cut.Find(".resource-details-state [role=status]"));
            Assert.IsEmpty(cut.FindAll("button, .resource-details-navigation, .unsaved-changes-bar, .required-fields-note"));
            cut.Render(p => p.Add(c => c.LoadState, new ResourceLoadState(ResourceLoadStatus.Failed)));
            Assert.AreEqual(failed, cut.Find("h2").TextContent);
            Assert.AreEqual(retry, cut.Find("button").TextContent);
            await cut.Find("button").ClickAsync();
            Assert.AreEqual(1, retries);
            foreach (var (status, title) in new[] { (ResourceLoadStatus.NotFound, notFound), (ResourceLoadStatus.Forbidden, forbidden) })
            {
                cut.Render(p => p.Add(c => c.LoadState, new ResourceLoadState(status)));
                Assert.AreEqual(title, cut.Find("h2").TextContent);
                Assert.IsEmpty(cut.FindAll("button"));
            }
        }
        finally { System.Globalization.CultureInfo.CurrentUICulture = previous; }
    }

    [TestMethod]
    public async Task RefreshingAndFailedRefreshRetainTheEditorAndDraftActions()
    {
        using var context = Context();
        RenderFragment editor = builder => { builder.OpenComponent<DraftProbe>(0); builder.CloseComponent(); };
        var refreshes = 0;
        var retries = 0;
        var cut = context.Render<ResourceDetailsTemplate>(p => p
            .Add(c => c.Name, "example").Add(c => c.ResourceType, "Agent").Add(c => c.Content, editor)
            .Add(c => c.SectionTestIdPrefix, "example").Add(c => c.HasDraft, true).Add(c => c.DraftActions, "<button>Save</button>")
            .Add(c => c.LoadState, new ResourceLoadState(ResourceLoadStatus.Ready) { HasContent = true })
            .Add(c => c.OnRefresh, () => refreshes++).Add(c => c.OnRetry, () => retries++));
        var instance = cut.FindComponent<DraftProbe>().Instance;
        cut.Find("input").Input("Incomplete draft");
        await cut.Find("[data-testid=example-refresh]").ClickAsync();
        Assert.AreEqual(1, refreshes);
        cut.Render(p => p.Add(c => c.LoadState, new ResourceLoadState(ResourceLoadStatus.Refreshing) { HasContent = true }));
        Assert.IsNotNull(cut.Find(".resource-refresh-status[role=status]"));
        Assert.IsTrue(cut.Find("[data-testid=example-refresh]").HasAttribute("disabled"));
        cut.Render(p => p.Add(c => c.LoadState, new ResourceLoadState(ResourceLoadStatus.Failed) { HasContent = true, ErrorId = "trace-123" }));
        Assert.IsNotNull(cut.Find(".resource-refresh-error[role=alert]"));
        Assert.Contains("trace-123", cut.Find(".resource-refresh-error").TextContent);
        Assert.IsNotNull(cut.Find(".unsaved-changes-bar"));
        Assert.AreSame(instance, cut.FindComponent<DraftProbe>().Instance);
        Assert.AreEqual("Incomplete draft", cut.Find("input").GetAttribute("value"));
        await cut.Find("[data-testid=example-retry]").ClickAsync();
        Assert.AreEqual(1, retries);
        cut.Render(p => p.Add(c => c.LoadState, new ResourceLoadState(ResourceLoadStatus.Ready) { HasContent = true }));
        Assert.IsEmpty(cut.FindAll(".resource-refresh-error, .resource-refresh-status"));
        Assert.AreSame(instance, cut.FindComponent<DraftProbe>().Instance);
    }

    [TestMethod]
    [DataRow("en-US", ResourceReadOnlyReason.Inherited, "another scope")]
    [DataRow("fr-FR", ResourceReadOnlyReason.Inherited, "autre périmètre")]
    [DataRow("en-US", ResourceReadOnlyReason.Managed, "managed by the platform")]
    [DataRow("fr-FR", ResourceReadOnlyReason.Managed, "gérée par la plateforme")]
    [DataRow("en-US", ResourceReadOnlyReason.InsufficientPermissions, "do not have permission")]
    [DataRow("fr-FR", ResourceReadOnlyReason.InsufficientPermissions, "droits nécessaires")]
    public void DefinitionReadOnlyReasonsAreLocalizedAndSuppressDraftMutationCommands(string culture, ResourceReadOnlyReason reason, string message)
    {
        var previous = System.Globalization.CultureInfo.CurrentUICulture;
        System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo(culture);
        try
        {
            using var context = Context();
            var cut = context.Render<ResourceDetailsTemplate>(p => p.Add(c => c.Name, "Example").Add(c => c.ResourceType, "Agent")
                .Add(c => c.Content, "<p>Saved definition</p>").Add(c => c.DefinitionReadOnlyReason, reason)
                .Add(c => c.HasDraft, true).Add(c => c.DraftActions, "<button>Save</button>"));
            Assert.Contains(message, cut.Find(".resource-readonly-notice").TextContent);
            Assert.IsEmpty(cut.FindAll(".unsaved-changes-bar"));
            Assert.Contains("Saved definition", cut.Markup);
        }
        finally { System.Globalization.CultureInfo.CurrentUICulture = previous; }
    }

    private static BunitContext Context()
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var navigation = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-navigation.js");
        navigation.Setup<string>("navigate", _ => true).SetResult("http://localhost/#overview");
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        return context;
    }

    public sealed class DraftProbe : ComponentBase
    {
        private string draft = string.Empty;
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "input");
            builder.AddAttribute(1, "value", draft);
            builder.AddAttribute(2, "oninput", EventCallback.Factory.Create<ChangeEventArgs>(this, args => draft = args.Value?.ToString() ?? string.Empty));
            builder.CloseElement();
        }
    }
}
