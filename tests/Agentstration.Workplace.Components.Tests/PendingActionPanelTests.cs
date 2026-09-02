using System.Globalization;
using Agentstration.Web.Components;
using Agentstration.Web.Components.State;
using Agentstration.Work;
using Agentstration.Workplace.Components;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Agentstration.Workplace.Components.Tests;

[TestClass]
public sealed class PendingActionPanelTests
{
    [TestMethod]
    public async Task ChoiceActionRendersInlineWithAdaptiveOptions()
    {
        using var context = CreateContext();
        PendingActionAnswer? answer = null;
        var action = new RequestChoiceAction(
            "Choose detail",
            "Select the expected report depth.",
            [new EntryFieldOption("compact", "Compact"), new EntryFieldOption("detailed", "Detailed")],
            PendingActionId.New(),
            "opaque-token");

        var rendered = context.Render<PendingActionPanel>(parameters => parameters
            .Add(value => value.Action, action)
            .Add(value => value.OnSubmit, EventCallback.Factory.Create<PendingActionAnswer>(this, value => answer = value)));

        Assert.IsTrue(rendered.Markup.Contains(Localizer(context)["ActionRequired"], StringComparison.Ordinal));
        Assert.IsTrue(rendered.Markup.Contains("Compact", StringComparison.Ordinal));
        Assert.IsTrue(rendered.Markup.Contains("Detailed", StringComparison.Ordinal));
        Assert.IsFalse(rendered.FindAll("button").Any(value => value.TextContent == Localizer(context)["Continue"]), "A simple choice must not require a second submit action.");
        await rendered.FindAll("button").Single(value => value.TextContent == "Detailed").ClickAsync(new());
        Assert.AreEqual("detailed", answer?.Values["detailLevel"].GetString());
    }

    [TestMethod]
    public async Task ConfirmationActionSubmitsStructuredAnswer()
    {
        using var context = CreateContext();
        PendingActionAnswer? answer = null;
        var action = new RequestConfirmationAction("Generate?", null, PendingActionId.New(), "opaque-token");
        var rendered = context.Render<PendingActionPanel>(parameters => parameters
            .Add(value => value.Action, action)
            .Add(value => value.OnSubmit, EventCallback.Factory.Create<PendingActionAnswer>(this, value => answer = value)));

        await rendered.FindAll("button").Single(value => value.TextContent == Localizer(context)["Confirm"]).ClickAsync(new());

        Assert.IsNotNull(answer);
        Assert.AreEqual(action.PendingActionId, answer.PendingActionId);
        Assert.IsTrue(answer.Values["confirmed"].GetBoolean());
    }

    [TestMethod]
    public async Task ConversationModeRendersInputAsAnAssistantTurn()
    {
        using var context = CreateContext();
        PendingActionAnswer? answer = null;
        var action = new RequestInputAction(
            "What should the report focus on?",
            "A short answer is enough.",
            [new EntryFieldDefinition { Name = "focus", Type = EntryFieldType.Textarea, Required = true }],
            PendingActionId.New(),
            "opaque-token");
        var rendered = context.Render<PendingActionPanel>(parameters => parameters
            .Add(value => value.Action, action)
            .Add(value => value.ConversationMode, true)
            .Add(value => value.OnSubmit, EventCallback.Factory.Create<PendingActionAnswer>(this, value => answer = value)));

        Assert.AreEqual(1, rendered.FindAll(".pending-author-avatar").Count);
        Assert.IsTrue(rendered.Markup.Contains("Agentstration", StringComparison.Ordinal));
        Assert.IsTrue(rendered.Markup.Contains(Localizer(context)["ResponseNeeded"], StringComparison.Ordinal));
        Assert.AreEqual(0, rendered.FindAll(".panel-header").Count);
        Assert.IsFalse(rendered.Markup.Contains(Localizer(context)["ActionRequired"], StringComparison.Ordinal));

        await rendered.Find("textarea").ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "Revenue trends" });
        await rendered.FindAll("button").Single(value => value.TextContent == Localizer(context)["Continue"]).ClickAsync(new());
        Assert.AreEqual("Revenue trends", answer?.Values["focus"].GetString());
    }

    [TestMethod]
    public void WorkplaceLayoutUsesTheConsoleDesignSystemShell()
    {
        using var context = CreateContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddAgentstrationWebComponents();
        var localizer = context.Services.GetRequiredService<IStringLocalizer<WorkplaceLayoutStrings>>();
        var rendered = context.Render<WorkplaceLayout>(parameters => parameters
            .Add(value => value.Body, builder => builder.AddContent(0, "Workplace content")));

        Assert.IsTrue(rendered.Markup.Contains("app-shell theme-light", StringComparison.Ordinal));
        Assert.IsTrue(rendered.Markup.Contains("side-nav", StringComparison.Ordinal));
        Assert.IsTrue(rendered.Markup.Contains("images/agentstration-workplace-lockup-dark.png", StringComparison.Ordinal));
        Assert.AreEqual(1, rendered.FindAll(".workplace-brand-lockup .brand-lockup-logo").Count);
        Assert.AreEqual(1, rendered.FindAll(".mobile-brand-logo").Count);
        Assert.IsTrue(rendered.Find(".workplace-brand-lockup .brand-lockup-logo").GetAttribute("src")?.EndsWith("agentstration-workplace-lockup-dark.png", StringComparison.Ordinal));
        Assert.IsTrue(rendered.Find(".mobile-brand-logo").GetAttribute("src")?.EndsWith("agentstration-workplace-lockup-dark.png", StringComparison.Ordinal));
        Assert.AreEqual(2, rendered.FindAll(".navigation-group").Count);
        Assert.AreEqual(localizer["Work"].Value, rendered.Find(".navigation-group h2").TextContent);
        Assert.AreEqual(3, rendered.FindAll(".side-nav .nav-icon").Count);
        Assert.AreEqual(localizer["Home"].Value, rendered.Find(".nav-label").TextContent);
        Assert.AreEqual(localizer["Activity"].Value, rendered.FindAll(".nav-label")[1].TextContent);
        Assert.AreEqual(1, rendered.FindAll(".mobile-profile").Count);
        Assert.AreEqual(0, rendered.FindAll(".side-nav-notifications").Count);
        Assert.IsTrue(rendered.Markup.Contains("Workplace content", StringComparison.Ordinal));
    }

    [TestMethod]
    public void WorkplaceLayoutDisablesWorkspaceNavigationBeforeInitialization()
    {
        using var context = CreateContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddAgentstrationWebComponents();

        var rendered = context.Render<WorkplaceLayout>(parameters => parameters
            .Add(value => value.Body, builder => builder.AddContent(0, "Workplace content")));

        Assert.AreEqual(1, rendered.FindAll(".side-nav a.active").Count);
        var disabled = rendered.FindAll(".side-nav a.side-nav-disabled");
        Assert.AreEqual(1, disabled.Count);
        Assert.IsTrue(disabled.All(value => value.GetAttribute("aria-disabled") == "true"));
        Assert.IsTrue(disabled.All(value => value.GetAttribute("tabindex") == "-1"));
        Assert.IsTrue(disabled.All(value => !value.HasAttribute("href")));
    }

    [TestMethod]
    [DoNotParallelize]
    public void WorkplaceLayoutUsesTheSelectedCulture()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            using var context = CreateContext();
            context.JSInterop.Mode = JSRuntimeMode.Loose;
            context.Services.AddAgentstrationWebComponents();

            var rendered = context.Render<WorkplaceLayout>(parameters => parameters
                .Add(value => value.Body, builder => builder.AddContent(0, "Contenu")));

            StringAssert.Contains(rendered.Markup, "Accueil");
            StringAssert.Contains(rendered.Markup, "Activité");
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [TestMethod]
    public void WorkplaceLayoutUsesTheWorkspaceDisplayNameOutsideUrls()
    {
        using var context = CreateContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddAgentstrationWebComponents();
        var localizer = context.Services.GetRequiredService<IStringLocalizer<WorkplaceLayoutStrings>>();
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("w/personal");
        context.Services.GetRequiredService<WorkplaceContextState>().SetWorkspace("personal", "Personal Space", "acme", "ACME Europe");

        var rendered = context.Render<WorkplaceLayout>(parameters => parameters
            .Add(value => value.Body, builder => builder.AddContent(0, "Workplace content")));

        var workplaceLabel = localizer["NamedWorkplace", "Personal Space"].Value;
        Assert.AreEqual($"ACME Europe/{workplaceLabel}", rendered.Find(".breadcrumb").TextContent);
        Assert.AreEqual(workplaceLabel, rendered.Find(".breadcrumb a").TextContent);
        Assert.AreEqual("Personal Space", rendered.Find(".sidebar-footer strong").TextContent);
        Assert.AreEqual("ACME Europe", rendered.Find(".sidebar-footer small").TextContent);
        Assert.AreEqual("/w/personal", rendered.Find(".breadcrumb a").GetAttribute("href"));
        Assert.AreEqual(0, rendered.FindAll(".environment-chip").Count);
    }

    [TestMethod]
    public void WorkplaceLayoutShowsRecentConversationsInDesktopNavigation()
    {
        using var context = CreateContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddAgentstrationWebComponents();
        context.Services.AddScoped<IRecentConversationNavigationProvider, StubRecentConversationNavigationProvider>();
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("w/personal/d/home");

        var rendered = context.Render<WorkplaceLayout>(parameters => parameters
            .Add(value => value.Body, builder => builder.AddContent(0, "Workplace content")));

        rendered.WaitForAssertion(() =>
        {
            var link = rendered.Find(".recent-conversation-navigation a");
            Assert.AreEqual("Quarterly planning", link.TextContent);
            Assert.AreEqual("/w/personal/d/home/conversations/11111111-1111-1111-1111-111111111111", link.GetAttribute("href"));
        });
    }

    private sealed class StubRecentConversationNavigationProvider : IRecentConversationNavigationProvider
    {
        public Task<IReadOnlyList<RecentConversationNavigationItem>> ListAsync(
            string workspaceName,
            string? dashboardName,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RecentConversationNavigationItem>>(
                [new("Quarterly planning", "/w/personal/d/home/conversations/11111111-1111-1111-1111-111111111111")]);
    }

    private static BunitContext CreateContext()
    {
        var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        return context;
    }

    private static IStringLocalizer<WorkplaceLayoutStrings> Localizer(BunitContext context) =>
        context.Services.GetRequiredService<IStringLocalizer<WorkplaceLayoutStrings>>();
}
