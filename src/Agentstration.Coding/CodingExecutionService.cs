using System.Text.Json;
using Agentstration.Application.Work;
using Agentstration.Coding.Contracts;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;

namespace Agentstration.Coding;

public sealed class CodingExecutionService(CodingFlowConfigurationService configurations, CodingRepositoryService repositories,
    FlowService flows, IWorkItemRepository work, IWorkPlanRepository plans, IWorkplaceContext context, TimeProvider clock)
    : IWorkPlanAdmissionGate, IRuntimeCapabilityMaterialProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public string RuntimeCapability => CodingExecutionProtocol.RuntimeCapability;

    public async Task<Result<IReadOnlyList<WorkPlanAdmissionEvidence>>> ObserveAsync(FlowRunScope scope, WorkPlanSnapshot plan,
        WorkPlanRevision revision, WorkTaskId taskId, CancellationToken token)
    {
        if (scope.WorkspaceId != context.WorkspaceId || scope.PrincipalId != context.PrincipalId)
            return AdmissionFailure("coding_flow_denied");
        var target = plan.Execution?.Targets.SingleOrDefault(value => value.TaskId == taskId);
        if (target is null) return Result<IReadOnlyList<WorkPlanAdmissionEvidence>>.Success([]);
        var version = await flows.ResolveAsync(scope.AccessScopeRef, target.Flow, target.Flow.FlowId.Namespace, token);
        if (version.Definition is not RuntimeFlowDefinition runtime || runtime.RuntimeCapability != RuntimeCapability)
            return Result<IReadOnlyList<WorkPlanAdmissionEvidence>>.Success([]);
        var resolved = await configurations.ResolveAsync(scope.WorkspaceId, runtime, true, token);
        if (!resolved.IsSuccess) return AdmissionFailure(resolved.ErrorCode!);
        var input = await configurations.ValidateInputAsync(scope.WorkspaceId, resolved.Value!, target.Input, token);
        if (!input.IsSuccess) return AdmissionFailure(input.ErrorCode!);
        if (plan.Execution?.ProjectId is not { } projectId) return AdmissionFailure("coding_project_required");
        var task = (await work.GetAsync(scope.WorkspaceId, scope.PrincipalId, taskId.ToWorkItemId(), token))?.Value;
        if (task?.TaskHierarchy?.Kind != WorkTaskKind.Atomic) return AdmissionFailure("coding_atomic_task_required");
        var repository = await repositories.ResolveAsync(scope.WorkspaceId, projectId,
            new(taskId.Value, [resolved.Value!.Configuration.Repository], task.Version), token);
        if (!repository.IsSuccess) return AdmissionFailure(repository.ErrorCode!);
        var selected = repository.Value!.Repositories.Single();
        return Result<IReadOnlyList<WorkPlanAdmissionEvidence>>.Success([new(CodingExecutionProtocol.AdmissionKind, taskId.ToString(),
            AdmissionDigest(resolved.Value.Configuration, input.Value!, selected), clock.GetUtcNow())]);
    }

    public async Task<RuntimeRootFlowExecutionMaterial> EnrichAsync(RuntimeWorkerAssignment assignment,
        RuntimeRootFlowExecutionMaterial material, CancellationToken token)
    {
        if (assignment.RuntimeCapability != RuntimeCapability || assignment.RuntimeCapabilityVersion != CodingExecutionProtocol.CapabilityVersion
            || assignment.ExecutionMaterialVersion != CodingExecutionProtocol.MaterialVersion || material.SchemaVersion != CodingExecutionProtocol.MaterialVersion)
            throw Invalid("coding_material_version_unsupported");
        if (assignment.CurrentAttempt is not { State: RuntimeAssignmentAttemptState.Active } claimed) throw Invalid("coding_assignment_inactive");
        if (material.WorkspaceId != context.WorkspaceId || material.PrincipalId != context.PrincipalId || !Guid.TryParse(material.WorkItemResourceId, out var itemId))
            throw Invalid("coding_plan_attempt_required");
        var execution = (await work.GetAsync(material.WorkspaceId, material.PrincipalId, new(itemId), token))?.Value;
        if (execution is null || !Guid.TryParse(execution.Metadata.GetValueOrDefault("workPlan.id"), out var planId)
            || !Guid.TryParse(execution.Metadata.GetValueOrDefault("workPlan.attemptId"), out var attemptId)) throw Invalid("coding_plan_attempt_required");
        var plan = await plans.GetAsync(material.WorkspaceId, material.PrincipalId, new(planId), token);
        var attempt = plan?.Attempts.SingleOrDefault(value => value.Id == attemptId && value.FlowRunId == material.RunId && value.WorkItemId.Value == itemId);
        if (plan is null || attempt is not { Status: WorkPlanAttemptStatus.Admitted or WorkPlanAttemptStatus.Submitted, CancellationRequested: false, ProjectContext: { } captured })
            throw Invalid("coding_plan_attempt_required");
        var task = (await work.GetAsync(material.WorkspaceId, material.PrincipalId, attempt.TaskId.ToWorkItemId(), token))?.Value;
        if (task?.TaskHierarchy?.Kind != WorkTaskKind.Atomic || task.CurrentExecutionId != new WorkExecutionId(attempt.Id)
            || task.TaskHierarchy.CreationDigest != attempt.TaskDigest) throw Invalid("coding_task_attempt_changed");
        var version = material.Definition.Deserialize<FlowVersion>(Json);
        if (version?.Definition is not RuntimeFlowDefinition runtime || version.FlowId != attempt.Flow.Resolve(attempt.Flow.FlowId.Namespace)
            || version.ScopeRef != attempt.Flow.ScopeRef || version.Version != attempt.Flow.Version
            || WorkPlanDefinition.Digest(attempt.Input) != WorkPlanDefinition.Digest(material.Input)) throw Invalid("coding_flow_pin_changed");
        var resolved = await configurations.ResolveAsync(material.WorkspaceId, runtime, true, token);
        if (!resolved.IsSuccess) throw Invalid(resolved.ErrorCode!);
        var input = await configurations.ValidateInputAsync(material.WorkspaceId, resolved.Value!, material.Input, token);
        if (!input.IsSuccess) throw Invalid(input.ErrorCode!);
        // Reauthorize the exact captured connection. A later Project edit cannot substitute a new one.
        var repository = await repositories.ResolveAdmittedAsync(material.WorkspaceId, plan.Id, attempt.Id, resolved.Value!.Configuration.Repository, token);
        if (!repository.IsSuccess) throw Invalid(repository.ErrorCode!);
        var digest = AdmissionDigest(resolved.Value.Configuration, input.Value!, repository.Value!);
        if (!attempt.AdmissionEvidence.Any(value => value.Kind == CodingExecutionProtocol.AdmissionKind && value.Reference == attempt.TaskId.ToString() && value.Revision == digest))
            throw Invalid("coding_admission_changed");
        var coordinates = new CodingInvocationCoordinates(material.WorkspaceId, material.PrincipalId, captured.ProjectId, plan.Id,
            attempt.PlanRevision, attempt.PlanDigest, attempt.TaskId, attempt.TaskVersion, attempt.TaskDigest, attempt.Id, attempt.WorkItemId,
            attempt.FlowRunId, assignment.Id.Value, claimed.Id.Value, claimed.Id.Value);
        var native = new CodingExecutionMaterial(CodingExecutionProtocol.MaterialVersion, coordinates, resolved.Value.Configuration,
            resolved.Value.Harness, repository.Value!, input.Value!, task.Instruction);
        if (!CodingExecutionValidation.Material(native)) throw Invalid("coding_material_invalid");
        return material with { RuntimeConfiguration = JsonSerializer.SerializeToElement(native, Json), OrchestrationTimeout = TimeSpan.FromSeconds(native.Configuration.TimeoutSeconds) };
    }

    private static string AdmissionDigest(CodingExecutionConfiguration configuration, CodingTaskInput input, CodingRepositoryIdentity repository) =>
        CodingExecutionValidation.AdmissionDigest(configuration, input, repository);
    private static Result<IReadOnlyList<WorkPlanAdmissionEvidence>> AdmissionFailure(string code) => Result<IReadOnlyList<WorkPlanAdmissionEvidence>>.Failure(code);
    private static RuntimeExecutionMaterialException Invalid(string code) => new(code, "The admitted Coding execution material is unavailable, stale or invalid.");
}
