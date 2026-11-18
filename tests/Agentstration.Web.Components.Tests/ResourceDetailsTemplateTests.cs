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
            .Add(c => c.EnabledChanged, value => activation = value)
            .Add(c => c.ResourceIdentity, "endpoint|team|instance")
            .Add(c => c.Sections, new[] { new ResourceNavigationItem("overview", "Overview", "home") }));
        var instance = cut.FindComponent<DraftProbe>().Instance;
        await cut.Find(".resource-details-content input").InputAsync("Incomplete local draft");
        cut.Find("[role=switch]").Change(false);
        Assert.AreEqual(false, activation);
        cut.Render(p => p.Add(c => c.Name, "Updated saved service").Add(c => c.Enabled, false)
            .Add(c => c.EnabledDisabled, true));
        Assert.AreSame(instance, cut.FindComponent<DraftProbe>().Instance);
        Assert.AreEqual("Incomplete local draft", cut.Find(".resource-details-content input").GetAttribute("value"));
        Assert.IsTrue(cut.Find("[role=switch]").HasAttribute("disabled"));
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
