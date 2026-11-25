namespace Agentstration.Flows;

/// <summary>Canonical nullable token totals; provider totals are never inferred from other counters.</summary>
public static class FlowRunTokenUsageAggregation
{
    public static FlowRunTokenUsageTotal Invocation(long? inputTokens, long? outputTokens, long? totalTokens)
    {
        var reported = inputTokens is not null || outputTokens is not null || totalTokens is not null;
        return new(inputTokens, outputTokens, totalTokens,
            Coverage(1, reported ? 1 : 0, inputTokens is not null && outputTokens is not null && totalTokens is not null),
            reported ? 1 : 0, 1);
    }

    public static FlowRunTokenUsageTotal Combine(IReadOnlyList<FlowRunTokenUsageTotal> totals)
    {
        var invocations = totals.Sum(total => total.InvocationCount);
        var reported = totals.Sum(total => total.ReportedInvocationCount);
        var complete = totals.Where(total => total.InvocationCount > 0)
            .All(total => total.Coverage == FlowRunTokenUsageCoverage.Complete);
        return new(Sum(totals.Select(total => total.InputTokens)), Sum(totals.Select(total => total.OutputTokens)),
            Sum(totals.Select(total => total.TotalTokens)), Coverage(invocations, reported, complete), reported, invocations);
    }

    /// <summary>Combines non-overlapping reports within one invocation, retaining its single invocation identity.</summary>
    public static FlowRunTokenUsageTotal MergeInvocationReports(IReadOnlyList<FlowRunTokenUsageTotal> reports)
    {
        var combined = Combine(reports);
        return combined with { InvocationCount = 1, ReportedInvocationCount = combined.ReportedInvocationCount > 0 ? 1 : 0 };
    }

    private static long? Sum(IEnumerable<long?> values)
    {
        var known = values.Where(value => value is not null).Select(value => value!.Value).ToArray();
        return known.Length == 0 ? null : known.Sum();
    }

    private static FlowRunTokenUsageCoverage Coverage(int invocations, int reported, bool complete) =>
        invocations == 0 || reported == 0 ? FlowRunTokenUsageCoverage.Unavailable
            : complete ? FlowRunTokenUsageCoverage.Complete : FlowRunTokenUsageCoverage.Partial;
}
