using System.Collections.Concurrent;
using System.Text.Json;
using Agentstration.Flow;
using Agentstration.Resources;
using Agentstration.Web.Components;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class FlowRunEventBufferTests
{
    [TestMethod]
    public void ConcurrentUpdatesPublishStableDeduplicatedSnapshots()
    {
        var buffer = new FlowRunEventBuffer();
        var failures = new ConcurrentQueue<Exception>();

        Parallel.For(1, 1_001, sequence =>
        {
            try
            {
                buffer.TryAdd(Event(sequence));
                buffer.TryAdd(Event(sequence));
                _ = buffer.Snapshot().Count(item => item.Type == FlowRunEventType.ParticipantTurnStarted);
            }
            catch (Exception exception)
            {
                failures.Enqueue(exception);
            }
        });

        Assert.IsEmpty(failures);
        Assert.AreEqual(1_000, buffer.Snapshot().Count);
        Assert.AreEqual(1_000, buffer.LastSequence);
    }

    [TestMethod]
    public void SnapshotDoesNotChangeWhenMoreEventsArrive()
    {
        var buffer = new FlowRunEventBuffer();
        buffer.TryAdd(Event(1));
        var snapshot = buffer.Snapshot();

        buffer.TryAdd(Event(2));

        Assert.HasCount(1, snapshot);
        Assert.HasCount(2, buffer.Snapshot());
    }

    private static FlowRunEvent Event(long sequence) => new(
        new WorkspaceId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
        "run-1",
        sequence,
        FlowRunEventType.ParticipantTurnStarted,
        "participant",
        JsonSerializer.SerializeToElement(new { turn = sequence }),
        DateTimeOffset.UtcNow);
}
