using Agentstration.Flows;

namespace Agentstration.Work;

public sealed record WorkUsageSummaryQuery(IReadOnlyList<WorkPlanId> PlanIds, WorkTaskId? TaskId = null,
    bool IncludeDescendantTasks = false, WorkUsageFlowBoundary FlowBoundary = WorkUsageFlowBoundary.Inclusive,
    WorkUsageReadSource Source = WorkUsageReadSource.Local, WorkParticipantId? ParticipantId = null);
public sealed record WorkUsageSummary(WorkUsageTotals Canonical, WorkUsageTotals Measured, WorkUsageTotals Estimated, WorkUsageTotals Unknown,
    int OriginalRecords, int Corrections, int UnknownInvocationCounts, int LegacyRecords, IReadOnlyList<WorkPlanId> PlanIds);

public static class WorkUsageAccounting
{
    public static bool SameIdentity(WorkUsageRecord left, WorkUsageRecord right) => left.Totals.Direct.InvocationCount == right.Totals.Direct.InvocationCount
        && left.Totals.Inclusive.InvocationCount == right.Totals.Inclusive.InvocationCount && WorkUsageFormat.Digest(left with
    {
        Id = right.Id,
        Revision = right.Revision,
        PreviousId = right.PreviousId,
        PreviousDigest = right.PreviousDigest,
        Correction = right.Correction,
        RecordedAt = right.RecordedAt,
        Measurement = right.Measurement,
        Totals = right.Totals
    }) == WorkUsageFormat.Digest(right);

    public static Result<WorkUsageSummary> Summarize(IReadOnlyList<WorkUsageRecord> records, WorkUsageSummaryQuery query, int legacyRecords = 0)
    {
        if (query is null || query.PlanIds is null || query.PlanIds.Count > 100 || query.PlanIds.Any(id => id.Value == Guid.Empty)
            || query.ParticipantId?.Value == Guid.Empty || query.TaskId?.Value == Guid.Empty || !Enum.IsDefined(query.FlowBoundary) || !Enum.IsDefined(query.Source)) return Failure("usage_query_invalid");
        if (records.Count > WorkUsageFormat.MaximumSummaryRecords) return Failure("usage_summary_limit");
        if (records.Any(record => !WorkUsageFormat.Valid(record)) || records.Select(record => record.Id).Distinct().Count() != records.Count)
            return Failure("usage_records_invalid");
        var latest = new List<WorkUsageRecord>();
        var corrections = 0;
        foreach (var group in records.GroupBy(record => record.RootId))
        {
            var chain = group.OrderBy(record => record.Revision).ToArray();
            if (chain[0].Revision != 1) return Failure("usage_lineage_incomplete");
            for (var index = 1; index < chain.Length; index++)
                if (chain[index].Revision != index + 1 || chain[index].PreviousId != chain[index - 1].Id
                    || chain[index].PreviousDigest != WorkUsageFormat.Digest(chain[index - 1]) || !SameIdentity(chain[index - 1], chain[index]))
                    return Failure("usage_lineage_conflict");
            var current = chain[^1];
            if (query.ParticipantId is { } participant && current.ParticipantId != participant
                || query.PlanIds.Count > 0 && !query.PlanIds.Contains(current.PlanId)
                || query.TaskId is { } task && current.TaskId != task && (!query.IncludeDescendantTasks || !current.AncestorTaskIds.Contains(task))) continue;
            latest.Add(current); corrections += chain.Length - 1;
        }
        // The same actual invocation or functional Flow root must never be charged under multiple root identities.
        if (latest.GroupBy(value => (value.ParticipantId, value.Source.AttemptId, value.Source.InvocationId, value.Source.AssignmentAttemptId))
            .Any(group => group.Count() > 1)) return Failure("usage_execution_duplicate");
        try
        {
            return Result<WorkUsageSummary>.Success(new(Canonical(), Totals(WorkUsageMeasurement.Measured), Totals(WorkUsageMeasurement.Estimated), Totals(WorkUsageMeasurement.Unknown),
                latest.Count, corrections, latest.Count(value => !value.Source.InvocationCountKnown), legacyRecords,
                latest.Select(value => value.PlanId).Distinct().OrderBy(value => value.Value).ToArray()));
        }
        catch (OverflowException) { return Failure("usage_summary_limit"); }

        WorkUsageTotals Canonical()
        {
            var total = FlowRunTokenUsageAggregation.Combine(latest.Select(value =>
            {
                var selected = query.FlowBoundary == WorkUsageFlowBoundary.Direct ? value.Totals.Direct : value.Totals.Inclusive;
                return value.Measurement == WorkUsageMeasurement.Measured ? selected
                    : new FlowRunTokenUsageTotal(null, null, null, FlowRunTokenUsageCoverage.Unavailable, 0, selected.InvocationCount);
            }).ToArray());
            if (latest.Any(value => !value.Source.InvocationCountKnown) && total.Coverage == FlowRunTokenUsageCoverage.Complete)
                total = total with { Coverage = FlowRunTokenUsageCoverage.Partial };
            return new(total, total);
        }

        WorkUsageTotals Totals(WorkUsageMeasurement measurement)
        {
            var selected = latest.Where(value => value.Measurement == measurement).ToArray();
            var totals = FlowRunTokenUsageAggregation.Combine(selected.Select(value => query.FlowBoundary == WorkUsageFlowBoundary.Direct
                ? value.Totals.Direct : value.Totals.Inclusive).ToArray());
            return new(totals, totals)
            {
                CachedInputTokens = Sum(selected.Select(value => value.Totals.CachedInputTokens)),
                ReasoningOutputTokens = Sum(selected.Select(value => value.Totals.ReasoningOutputTokens))
            };
        }
    }
    private static long? Sum(IEnumerable<long?> values)
    {
        var known = values.Where(value => value is not null).Select(value => value!.Value).ToArray();
        return known.Length == 0 ? null : known.Sum();
    }
    private static Result<WorkUsageSummary> Failure(string code) => Result<WorkUsageSummary>.Failure(code);
}
