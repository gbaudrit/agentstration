using System.Globalization;
using System.Text.Json;
using Agentstration.Flow;
using Agentstration.Web.Components;
using Bunit;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class FlowRunTimelineTests
{
    [TestMethod]
    public void ConsecutiveStreamingDeltasRenderAsOneSemanticTimelineEntry()
    {
        using var context = new BunitContext();
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

        Assert.AreEqual(3, rendered.FindAll(".flow-timeline-item").Count);
        Assert.AreEqual("out est simple", rendered.Find(".flow-stream-output").TextContent);
        Assert.Contains("3 delta(s)", rendered.Markup, StringComparison.Ordinal);
        Assert.Contains("Live", rendered.Markup, StringComparison.Ordinal);
    }

    [TestMethod]
    public void RawViewKeepsIndividualEventsAndBoundsTheRenderedWindow()
    {
        using var context = new BunitContext();
        var now = DateTimeOffset.UtcNow;
        var events = Enumerable.Range(1, 205)
            .Select(sequence => Event(sequence, FlowRunEventType.StepOutputDelta, "agent_1", new { delta = sequence.ToString(CultureInfo.InvariantCulture) }, now))
            .ToArray();
        var rendered = context.Render<FlowRunTimeline>(parameters => parameters.Add(value => value.Events, events));

        rendered.FindAll("[role=tab]")[1].Click();

        Assert.AreEqual(200, rendered.FindAll(".flow-raw-events li").Count);
        Assert.Contains("Showing 200 of 205", rendered.Markup, StringComparison.Ordinal);
        rendered.Find(".flow-load-events").Click();
        Assert.AreEqual(205, rendered.FindAll(".flow-raw-events li").Count);
    }

    [TestMethod]
    public void ParticipantTurnsAndTimeoutRenderAsSemanticActivity()
    {
        using var context = new BunitContext();
        var now = DateTimeOffset.UtcNow;
        FlowRunEvent[] events =
        [
            Event(1, FlowRunEventType.ParticipantTurnStarted, "researcher", new { turn = 1 }, now),
            Event(2, FlowRunEventType.ParticipantTurnCompleted, "researcher", new { turn = 1 }, now),
            Event(3, FlowRunEventType.ParticipantTurnStarted, "reviewer", new { turn = 2 }, now),
            Event(4, FlowRunEventType.FlowRunTimedOut, null, null, now)
        ];

        var rendered = context.Render<FlowRunTimeline>(parameters => parameters.Add(value => value.Events, events));

        Assert.Contains("researcher started a turn", rendered.Markup, StringComparison.Ordinal);
        Assert.Contains("researcher completed a turn", rendered.Markup, StringComparison.Ordinal);
        Assert.Contains("reviewer started a turn", rendered.Markup, StringComparison.Ordinal);
        Assert.Contains("Flow run timed out", rendered.Markup, StringComparison.Ordinal);
    }

    [TestMethod]
    public void InteractiveLifecycleRendersAsSemanticActivity()
    {
        using var context = new BunitContext();
        var now = DateTimeOffset.UtcNow;
        var rendered = context.Render<FlowRunTimeline>(parameters => parameters.Add(value => value.Events, new[]
        {
            Event(1, FlowRunEventType.InputRequested, "agent-1", new { prompt = "Continue?" }, now),
            Event(2, FlowRunEventType.InputReceived, "agent-1", null, now),
            Event(3, FlowRunEventType.FlowRunResumed, null, null, now)
        }));

        Assert.Contains("Response requested", rendered.Markup, StringComparison.Ordinal);
        Assert.Contains("Response received", rendered.Markup, StringComparison.Ordinal);
        Assert.Contains("Flow run resumed", rendered.Markup, StringComparison.Ordinal);
    }

    private static FlowRunEvent Event(long sequence, FlowRunEventType type, string? stepId, object? payload, DateTimeOffset timestamp) =>
        new(new(Guid.Parse("11111111-1111-1111-1111-111111111111")), "run-1", sequence, type, stepId, payload is null ? null : JsonSerializer.SerializeToElement(payload), timestamp);
}
