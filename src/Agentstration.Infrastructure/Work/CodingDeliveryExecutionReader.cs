using System.Text.Json;
using Agentstration.Coding;
using Agentstration.Coding.Contracts;
using Agentstration.Flows;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Runtime.Abstractions;
using Agentstration.Work;

namespace Agentstration.Infrastructure.Work;

public sealed class CodingDeliveryExecutionReader(IFlowRepository flows, IRuntimeWorkerAssignmentStore assignments) : ICodingDeliveryExecutionReader
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task<Result<CodingDeliveryExecution>> ReadAsync(FlowRunScope scope, WorkProjectId projectId, WorkPlanId planId, WorkPlanAttempt attempt, CancellationToken token)
    {
        var run = (await flows.GetRunExactAsync(scope.ScopeRef, attempt.FlowRunId, token))?.Value;
        if (run is null || run.Scope != scope || !run.Status.IsTerminal() || run.ParentFlowRunId is not null
            || run.WorkTaskId != attempt.TaskId.ToString() || run.WorkItemResourceId != attempt.WorkItemId.ToString()
            || run.DefinitionSnapshot.Definition is not RuntimeFlowDefinition { RuntimeCapability: CodingExecutionProtocol.RuntimeCapability } runtime)
            return Result<CodingDeliveryExecution>.Failure("coding_delivery_attempt_invalid");
        CodingExecutionConfiguration? configuration;
        try { configuration = runtime.Configuration.Deserialize<CodingExecutionConfiguration>(Json); }
        catch (JsonException) { return Result<CodingDeliveryExecution>.Failure("coding_delivery_source_invalid"); }
        if (configuration is null) return Result<CodingDeliveryExecution>.Failure("coding_delivery_source_invalid");
        var assignment = (await assignments.GetByTargetExactAsync(scope.ScopeRef, RuntimeAssignmentTargetKind.FlowRun, attempt.FlowRunId, token))?.Value;
        return assignment is null ? Result<CodingDeliveryExecution>.Failure("coding_delivery_result_unavailable")
            : CodingDeliveryResultReader.Read(scope, projectId, planId, attempt, assignment, configuration);
    }
}
