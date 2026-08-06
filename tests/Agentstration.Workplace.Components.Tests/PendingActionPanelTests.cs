using Agentstration.Work;
using Agentstration.Workplace.Components;
using Bunit;
using Microsoft.AspNetCore.Components;

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
    public void WorkplaceLayoutUsesTheConsoleDesignSystemShell()
    {
        using var context = new BunitContext();
        var rendered = context.Render<WorkplaceLayout>(parameters => parameters
            .Add(value => value.Body, builder => builder.AddContent(0, "Workplace content")));

        Assert.IsTrue(rendered.Markup.Contains("app-shell theme-light", StringComparison.Ordinal));
        Assert.IsTrue(rendered.Markup.Contains("side-nav", StringComparison.Ordinal));
        Assert.IsTrue(rendered.Markup.Contains("Workplace content", StringComparison.Ordinal));
    }
}
