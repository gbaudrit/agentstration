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
    [TestMethod]
    public void ProcessingDraftsShareAdmissionCapacityWithoutEvictingOrReplacingAcceptedContent()
    {
        var clock = new Clock();
        var cache = new DirectExecutionPayloadPersistence(clock);
        var scope = ResourceScopeRef.Workspace(Guid.NewGuid());
        var content = new string('x', 4 * 1024 * 1024);
        for (var index = 0; index < 15; index++) cache.StoreProcessing(scope, "draft-" + index, content, clock.Now + DirectExecutionPayloadPersistence.Lifetime);
        cache.StoreProcessing(scope, "draft-0", "accepted", clock.Now + DirectExecutionPayloadPersistence.Lifetime);
        var oversized = new string('y', 9 * 1024 * 1024);
        var full = Assert.ThrowsExactly<FlowValidationException>(() => cache.StoreProcessing(scope, "draft-0", oversized, clock.Now + DirectExecutionPayloadPersistence.Lifetime));
        Assert.AreEqual("direct_content_capacity", full.Code);
        Assert.AreEqual("accepted", cache.GetProcessing<string>(scope, "draft-0"));
        Assert.IsNull(cache.GetProcessing<string>(ResourceScopeRef.Workspace(Guid.NewGuid()), "draft-0"));
        Assert.IsNull(new DirectExecutionPayloadPersistence(clock).GetProcessing<string>(scope, "draft-0"));
        clock.Now += DirectExecutionPayloadPersistence.Lifetime;
        Assert.IsNull(cache.GetProcessing<string>(scope, "draft-0"));
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
