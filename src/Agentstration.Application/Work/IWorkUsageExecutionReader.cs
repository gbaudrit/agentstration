using Agentstration.Flows;
using Agentstration.Work;

namespace Agentstration.Application.Work;

public sealed record WorkUsageExecution(WorkUsageSource Source, WorkUsageTotals Totals, IReadOnlyList<WorkUsageObservation> Observations);

/// <summary>Reads durable, terminal execution evidence for an already authorized historical functional attempt.</summary>
public interface IWorkUsageExecutionReader
{
    Task<Result<IReadOnlyList<WorkUsageExecution>>> ReadAsync(FlowRunScope scope, WorkProjectId projectId,
        WorkPlanId planId, WorkPlanAttempt attempt, CancellationToken cancellationToken);
}
