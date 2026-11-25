using Agentstration.Application.Work;
using Agentstration.Coding;
using Agentstration.Coding.Contracts;
using Agentstration.Flows;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Runtime.Abstractions;
using Agentstration.Work;

namespace Agentstration.Infrastructure.Work;

public sealed class WorkUsageExecutionReader(IFlowRepository flows, IRuntimeWorkerAssignmentStore assignments) : IWorkUsageExecutionReader
{
    public async Task<Result<IReadOnlyList<WorkUsageExecution>>> ReadAsync(FlowRunScope scope, WorkProjectId projectId,
        WorkPlanId planId, WorkPlanAttempt attempt, CancellationToken token)
    {
        var run = (await flows.GetRunExactAsync(scope.ScopeRef, attempt.FlowRunId, token))?.Value;
        if (run is null || run.Scope != scope || !run.Status.IsTerminal() || run.ParentFlowRunId is not null
            || run.WorkTaskId != attempt.TaskId.ToString() || run.WorkItemResourceId != attempt.WorkItemId.ToString()) return Failure("usage_execution_not_final");
        var assignment = (await assignments.GetByTargetExactAsync(scope.ScopeRef, RuntimeAssignmentTargetKind.FlowRun, run.Id, token))?.Value;
        if (assignment is null && run.DefinitionSnapshot.Definition is RuntimeFlowDefinition { RuntimeCapability: CodingExecutionProtocol.RuntimeCapability })
            return Failure("usage_source_unavailable");
        if (assignment?.RuntimeCapability == CodingExecutionProtocol.RuntimeCapability)
            return CodingUsageAccounting.Read(scope, projectId, planId, attempt, assignment);
        if (run.TokenUsage.State != FlowRunTokenUsageState.Final) return Failure("usage_execution_not_final");
        var source = new WorkUsageSource(WorkUsageSourceKind.FlowRun, "flow-run/1.0", attempt.Id, attempt.WorkItemId.Value, run.Id, attempt.Status);
        return Result<IReadOnlyList<WorkUsageExecution>>.Success([new(source, new(run.TokenUsage.Direct, run.TokenUsage.Inclusive), [])]);
    }
    private static Result<IReadOnlyList<WorkUsageExecution>> Failure(string code) => Result<IReadOnlyList<WorkUsageExecution>>.Failure(code);
}
