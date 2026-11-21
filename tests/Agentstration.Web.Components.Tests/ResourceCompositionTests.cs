using Agentstration.Web.Components;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Components.Tests;

[TestClass]
public sealed class ResourceCompositionTests
{
    [TestMethod]
    [DataRow("en-US", "Resource identity", "Workspace: Project workspace")]
    [DataRow("fr-FR", "Identité de la ressource", "Workspace : Project workspace")]
    public async Task CompactHeaderExposesSavedIdentityAndCopiesTheCompleteDescription(string culture, string label, string scopeLabel)
    {
        var previous = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo(culture);
            using var context = Context();
            const string description = "A complete saved description.\nA second line that must remain available for copying.";
            var module = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-ui.js");
            var copy = module.SetupVoid("copyText", description); copy.SetVoidResult();
            var page = context.Render<ResourceHeader>(p => p.Add(c => c.Name, "Saved resource").Add(c => c.ResourceType, "Trigger")
                .Add(c => c.TechnicalName, "long-technical-name").Add(c => c.Namespace, "team.resources")
                .Add(c => c.ScopeRef, Agentstration.Resources.ResourceScopeRef.Workspace(Guid.NewGuid()))
                .Add(c => c.Description, description).Add(c => c.ShowTechnicalIdentity, false)
                .Add(c => c.CollectionHref, "/triggers?context=%2Finstance").Add(c => c.CollectionLabel, "Trigger list"));
            Assert.AreEqual(label, page.Find("[data-testid=resource-identity-open]").GetAttribute("aria-label"));
            Assert.AreEqual(description, page.Find(".resource-description").GetAttribute("title"));
            Assert.AreEqual("/triggers?context=%2Finstance", page.Find(".resource-header-toolbar .resource-collection-link").GetAttribute("href"));
            Assert.AreEqual("Trigger list", page.Find(".resource-collection-link").GetAttribute("aria-label"));
            Assert.AreEqual(string.Empty, page.Find(".resource-collection-link").TextContent);
            Assert.IsNotNull(page.Find(".resource-header-heading > .resource-title-row"));
            Assert.IsNotNull(page.Find(".resource-header-top > .resource-header-toolbar"));
            Assert.HasCount(1, page.FindAll(".resource-title-row [data-testid=resource-identity-open]"));
            Assert.IsEmpty(page.FindAll(".resource-header-toolbar [data-testid=resource-identity-open], .resource-header-technical"));
            var summary = page.FindComponent<ResourceIdentityPopover>().FindComponent<ResourceIdentitySummary>();
            Assert.AreEqual(description, summary.Find(".resource-text .copyable-text").TextContent);
            await summary.Find(".resource-text .copy-value").ClickAsync(new());
            Assert.HasCount(1, copy.Invocations);
            summary.Render(p => p.Add(c => c.Scope, builder =>
            {
                builder.OpenComponent<ResourceScopeBadge>(0);
                builder.AddAttribute(1, nameof(ResourceScopeBadge.ScopeRef), page.Instance.ScopeRef);
                builder.AddAttribute(2, nameof(ResourceScopeBadge.DisplayName), "Project workspace");
                builder.AddAttribute(3, nameof(ResourceScopeBadge.JoinedName), true);
                builder.CloseComponent();
            }));
            Assert.AreEqual(scopeLabel, summary.Find(".resource-scope-text .copyable-text").TextContent);
        }
        finally { System.Globalization.CultureInfo.CurrentUICulture = previous; }
    }

    [TestMethod]
    public async Task EachLabelCopiesItsOwnKeyAndValueWithoutChangingOtherLabels()
    {
        using var context = Context();
        var module = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-ui.js");
        var copy = module.SetupVoid("copyText", "team : data");
        copy.SetVoidResult();
        var values = new Dictionary<string, string> { ["environment"] = "local", ["team"] = "data" };
        var labels = context.Render<LabelsSummary>(p => p.Add(c => c.Values, values));

        Assert.HasCount(2, labels.FindAll(".resource-label .copy-value"));
        await labels.Find(".resource-label:last-child .copy-value").ClickAsync(new());

        Assert.HasCount(1, copy.Invocations);
        Assert.AreEqual("environment : local", labels.Find(".resource-label:first-child").TextContent);
        Assert.AreEqual("data", values["team"]);
        Assert.HasCount(1, labels.FindAll("[role=status]"));
    }

    [TestMethod]
    public void ScreensCanEnrichHeaderBadgesWithTheSameKeyValuePresentationAsLabels()
    {
        using var context = Context();
        RenderFragment facts = builder =>
        {
            builder.OpenComponent<ResourceMetadataBadge>(0);
            builder.AddAttribute(1, nameof(ResourceMetadataBadge.Label), "Environment");
            builder.AddAttribute(2, nameof(ResourceMetadataBadge.Value), "Production");
            builder.CloseComponent();
        };
        var page = context.Render<ResourceHeader>(p => p.Add(c => c.Name, "Example").Add(c => c.ResourceType, "Flow").Add(c => c.Metadata, facts));
        Assert.AreEqual("Environment : Production", page.Find(".resource-header-badges .resource-metadata-badge").TextContent);
        Assert.IsEmpty(page.FindAll(".resource-header-copy > .resource-metadata"));
        var labels = context.Render<LabelsSummary>(p => p.Add(c => c.Values, new Dictionary<string, string> { ["team"] = "data" }));
        Assert.AreEqual("team : data", labels.Find(".resource-label").TextContent);
        Assert.HasCount(1, labels.FindComponents<ResourceMetadataBadge>());
    }

    [TestMethod]
    [DataRow("en-US", "Workspace · Project workspace")]
    [DataRow("fr-FR", "Espace de travail · Project workspace")]
    public void IdentitySummariesPresentTheOwnerAsLocalizedTextWhileHeadersRetainBadges(string culture, string label)
    {
        var previous = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo(culture);
            using var context = Context();
            var owner = Agentstration.Resources.ResourceScopeRef.Workspace(Guid.NewGuid());
            RenderFragment scope = builder =>
            {
                builder.OpenComponent<ResourceScopeBadge>(0);
                builder.AddAttribute(1, nameof(ResourceScopeBadge.ScopeRef), owner);
                builder.AddAttribute(2, nameof(ResourceScopeBadge.DisplayName), "Project workspace");
                builder.CloseComponent();
            };
            var summary = context.Render<ResourceIdentitySummary>(p => p.Add(c => c.DisplayName, "Example")
                .Add(c => c.TechnicalName, "example").Add(c => c.Scope, scope));
            Assert.AreEqual(label, summary.Find(".resource-scope-text").TextContent);
            Assert.IsEmpty(summary.FindAll(".resource-scope-badge, input"));
            Assert.DoesNotContain(owner.Value, summary.Markup);
            var header = context.Render<ResourceHeader>(p => p.Add(c => c.Name, "Example").Add(c => c.ResourceType, "Agent").Add(c => c.ScopeRef, owner));
            Assert.HasCount(1, header.FindAll(".resource-scope-badge"));
        }
        finally { System.Globalization.CultureInfo.CurrentUICulture = previous; }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task IdentityDisclosureCanStartOpenIndependentlyOfHeaderIdentity(bool headerOwnsIdentity)
    {
        using var context = Context();
        var summary = context.Render<ResourceIdentitySummary>(p => p.Add(c => c.DisplayName, "Example")
            .Add(c => c.TechnicalName, "example").Add(c => c.HeaderOwnsIdentity, headerOwnsIdentity)
            .Add(c => c.Collapsible, true).Add(c => c.InitiallyCollapsed, false));
        var toggle = summary.Find(".panel-disclosure");
        Assert.AreEqual("true", toggle.GetAttribute("aria-expanded"));
        await toggle.ClickAsync(new());
        Assert.AreEqual("false", toggle.GetAttribute("aria-expanded"));
        Assert.IsTrue(summary.Find(".form-section-body").HasAttribute("hidden"));
        summary.Render();
        Assert.AreEqual("false", toggle.GetAttribute("aria-expanded"));
        await toggle.ClickAsync(new());
        Assert.AreEqual("true", toggle.GetAttribute("aria-expanded"));
        Assert.AreEqual(!headerOwnsIdentity, summary.FindAll("dd").Any(value => value.TextContent == "Example"));
    }

    [TestMethod]
    public void HeadersShowExactOwnerAndOmitTheProductEyebrow()
    {
        const string scopeLabel = "Instance";
        using var context = Context();
        var page = context.Render<PageHeader>(p => p.Add(v => v.Title, "Service")
            .Add(v => v.ScopeRef, Agentstration.Resources.ResourceScopeRef.Instance));
        Assert.IsEmpty(page.FindAll(".eyebrow"));
        Assert.AreEqual(scopeLabel, page.Find(".page-title-row .resource-scope-badge").TextContent);
        Assert.AreEqual("/instance", page.Find(".resource-owner-scope").GetAttribute("data-scope-ref"));
        var resource = context.Render<ResourceHeader>(p => p.Add(v => v.Name, "Service")
            .Add(v => v.ResourceType, "Type").Add(v => v.ScopeRef, Agentstration.Resources.ResourceScopeRef.Instance));
        Assert.AreEqual(scopeLabel, resource.Find(".resource-header-badges .resource-scope-badge").TextContent);
        page.Render(p => p.Add(v => v.ScopeRef, (Agentstration.Resources.ResourceScopeRef?)null));
        Assert.IsEmpty(page.FindAll(".resource-owner-scope"));
    }

    [TestMethod]
    public void HeaderActivationIsOptionalAndDelegatesChanges()
    {
        using var context = Context();
        var changed = false;
        var page = context.Render<PageHeader>(p => p.Add(v => v.Title, "Service"));
        Assert.IsEmpty(page.FindAll("input[role=switch]"));
        page.Render(p => p.Add(v => v.Enabled, true).Add(v => v.EnabledChanged, value => changed = !value).Add(v => v.EnabledTestId, "service-enabled"));
        Assert.AreEqual("service-enabled", page.Find("input[role=switch]").GetAttribute("data-testid"));
        page.Find("input[role=switch]").Change(false);
        Assert.IsTrue(changed);
        page.Render(p => p.Add(v => v.EnabledDisabled, true));
        Assert.IsTrue(page.Find("input[role=switch]").HasAttribute("disabled"));
        var resource = context.Render<ResourceHeader>(p => p.Add(v => v.Name, "Service").Add(v => v.ResourceType, "Type").Add(v => v.Enabled, false));
        Assert.IsTrue(resource.Find("input[role=switch]").HasAttribute("disabled"), "A header without a mutation handler must be read-only.");
    }

    [TestMethod]
    public async Task HeaderCopiesTheQualifiedIdentityAndKeepsUnqualifiedCallersCompatible()
    {
        using var context = Context();
        var module = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-ui.js");
        var copy = module.SetupVoid("copyText", "support/welcome-agent");
        copy.SetVoidResult();
        var cut = context.Render<ResourceHeader>(parameters => parameters
            .Add(value => value.Name, "Welcome agent")
            .Add(value => value.ResourceType, "Agent")
            .Add(value => value.TechnicalName, "welcome-agent")
            .Add(value => value.Namespace, "support"));
        Assert.AreEqual("support/welcome-agent", cut.Find(".resource-header-technical code").TextContent);
        StringAssert.Contains(cut.Find(".resource-header-technical .copy-value").GetAttribute("title")!, "support/welcome-agent");
        await cut.Find(".resource-header-technical .copy-value").ClickAsync(new());
        Assert.HasCount(1, copy.Invocations);
        cut.Render(parameters => parameters.Add(value => value.Namespace, (string?)null));
        Assert.AreEqual("welcome-agent", cut.Find(".resource-header-technical code").TextContent);
        var unqualifiedCopy = module.SetupVoid("copyText", "welcome-agent");
        unqualifiedCopy.SetVoidResult();
        await cut.Find(".resource-header-technical .copy-value").ClickAsync(new());
        Assert.HasCount(1, unqualifiedCopy.Invocations);
    }

    [TestMethod]
    public async Task LabelRowsValidateDuplicatesAndIncompleteValuesWithoutLosingTheDraft()
    {
        using var context = Context();
        var model = new LabelsModel { Labels = "environment=production" };
        var edit = new EditContext(model);
        var cut = context.Render<CascadingValue<EditContext>>(parameters => parameters
            .Add(value => value.Value, edit)
            .AddChildContent<LabelsEditor>(labels => labels
                .Add(value => value.Value, model.Labels)
                .Add(value => value.ValueChanged, value => model.Labels = value)
                .Add(value => value.ValueExpression, () => model.Labels)));
        await cut.Find("[data-testid='label-add']").ClickAsync(new());
        Assert.IsTrue(await cut.InvokeAsync(edit.Validate));
        Assert.AreEqual("environment=production", model.Labels);
        Assert.IsFalse(edit.IsModified());
        var keys = cut.FindAll("[data-testid='label-key']");
        keys[1].Input("environment");
        Assert.IsFalse(await cut.InvokeAsync(edit.Validate));
        Assert.AreEqual("environment=production", model.Labels, "An incomplete row must not corrupt the resource binding.");
        cut.FindAll("[data-testid='label-value']")[1].Input("staging");
        Assert.IsFalse(await cut.InvokeAsync(edit.Validate));
        Assert.HasCount(2, cut.FindAll(".label-row-error"));
        Assert.AreEqual("environment=production", model.Labels, "Duplicate keys must remain a local draft.");
        keys[1].Input("team");
        Assert.IsTrue(await cut.InvokeAsync(edit.Validate));
        StringAssert.Contains(model.Labels, "team=staging");
        await cut.FindAll(".label-remove")[1].ClickAsync(new());
        Assert.AreEqual("environment=production", model.Labels);
        Assert.IsTrue(await cut.InvokeAsync(edit.Validate));
        Assert.IsTrue(edit.IsModified());
    }

    [TestMethod]
    public async Task ImageFailureFallsBackAndANewResourceImageCanLoad()
    {
        using var context = Context();
        var cut = context.Render<ResourceIcon>(parameters => parameters
            .Add(value => value.Source, "/images/service.svg")
            .Add(value => value.DarkSource, "/images/service-dark.svg")
            .Add(value => value.IconName, "layers"));
        Assert.HasCount(2, cut.FindAll("img"));
        Assert.IsTrue(cut.FindAll("img").All(image => image.GetAttribute("alt") == string.Empty));
        await cut.Find("img").TriggerEventAsync("onerror", new Microsoft.AspNetCore.Components.Web.ErrorEventArgs());
        Assert.IsEmpty(cut.FindAll("img"));
        Assert.HasCount(1, cut.FindAll("svg"));
        cut.Render(parameters => parameters.Add(value => value.Source, "/images/other.svg"));
        Assert.HasCount(2, cut.FindAll("img"));
        cut.Render(parameters => parameters.Add(value => value.Source, "javascript:alert(1)"));
        Assert.IsEmpty(cut.FindAll("img"));
    }

    [TestMethod]
    public async Task IdentityKeepsMissingDescriptionExplicitAndCopiesOnlyTheRequestedValue()
    {
        var previousCulture = System.Globalization.CultureInfo.CurrentUICulture;
        System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        try
        {
            using var context = Context();
            var module = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-ui.js");
            var copy = module.SetupVoid("copyText", "welcome-agent");
            copy.SetVoidResult();
            var uid = Guid.Parse("12345678-1234-4567-8901-123456789012");
            var copyUid = module.SetupVoid("copyText", uid.ToString("D"));
            copyUid.SetVoidResult();
            var cut = context.Render<ResourceIdentitySummary>(parameters => parameters
                .Add(value => value.DisplayName, "Welcome agent")
                .Add(value => value.TechnicalName, "welcome-agent")
                .Add(value => value.Namespace, "default").Add(value => value.ResourceUid, uid));
            StringAssert.Contains(cut.Markup, "Not provided");
            Assert.IsEmpty(cut.FindAll(".panel-header .resource-identity-uid"));
            Assert.AreEqual(uid.ToString("D"), cut.Find(".resource-identity-facts dd:first-of-type .copyable-text").TextContent);
            CollectionAssert.AreEqual(new[] { "Resource UUID", "Display name", "Technical name", "Namespace", "Description" }, cut.FindAll(".resource-identity-facts dt").Select(field => field.TextContent).ToArray());
            await cut.FindComponents<CopyableValue>().Single(component => component.Instance.Value == "welcome-agent").Find(".copy-value").ClickAsync(new());
            Assert.HasCount(1, copy.Invocations);
            Assert.AreEqual("Copied", cut.Find("[role='status']").TextContent);
            await cut.Find(".resource-identity-uid .copy-value").ClickAsync(new());
            Assert.HasCount(1, copyUid.Invocations);
            Assert.IsEmpty(cut.FindAll(".labels-summary"), "Legacy consumers can omit labels.");
            cut.Render(parameters => parameters.Add(value => value.Labels, new Dictionary<string, string>()));
            Assert.AreEqual("No labels", cut.Find(".labels-summary").TextContent.Trim());
            cut.Render(parameters => parameters.Add(value => value.Labels, new Dictionary<string, string> { ["environment"] = "local" }));
            Assert.AreEqual("environment", cut.Find(".labels-summary strong").TextContent);
            Assert.Contains("local", cut.Find(".resource-identity-summary").TextContent);
            cut.Render(parameters => parameters.Add(value => value.ResourceUid, Guid.Empty));
            Assert.IsEmpty(cut.FindAll(".resource-identity-uid"));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = previousCulture;
        }
    }

    private static BunitContext Context()
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        return context;
    }
    private sealed class LabelsModel { public string Labels { get; set; } = string.Empty; }
}
