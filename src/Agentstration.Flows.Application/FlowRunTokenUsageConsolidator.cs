using Agentstration.Flows.Storage.Abstractions;

namespace Agentstration.Flows.Application;

public sealed class FlowRunTokenUsageConsolidator(IFlowRepository repository)
{
    public async Task<FlowRunTokenUsage> FinalizeAsync(FlowRun run, CancellationToken cancellationToken)
    {
        var direct = AggregateSteps(run.Steps);
        var childTotals = new List<FlowRunTokenUsageTotal>();
        foreach (var childRunId in ChildRunIds(run))
        {
            var child = await repository.GetRunAsync(run.Scope, childRunId, cancellationToken);
            if (child is null
                || child.Value.ParentFlowRunId != run.Id
                || child.Value.RootFlowRunId != (run.RootFlowRunId ?? run.Id)
                || child.Value.NestingDepth != run.NestingDepth + 1)
                throw new FlowValidationException("flow_token_usage_child_invalid",
                    $"Child Flow Run '{childRunId}' does not match the execution hierarchy.");
            if (!child.Value.Status.IsTerminal() || child.Value.TokenUsage.State != FlowRunTokenUsageState.Final)
                throw new FlowValidationException("flow_token_usage_child_not_final",
                    $"Child Flow Run '{childRunId}' must have final token usage before its parent can become terminal.");
            childTotals.Add(child.Value.TokenUsage.Inclusive);
        }

        return new(FlowRunTokenUsageState.Final, direct, Combine([direct, .. childTotals]));
    }

    private static IEnumerable<string> ChildRunIds(FlowRun run) => run.Steps
        .SelectMany(step => step.ChildFlowRunIds.Concat(
            string.IsNullOrWhiteSpace(step.ChildFlowRunId) ? [] : [step.ChildFlowRunId]))
        .Distinct(StringComparer.Ordinal)!;

    private static FlowRunTokenUsageTotal AggregateSteps(IReadOnlyList<FlowStepRun> steps)
    {
        var invocations = steps
            .Where(step => string.Equals(step.StepType, "agent", StringComparison.OrdinalIgnoreCase)
                && step.Status is FlowStepRunStatus.Succeeded or FlowStepRunStatus.Failed or FlowStepRunStatus.Cancelled)
            .ToArray();
        var reported = invocations.Count(step => step.Usage is
        { InputTokens: not null } or { OutputTokens: not null } or { TotalTokens: not null });
        var complete = invocations.Count(step => step.Usage is
        { InputTokens: not null, OutputTokens: not null, TotalTokens: not null });
        return new(
            Sum(invocations.Select(step => (long?)step.Usage?.InputTokens)),
            Sum(invocations.Select(step => (long?)step.Usage?.OutputTokens)),
            Sum(invocations.Select(step => (long?)step.Usage?.TotalTokens)),
            Coverage(invocations.Length, reported, complete == invocations.Length),
            reported,
            invocations.Length);
    }

    private static FlowRunTokenUsageTotal Combine(IReadOnlyList<FlowRunTokenUsageTotal> totals)
    {
        var invocationCount = totals.Sum(total => total.InvocationCount);
        var reported = totals.Sum(total => total.ReportedInvocationCount);
        var complete = totals.Where(total => total.InvocationCount > 0)
            .All(total => total.Coverage == FlowRunTokenUsageCoverage.Complete);
        return new(
            Sum(totals.Select(total => total.InputTokens)),
            Sum(totals.Select(total => total.OutputTokens)),
            Sum(totals.Select(total => total.TotalTokens)),
            Coverage(invocationCount, reported, complete),
            reported,
            invocationCount);
    }

    private static long? Sum(IEnumerable<long?> values)
    {
        var known = values.Where(value => value is not null).Select(value => value!.Value).ToArray();
        return known.Length == 0 ? null : known.Sum();
    }

    private static FlowRunTokenUsageCoverage Coverage(int invocations, int reported, bool complete) =>
        invocations == 0 || reported == 0
            ? FlowRunTokenUsageCoverage.Unavailable
            : complete
                ? FlowRunTokenUsageCoverage.Complete
                : FlowRunTokenUsageCoverage.Partial;
}
