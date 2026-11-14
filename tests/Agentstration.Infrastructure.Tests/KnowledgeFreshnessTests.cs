using Agentstration.Knowledge;
using Agentstration.Knowledge.Contracts;

namespace Agentstration.Infrastructure.Tests;

[TestClass]
public sealed class KnowledgeFreshnessTests
{
    [TestMethod]
    public void MaximumSupportedAgeDoesNotOverflowDateArithmetic()
    {
        var binding = new KnowledgeDataSourceBinding
        {
            Name = "input", DataSource = new("source"), MaximumAge = TimeSpan.MaxValue
        };
        Assert.IsTrue(KnowledgeFreshness.IsFresh(binding, DateTimeOffset.MinValue, new Clock()));
    }

    [TestMethod]
    [DataRow(false, -1, 86400, true, false)]
    [DataRow(false, 0, 86400, true, false)]
    [DataRow(false, 60, 60, true, false)]
    [DataRow(false, 60, 61, false, false)]
    [DataRow(true, -1, 0, true, true)]
    [DataRow(true, 0, 0, true, true)]
    [DataRow(true, 60, 60, true, false)]
    [DataRow(true, 60, 61, false, true)]
    [DataRow(false, 60, -1, false, false)]
    [DataRow(true, 60, -1, false, true)]
    public void FreshnessUsesSuccessfulCompletionAndIncludesTheExactBoundary(bool refresh, int maximumSeconds,
        int elapsedSeconds, bool fresh, bool acquire)
    {
        var clock = new Clock();
        var binding = new KnowledgeDataSourceBinding
        {
            Name = "input",
            DataSource = new("source"),
            RefreshIfNecessary = refresh,
            MaximumAge = maximumSeconds < 0 ? null : TimeSpan.FromSeconds(maximumSeconds)
        };
        DateTimeOffset? completed = elapsedSeconds < 0 ? null : clock.GetUtcNow().AddSeconds(-elapsedSeconds);
        Assert.AreEqual(fresh, KnowledgeFreshness.IsFresh(binding, completed, clock));
        Assert.AreEqual(acquire, KnowledgeFreshness.RequiresRefresh(binding, completed, clock));
        clock.Advance(TimeSpan.FromTicks(1));
        if (elapsedSeconds == 60 && maximumSeconds == 60)
            Assert.IsFalse(KnowledgeFreshness.IsFresh(binding, completed, clock));
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan elapsed) => now += elapsed;
    }
}
