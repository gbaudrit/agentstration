using Agentstration.Web.Components;
using Agentstration.Work;
using Agentstration.Workplace.Components;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using System.Globalization;

namespace Agentstration.Workplace.Components.Tests;

[TestClass]
public sealed class PendingActionPanelTests
{
    [TestMethod]
    public async Task ChoiceActionRendersInlineWithAdaptiveOptions()
    {
        using var context = new BunitContext();
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

        Assert.IsTrue(rendered.Markup.Contains("Action required", StringComparison.Ordinal));
        Assert.IsTrue(rendered.Markup.Contains("Compact", StringComparison.Ordinal));
        Assert.IsTrue(rendered.Markup.Contains("Detailed", StringComparison.Ordinal));
        Assert.IsFalse(rendered.FindAll("button").Any(value => value.TextContent == "Continue"), "A simple choice must not require a second submit action.");
        await rendered.FindAll("button").Single(value => value.TextContent == "Detailed").ClickAsync(new());
        Assert.AreEqual("detailed", answer?.Values["detailLevel"].GetString());
    }

    [TestMethod]
    public async Task ConfirmationActionSubmitsStructuredAnswer()
    {
        using var context = new BunitContext();
        PendingActionAnswer? answer = null;
        var action = new RequestConfirmationAction("Generate?", null, PendingActionId.New(), "opaque-token");
        var rendered = context.Render<PendingActionPanel>(parameters => parameters
            .Add(value => value.Action, action)
            .Add(value => value.OnSubmit, EventCallback.Factory.Create<PendingActionAnswer>(this, value => answer = value)));

        await rendered.FindAll("button").Single(value => value.TextContent == "Confirm").ClickAsync(new());

        Assert.IsNotNull(answer);
        Assert.AreEqual(action.PendingActionId, answer.PendingActionId);
        Assert.IsTrue(answer.Values["confirmed"].GetBoolean());
    }

    [TestMethod]
    public async Task ConversationModeRendersInputAsAnAssistantTurn()
    {
        using var context = new BunitContext();
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
        Assert.IsTrue(rendered.Markup.Contains("Response needed", StringComparison.Ordinal));
        Assert.AreEqual(0, rendered.FindAll(".panel-header").Count);
        Assert.IsFalse(rendered.Markup.Contains("Action required", StringComparison.Ordinal));

        await rendered.Find("textarea").ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "Revenue trends" });
        await rendered.FindAll("button").Single(value => value.TextContent == "Continue").ClickAsync(new());
        Assert.AreEqual("Revenue trends", answer?.Values["focus"].GetString());
    }

    [TestMethod]
    public void WorkplaceLayoutUsesTheConsoleDesignSystemShell()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddAgentstrationWebComponents();
        var rendered = context.Render<WorkplaceLayout>(parameters => parameters
            .Add(value => value.Body, builder => builder.AddContent(0, "Workplace content")));

        Assert.IsTrue(rendered.Markup.Contains("app-shell theme-light", StringComparison.Ordinal));
        Assert.IsTrue(rendered.Markup.Contains("side-nav", StringComparison.Ordinal));
        Assert.IsTrue(rendered.Markup.Contains("images/agentstration-lockup.png", StringComparison.Ordinal));
        Assert.AreEqual(1, rendered.FindAll(".brand-mark-compact").Count);
        Assert.IsTrue(rendered.Markup.Contains("Workplace content", StringComparison.Ordinal));
    }

    [TestMethod]
    public void WorkplaceLayoutDisablesWorkspaceNavigationBeforeInitialization()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddAgentstrationWebComponents();

        var rendered = context.Render<WorkplaceLayout>(parameters => parameters
            .Add(value => value.Body, builder => builder.AddContent(0, "Workplace content")));

        Assert.AreEqual(1, rendered.FindAll(".side-nav a.active").Count);
        var disabled = rendered.FindAll(".side-nav a.side-nav-disabled");
        Assert.AreEqual(2, disabled.Count);
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
            using var context = new BunitContext();
            context.JSInterop.Mode = JSRuntimeMode.Loose;
            context.Services.AddAgentstrationWebComponents();

            var rendered = context.Render<WorkplaceLayout>(parameters => parameters
                .Add(value => value.Body, builder => builder.AddContent(0, "Contenu")));

            StringAssert.Contains(rendered.Markup, "Accueil");
            StringAssert.Contains(rendered.Markup, "Tâches");
            StringAssert.Contains(rendered.Markup, "Mode local");
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }
}
