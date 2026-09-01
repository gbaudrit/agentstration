using System.Globalization;
using System.Text.Json;
using Agentstration.Flow;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class FlowRunTimelineTests
{
    [TestMethod]
    public void ConsecutiveStreamingDeltasRenderAsOneSemanticTimelineEntry()
    {
        using var context = CreateContext();
        var strings = context.Services.GetRequiredService<IStringLocalizer<FlowRunDetailsStrings>>();
        var now = DateTimeOffset.UtcNow;
        FlowRunEvent[] events =
        [
            Event(1, FlowRunEventType.FlowRunStarted, null, null, now),
            Event(2, FlowRunEventType.StepOutputDelta, "agent_1", new { delta = "out " }, now),
            Event(3, FlowRunEventType.StepOutputDelta, "agent_1", new { delta = "est " }, now),
            Event(4, FlowRunEventType.StepOutputDelta, "agent_1", new { delta = "simple" }, now),
            Event(5, FlowRunEventType.StepRunCompleted, "agent_1", new { transition = "output" }, now)
        ];

        var rendered = context.Render<FlowRunTimeline>(parameters => parameters
            .Add(value => value.Events, events)
            .Add(value => value.IsLive, true));

        rendered.FindAll("[role=tab]").Single(tab => tab.TextContent.Trim() == strings["Activity"].Value).Click();

        Assert.AreEqual(3, rendered.FindAll(".flow-timeline-item").Count);
        Assert.AreEqual("out est simple", rendered.Find(".flow-stream-output").TextContent);
        Assert.Contains(strings["DeltaCount.Many", 3].Value, rendered.Markup, StringComparison.Ordinal);
        Assert.Contains(strings["Live"].Value, rendered.Markup, StringComparison.Ordinal);
    }

    [TestMethod]
    public void RawViewKeepsIndividualEventsAndBoundsTheRenderedWindow()
    {
        using var context = CreateContext();
        var strings = context.Services.GetRequiredService<IStringLocalizer<FlowRunDetailsStrings>>();
        var now = DateTimeOffset.UtcNow;
        var events = Enumerable.Range(1, 205)
            .Select(sequence => Event(sequence, FlowRunEventType.StepOutputDelta, "agent_1", new { delta = sequence.ToString(CultureInfo.InvariantCulture) }, now))
            .ToArray();
        var rendered = context.Render<FlowRunTimeline>(parameters => parameters.Add(value => value.Events, events));

        rendered.FindAll("[role=tab]").Single(tab => tab.TextContent.Trim() == strings["RawEvents"].Value).Click();

        Assert.AreEqual(200, rendered.FindAll(".flow-raw-events li").Count);
        Assert.Contains(strings["ShowingEvents", 200, 205].Value, rendered.Markup, StringComparison.Ordinal);
        rendered.Find(".flow-load-events").Click();
        Assert.AreEqual(205, rendered.FindAll(".flow-raw-events li").Count);
    }

    [TestMethod]
    public void ParticipantTurnsAndTimeoutRenderAsSemanticActivity()
    {
        using var context = CreateContext();
        var strings = context.Services.GetRequiredService<IStringLocalizer<FlowRunDetailsStrings>>();
        var now = DateTimeOffset.UtcNow;
        FlowRunEvent[] events =
        [
            Event(1, FlowRunEventType.ParticipantTurnStarted, "researcher", new { turn = 1 }, now),
            Event(2, FlowRunEventType.StepOutputDelta, "researcher", new { content = "Investigated " }, now),
            Event(3, FlowRunEventType.StepOutputDelta, "researcher", new { content = "the issue." }, now),
            Event(4, FlowRunEventType.ParticipantTurnCompleted, "researcher", new { turn = 1 }, now),
            Event(5, FlowRunEventType.ParticipantHandoff, "researcher", new { from = "researcher", to = "reviewer" }, now),
            Event(6, FlowRunEventType.ParticipantTurnStarted, "reviewer", new { turn = 2 }, now),
            Event(7, FlowRunEventType.FlowRunTimedOut, null, null, now)
        ];

        var rendered = context.Render<FlowRunTimeline>(parameters => parameters.Add(value => value.Events, events));

        rendered.FindAll("[role=tab]").Single(tab => tab.TextContent.Trim() == strings["Activity"].Value).Click();

        Assert.Contains(strings["ParticipantTurnCompleted", "researcher", "1"].Value, rendered.Markup, StringComparison.Ordinal);
        Assert.AreEqual("Investigated the issue.", rendered.Find(".flow-turn-message pre").TextContent);
        Assert.Contains(strings["DeltaCount.Many", 2].Value, rendered.Markup, StringComparison.Ordinal);
        Assert.Contains(strings["Event.ParticipantHandoff", "researcher", "reviewer"].Value, rendered.Markup, StringComparison.Ordinal);
        Assert.AreEqual("researcher→reviewer", rendered.Find(".flow-handoff-route").TextContent.Replace(" ", string.Empty, StringComparison.Ordinal));
        Assert.IsTrue(rendered.FindAll(".flow-timeline-handoff").Any());
        Assert.Contains(strings["ParticipantTurnStarted", "reviewer", "2"].Value, rendered.Markup, StringComparison.Ordinal);
        Assert.Contains(strings["Event.FlowRunTimedOut", strings["Step"].Value].Value, rendered.Markup, StringComparison.Ordinal);
    }

    [TestMethod]
    public void InterleavedParticipantDeltasStayAttachedToTheirTurn()
    {
        using var context = CreateContext();
        var strings = context.Services.GetRequiredService<IStringLocalizer<FlowRunDetailsStrings>>();
        var now = DateTimeOffset.UtcNow;
        var rendered = context.Render<FlowRunTimeline>(parameters => parameters.Add(value => value.Events, new[]
        {
            Event(1, FlowRunEventType.ParticipantTurnStarted, "researcher", new { turn = 1 }, now),
            Event(2, FlowRunEventType.ParticipantTurnStarted, "reviewer", new { turn = 2 }, now),
            Event(3, FlowRunEventType.StepOutputDelta, "researcher", new { content = "research" }, now),
            Event(4, FlowRunEventType.StepOutputDelta, "reviewer", new { content = "review" }, now),
            Event(5, FlowRunEventType.ParticipantTurnCompleted, "reviewer", new { turn = 2 }, now),
            Event(6, FlowRunEventType.ParticipantTurnCompleted, "researcher", new { turn = 1 }, now)
        }));

        rendered.FindAll("[role=tab]").Single(tab => tab.TextContent.Trim() == strings["Activity"].Value).Click();

        var messages = rendered.FindAll(".flow-turn-message pre");
        Assert.HasCount(2, messages);
        Assert.AreEqual("research", messages[0].TextContent);
        Assert.AreEqual("review", messages[1].TextContent);
        Assert.IsFalse(rendered.FindAll(".flow-stream-output").Any());
    }

    [TestMethod]
    public void EmptyInternalTerminationTurnIsRemovedWhenParticipantCompletes()
    {
        using var context = CreateContext();
        var strings = context.Services.GetRequiredService<IStringLocalizer<FlowRunDetailsStrings>>();
        var now = DateTimeOffset.UtcNow;
        var rendered = context.Render<FlowRunTimeline>(parameters => parameters.Add(value => value.Events, new[]
        {
            Event(1, FlowRunEventType.ParticipantTurnStarted, "reviewer", new { turn = 8 }, now),
            Event(2, FlowRunEventType.StepRunCompleted, "reviewer", new { turns = 3 }, now),
            Event(3, FlowRunEventType.FlowRunCompleted, null, null, now)
        }));

        rendered.FindAll("[role=tab]").Single(tab => tab.TextContent.Trim() == strings["Activity"].Value).Click();

        Assert.IsFalse(rendered.Markup.Contains("turn 8", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(rendered.Markup.Contains("tour 8", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("reviewer", rendered.Markup, StringComparison.Ordinal);
    }

    [TestMethod]
    public void InteractiveLifecycleRendersAsSemanticActivity()
    {
        using var context = CreateContext();
        var strings = context.Services.GetRequiredService<IStringLocalizer<FlowRunDetailsStrings>>();
        var now = DateTimeOffset.UtcNow;
        var rendered = context.Render<FlowRunTimeline>(parameters => parameters.Add(value => value.Events, new[]
        {
            Event(1, FlowRunEventType.InputRequested, "agent-1", new { prompt = "Continue?" }, now),
            Event(2, FlowRunEventType.InputReceived, "agent-1", null, now),
            Event(3, FlowRunEventType.FlowRunResumed, null, null, now)
        }));

        rendered.FindAll("[role=tab]").Single(tab => tab.TextContent.Trim() == strings["Activity"].Value).Click();

        Assert.Contains(strings["Event.InputRequested", "agent-1"].Value, rendered.Markup, StringComparison.Ordinal);
        Assert.Contains(strings["Event.InputReceived", "agent-1"].Value, rendered.Markup, StringComparison.Ordinal);
        Assert.Contains(strings["Event.FlowRunResumed", strings["Step"].Value].Value, rendered.Markup, StringComparison.Ordinal);
    }

    [TestMethod]
    public void HistoricalHandoffRunInfersTransfersFromParticipantChanges()
    {
        using var context = CreateContext();
        var strings = context.Services.GetRequiredService<IStringLocalizer<FlowRunDetailsStrings>>();
        var now = DateTimeOffset.UtcNow;
        var rendered = context.Render<FlowRunTimeline>(parameters => parameters
            .Add(value => value.Events, new[]
            {
                Event(1, FlowRunEventType.ParticipantTurnStarted, "researcher", new { turn = 1 }, now),
                Event(2, FlowRunEventType.ParticipantTurnStarted, "researcher", new { turn = 2 }, now),
                Event(3, FlowRunEventType.ParticipantTurnStarted, "reviewer", new { turn = 3 }, now)
            })
            .Add(value => value.InferParticipantHandoffs, true));

        rendered.FindAll("[role=tab]").Single(tab => tab.TextContent.Trim() == strings["Activity"].Value).Click();

        Assert.HasCount(1, rendered.FindAll(".flow-handoff-route"));
        Assert.Contains(strings["Event.ParticipantHandoff", "researcher", "reviewer"].Value, rendered.Markup, StringComparison.Ordinal);

        rendered.FindAll("[role=tab]").Single(tab => tab.TextContent.Trim() == strings["Handoffs"].Value).Click();

        Assert.HasCount(1, rendered.FindAll(".flow-handoff-list li"));
        Assert.AreEqual("researcher→reviewer", rendered.Find(".flow-handoff-participants").TextContent.Replace(" ", string.Empty, StringComparison.Ordinal));
        Assert.Contains(strings["HandoffCount.One", 1].Value, rendered.Markup, StringComparison.Ordinal);
    }

    [TestMethod]
    public void SummaryIsTheDefaultViewAndShowsRunProgress()
    {
        using var context = CreateContext();
        var strings = context.Services.GetRequiredService<IStringLocalizer<FlowRunDetailsStrings>>();
        var now = DateTimeOffset.UtcNow;
        var rendered = context.Render<FlowRunTimeline>(parameters => parameters.Add(value => value.Events, new[]
        {
            Event(1, FlowRunEventType.FlowRunStarted, null, null, now),
            Event(2, FlowRunEventType.ParticipantTurnStarted, "researcher", new { turn = 1 }, now),
            Event(3, FlowRunEventType.ParticipantTurnCompleted, "researcher", new { turn = 1 }, now.AddSeconds(1)),
            Event(4, FlowRunEventType.ParticipantHandoff, "researcher", new { from = "researcher", to = "reviewer" }, now.AddSeconds(1)),
            Event(5, FlowRunEventType.StepRunCompleted, "reviewer", null, now.AddSeconds(2))
        }));

        var summaryTab = rendered.FindAll("[role=tab]").Single(tab => tab.TextContent.Trim() == strings["Summary"].Value);
        Assert.AreEqual("true", summaryTab.GetAttribute("aria-selected"));
        Assert.HasCount(5, rendered.FindAll(".flow-summary-metrics>div"));
        Assert.AreEqual("researcher→reviewer", rendered.Find(".flow-participant-path ol").TextContent.Replace(" ", string.Empty, StringComparison.Ordinal));
        Assert.Contains("2 s", rendered.Markup, StringComparison.Ordinal);
    }

    [TestMethod]
    public void ExternalViewRendersActivityWithoutNestedNavigation()
    {
        using var context = CreateContext();
        var now = DateTimeOffset.UtcNow;
        var rendered = context.Render<FlowRunTimeline>(parameters => parameters
            .Add(value => value.Events, new[]
            {
                Event(1, FlowRunEventType.ParticipantTurnStarted, "researcher", new { turn = 1 }, now),
                Event(2, FlowRunEventType.ParticipantTurnCompleted, "researcher", new { turn = 1 }, now.AddSeconds(1))
            })
            .Add(value => value.View, FlowRunTimeline.TimelineView.Activity)
            .Add(value => value.ShowHeader, false)
            .Add(value => value.ShowNavigation, false));

        Assert.IsEmpty(rendered.FindAll("[role=tab]"));
        Assert.IsEmpty(rendered.FindAll(".flow-timeline-header"));
        Assert.HasCount(1, rendered.FindAll(".flow-timeline-item"));
    }

    private static BunitContext CreateContext()
    {
        var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        return context;
    }

    private static FlowRunEvent Event(long sequence, FlowRunEventType type, string? stepId, object? payload, DateTimeOffset timestamp) =>
        new(new(Guid.Parse("11111111-1111-1111-1111-111111111111")), "run-1", sequence, type, stepId, payload is null ? null : JsonSerializer.SerializeToElement(payload), timestamp);
}
