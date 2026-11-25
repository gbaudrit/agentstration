using Agentstration.Flows;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class FlowRunTokenUsageAggregationTests
{
    [TestMethod]
    public void MeasuredZeroAndMissingReportsKeepDifferentCoverageAndNullableCounters()
    {
        var missing = FlowRunTokenUsageAggregation.Invocation(null, null, null);
        Assert.IsNull(missing.InputTokens); Assert.IsNull(missing.OutputTokens); Assert.IsNull(missing.TotalTokens);
        Assert.AreEqual(FlowRunTokenUsageCoverage.Unavailable, missing.Coverage);
        var mixed = FlowRunTokenUsageAggregation.Combine([FlowRunTokenUsageAggregation.Invocation(0, 0, 0), missing]);
        Assert.AreEqual(0L, mixed.InputTokens); Assert.AreEqual(0L, mixed.OutputTokens); Assert.AreEqual(0L, mixed.TotalTokens);
        Assert.AreEqual(FlowRunTokenUsageCoverage.Partial, mixed.Coverage);
        Assert.AreEqual(2, mixed.InvocationCount); Assert.AreEqual(1, mixed.ReportedInvocationCount);
    }

    [TestMethod]
    public void ProviderTotalIsIndependentFromInputAndOutput()
    {
        var partial = FlowRunTokenUsageAggregation.Invocation(20, 5, null);
        Assert.IsNull(partial.TotalTokens); Assert.AreEqual(FlowRunTokenUsageCoverage.Partial, partial.Coverage);
        var providerOnly = FlowRunTokenUsageAggregation.Invocation(null, null, 99);
        Assert.IsNull(providerOnly.InputTokens); Assert.IsNull(providerOnly.OutputTokens);
        Assert.AreEqual(99L, providerOnly.TotalTokens); Assert.AreEqual(FlowRunTokenUsageCoverage.Partial, providerOnly.Coverage);
    }

    [TestMethod]
    public void NonOverlappingTurnReportsRetainOneNativeInvocationWhenCombinedWithAnotherExecution()
    {
        var native = FlowRunTokenUsageAggregation.MergeInvocationReports([
            FlowRunTokenUsageAggregation.Invocation(10, 3, null), FlowRunTokenUsageAggregation.Invocation(20, 5, null)]);
        Assert.AreEqual(30L, native.InputTokens); Assert.AreEqual(8L, native.OutputTokens); Assert.IsNull(native.TotalTokens);
        Assert.AreEqual(1, native.InvocationCount); Assert.AreEqual(1, native.ReportedInvocationCount);
        var total = FlowRunTokenUsageAggregation.Combine([native, FlowRunTokenUsageAggregation.Invocation(50, 10, 60)]);
        Assert.AreEqual(80L, total.InputTokens); Assert.AreEqual(18L, total.OutputTokens); Assert.AreEqual(60L, total.TotalTokens);
        Assert.AreEqual(2, total.InvocationCount); Assert.AreEqual(FlowRunTokenUsageCoverage.Partial, total.Coverage);
    }
}
