using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Infrastructure.Flows;
using Agentstration.Resources;

namespace Agentstration.Infrastructure.Tests;

[TestClass]
public sealed class DirectExecutionPayloadPersistenceTests
{
    [TestMethod]
    public void RevisionTicketsKeepCommittedPayloadsIsolatedAndColdOrExpiredReadsHaveNoPayload()
    {
        var clock = new Clock();
        var cache = new DirectExecutionPayloadPersistence(clock);
        var scope = ResourceScopeRef.Workspace(Guid.NewGuid());
        var original = new FlowRunEvent(scope, "direct", 1, FlowRunEventType.StepRunCompleted, "read",
            JsonSerializer.SerializeToElement(new { content = "first" }), clock.Now)
        { TransientPayloads = true };
        var committed = cache.Store(original);
        var rejectedWrite = cache.Store(original with { Payload = JsonSerializer.SerializeToElement(new { content = "uncommitted" }) });
        Assert.IsNull(committed.Payload);
        Assert.AreNotEqual(committed.TransientPayloadTicket, rejectedWrite.TransientPayloadTicket);
        Assert.AreEqual("first", cache.Restore(committed).Payload!.Value.GetProperty("content").GetString());
        Assert.IsNull(cache.Restore(committed with { ScopeRef = ResourceScopeRef.Workspace(Guid.NewGuid()) }).Payload);
        Assert.IsNull(new DirectExecutionPayloadPersistence(clock).Restore(committed).Payload);
        clock.Now += DirectExecutionPayloadPersistence.Lifetime;
        Assert.IsNull(cache.Restore(committed).Payload);
    }

    [TestMethod]
    public void CapacityFailureDoesNotEvictAnAcceptedLiveHandoff()
    {
        var clock = new Clock();
        var cache = new DirectExecutionPayloadPersistence(clock);
        var scope = ResourceScopeRef.Workspace(Guid.NewGuid());
        var original = new FlowRunEvent(scope, "direct", 1, FlowRunEventType.StepRunCompleted, "read",
            JsonSerializer.SerializeToElement(new { content = "accepted" }), clock.Now)
        { TransientPayloads = true };
        var first = cache.Store(original);
        for (var index = 1; index < 4096; index++) _ = cache.Store(original with { Sequence = index + 1 });
        var full = Assert.ThrowsExactly<FlowValidationException>(() => cache.Store(original));
        Assert.AreEqual("direct_content_capacity", full.Code);
        Assert.AreEqual("accepted", cache.Restore(first).Payload!.Value.GetProperty("content").GetString());
        clock.Now += DirectExecutionPayloadPersistence.Lifetime;
        Assert.IsNotNull(cache.Store(original with { Timestamp = clock.Now }).TransientPayloadTicket);
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
