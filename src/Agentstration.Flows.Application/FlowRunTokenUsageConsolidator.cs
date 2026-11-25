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

        return new(FlowRunTokenUsageState.Final, direct, FlowRunTokenUsageAggregation.Combine([direct, .. childTotals]));
    }

    private static IEnumerable<string> ChildRunIds(FlowRun run) => run.Steps
        .SelectMany(step => step.ChildFlowRunIds.Concat(
            string.IsNullOrWhiteSpace(step.ChildFlowRunId) ? [] : [step.ChildFlowRunId]))
        .Distinct(StringComparer.Ordinal)!;

    private static FlowRunTokenUsageTotal AggregateSteps(IReadOnlyList<FlowStepRun> steps) =>
        FlowRunTokenUsageAggregation.Combine(steps
            .Where(step => string.Equals(step.StepType, "agent", StringComparison.OrdinalIgnoreCase)
                && step.Status is FlowStepRunStatus.Succeeded or FlowStepRunStatus.Failed or FlowStepRunStatus.Cancelled)
            .Select(step => FlowRunTokenUsageAggregation.Invocation(step.Usage?.InputTokens, step.Usage?.OutputTokens, step.Usage?.TotalTokens))
            .ToArray());
}
