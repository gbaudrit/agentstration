using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;

namespace Agentstration.Application.Work;

public sealed record ExportedWorkSharingPlan(WorkSharingPlan Plan, long Version);

public sealed partial class WorkProjectSharingService
{
    public async Task<Result<ExportedWorkSharingPlan>> ExportPlanAsync(WorkspaceId workspaceId, WorkPlanId id,
        IReadOnlyList<WorkTaskAllocation> allocation, CancellationToken token, WorkSharingIssueReference? issue = null)
    {
        var loaded = await plans.GetAsync(workspaceId, id, token);
        if (!loaded.IsSuccess) return Result<ExportedWorkSharingPlan>.Failure(loaded.ErrorCode!);
        var roots = new Dictionary<WorkTaskId, WorkTaskTreeDefinition>();
        foreach (var taskId in loaded.Value!.Revision.Definition.TaskIds)
        {
            if (roots.Values.Any(tree => tree.Tasks.Any(task => task.Id == taskId))) continue;
            var anchor = taskId;
            for (var depth = 0; ; depth++)
            {
                var tree = await trees.GetAsync(workspaceId, anchor, token);
                if (!tree.IsSuccess) return Result<ExportedWorkSharingPlan>.Failure(tree.ErrorCode!);
                var node = tree.Value!.Tasks.Single(node => node.Definition.Id == anchor);
                if (node.Definition.ParentTaskId is not { } parent)
                {
                    roots[anchor] = new(anchor, tree.Value.Tasks.Select(node => node.Definition).ToArray()); break;
                }
                if (depth >= WorkTaskTreeDefinition.MaximumDepth) return Result<ExportedWorkSharingPlan>.Failure("sharing_plan_not_portable");
                anchor = parent;
            }
        }
        var portable = new WorkSharingPlan(id, loaded.Value.Revision.Definition, roots.Values.ToArray(), allocation) { Issue = issue };
        var normalized = WorkSharingContent.Normalize(new(new(WorkSharingDomain.Plan, id.ToString()), 0, JsonSerializer.SerializeToElement(portable, Json)));
        return normalized.IsSuccess ? Result<ExportedWorkSharingPlan>.Success(new(normalized.Value!.Content.Deserialize<WorkSharingPlan>(Json)!, loaded.Value.Plan.Version))
            : Result<ExportedWorkSharingPlan>.Failure("sharing_plan_not_portable");
    }

    /// <summary>Explicit, retry-safe projection into ordinary local draft Work. Never activates a plan or applies Bootstrap.</summary>
    public async Task<Result<WorkPlanView>> ImportPlanAsync(WorkspaceId workspaceId, WorkProjectId projectId, WorkPlanId planId, string expectedDigest, CancellationToken token)
    {
        var pulled = await PullAsync(workspaceId, projectId, null, token);
        if (!pulled.IsSuccess) return Result<WorkPlanView>.Failure(pulled.ErrorCode!);
        var record = pulled.Value!.Records.SingleOrDefault(record => record.Key == new WorkSharingRecordKey(WorkSharingDomain.Plan, planId.ToString()));
        if (record is null || record.Digest != expectedDigest) return Result<WorkPlanView>.Failure("sharing_record_conflict");
        var portable = record.Content.Deserialize<WorkSharingPlan>(Json)!;
        var pinnedProject = await projects.GetAsync(workspaceId, projectId, token);
        if (!pinnedProject.IsSuccess || pinnedProject.Value!.Version != pulled.Value.ProjectVersion
            || pinnedProject.Value.Definition.Sharing != pulled.Value.Connection) return Result<WorkPlanView>.Failure("sharing_connection_changed");
        // Refuse a plan already associated with another sharing Project, before creating local state.
        var mapping = await repository.FindPlanProjectAsync(workspaceId, context.PrincipalId, planId, token);
        if (mapping is not null && mapping != projectId) return Result<WorkPlanView>.Failure("sharing_plan_project_conflict");
        foreach (var tree in portable.Trees)
        {
            var created = await trees.CreateAsync(workspaceId, tree, token);
            if (!created.IsSuccess) return Result<WorkPlanView>.Failure(created.ErrorCode!);
        }
        var local = await plans.GetAsync(workspaceId, planId, token);
        var command = new Guid(Convert.FromHexString(WorkPlanDefinition.Digest(new { Import = planId, record.Digest })).AsSpan(0, 16));
        if (!local.IsSuccess)
        {
            if (local.ErrorCode != "work_plan_not_found") return local;
            local = await plans.CreateAsync(workspaceId, planId, new(command, 0, portable.Definition), token);
        }
        else if (WorkPlanDefinition.Digest(local.Value!.Revision.Definition) != WorkPlanDefinition.Digest(portable.Definition))
        {
            // Import is not permission to replace a local proposal or a running/validated plan.
            return Result<WorkPlanView>.Failure("sharing_local_plan_conflict");
        }
        if (!local.IsSuccess) return local;
        if (!await repository.MapPlanAsync(workspaceId, context.PrincipalId, projectId, planId, token))
            return Result<WorkPlanView>.Failure("sharing_plan_project_conflict");
        var project = await projects.GetAsync(workspaceId, projectId, token);
        if (!project.IsSuccess) return Result<WorkPlanView>.Failure(project.ErrorCode!);
        if (project.Value!.Definition.Sharing != pulled.Value.Connection) return Result<WorkPlanView>.Failure("sharing_connection_changed");
        if (!project.Value!.Definition.PlanIds.Contains(planId))
        {
            var definition = project.Value.Definition with { PlanIds = [.. project.Value.Definition.PlanIds, planId] };
            var saved = await projects.UpdateAsync(workspaceId, projectId, new(command, project.Value.Version, definition), token);
            if (!saved.IsSuccess) return Result<WorkPlanView>.Failure(saved.ErrorCode!);
        }
        return local;
    }

    public async Task<Result<WorkPlanExternalOperationReference>> GetPlanOperationAsync(WorkspaceId workspaceId, WorkProjectId projectId, WorkPlanId planId, CancellationToken token)
    {
        var project = await projects.GetAsync(workspaceId, projectId, token);
        if (!project.IsSuccess) return Result<WorkPlanExternalOperationReference>.Failure(project.ErrorCode!);
        var id = await repository.GetPlanOperationAsync(workspaceId, context.PrincipalId, projectId, planId, token);
        return id is { } operationId ? Result<WorkPlanExternalOperationReference>.Success(new(planId, operationId))
            : Result<WorkPlanExternalOperationReference>.Failure("work_project_plan_operation_not_found");
    }
    public async Task<Result<WorkPlanExternalOperationReference>> SetPlanOperationAsync(WorkspaceId workspaceId, WorkProjectId projectId, WorkPlanId planId, WorkTaskOperationId operationId, CancellationToken token)
    {
        var project = await projects.GetAsync(workspaceId, projectId, token);
        if (!project.IsSuccess) return Result<WorkPlanExternalOperationReference>.Failure(project.ErrorCode!);
        return await repository.SetPlanOperationAsync(workspaceId, context.PrincipalId, projectId, planId, operationId, token)
            ? Result<WorkPlanExternalOperationReference>.Success(new(planId, operationId))
            : Result<WorkPlanExternalOperationReference>.Failure("work_project_plan_operation_conflict");
    }

    internal async Task<Result<IReadOnlyList<WorkPlanAdmissionEvidence>>> ObserveAllocationAsync(FlowRunScope scope, WorkPlanSnapshot plan,
        WorkPlanRevision revision, WorkTaskId taskId, CancellationToken token)
    {
        var mapped = await repository.FindPlanProjectAsync(scope.WorkspaceId, scope.PrincipalId, plan.Id, token);
        var projectId = plan.Execution?.ProjectId;
        if (mapped is not null && projectId != mapped) return AdmissionFailure("sharing_project_required");
        if (projectId is null) return Result<IReadOnlyList<WorkPlanAdmissionEvidence>>.Success([]);
        var project = await projects.GetAsync(scope.WorkspaceId, projectId.Value, token);
        if (!project.IsSuccess) return AdmissionFailure(project.ErrorCode!);
        if (project.Value!.Definition.Sharing is null)
            return mapped is null ? Result<IReadOnlyList<WorkPlanAdmissionEvidence>>.Success([]) : AdmissionFailure("sharing_connection_required");
        var connected = await ConnectAsync(scope.WorkspaceId, projectId.Value, token);
        if (!connected.IsSuccess) return AdmissionFailure(connected.ErrorCode!);
        var snapshot = await ReadAsync(connected.Value!, null, token);
        if (!snapshot.IsSuccess) return AdmissionFailure(snapshot.ErrorCode!);
        var record = snapshot.Value!.Records.SingleOrDefault(record => record.Key == new WorkSharingRecordKey(WorkSharingDomain.Plan, plan.Id.ToString()));
        if (record is null) return AdmissionFailure("sharing_plan_required");
        var shared = record.Content.Deserialize<WorkSharingPlan>(Json)!;
        if (ValidateLocalConnections(project.Value, snapshot.Value, shared, taskId, plan) is { } connectionError)
            return AdmissionFailure(connectionError);
        var allocation = shared.Allocation.SingleOrDefault(value => value.TaskId == taskId);
        if (allocation?.ParticipantId != project.Value.Definition.Sharing.ParticipantId) return AdmissionFailure("sharing_task_not_allocated");
        var local = await ExportPlanAsync(scope.WorkspaceId, plan.Id, shared.Allocation, token, shared.Issue);
        if (!local.IsSuccess || local.Value!.Version != plan.Version
            || WorkPlanDefinition.Digest(JsonSerializer.SerializeToElement(local.Value.Plan, Json)) != record.Digest
            || WorkPlanDefinition.Digest(local.Value.Plan.Definition) != WorkPlanDefinition.Digest(revision.Definition)) return AdmissionFailure("sharing_plan_stale");
        if (!await repository.MapPlanAsync(scope.WorkspaceId, scope.PrincipalId, projectId.Value, plan.Id, token)) return AdmissionFailure("sharing_plan_project_conflict");
        var evidence = new List<WorkPlanAdmissionEvidence>
        {
            new("project-sharing", $"{shared.PlanId}:{taskId}:{allocation.ParticipantId}",
                $"{snapshot.Value.Revision}:{record.Digest}", clock.GetUtcNow())
        };
        if (shared.Definition.TaskConnections.Any(value => value.TaskId == taskId))
        {
            var metadata = snapshot.Value.Records.Single(value => value.Key == WorkSharingProjectMetadata.Key);
            evidence.Add(new("project-sharing-connections", taskId.ToString(), $"{snapshot.Value.Revision}:{metadata.Digest}", clock.GetUtcNow()));
        }
        return Result<IReadOnlyList<WorkPlanAdmissionEvidence>>.Success(evidence);
    }
    private static Result<IReadOnlyList<WorkPlanAdmissionEvidence>> AdmissionFailure(string code) => Result<IReadOnlyList<WorkPlanAdmissionEvidence>>.Failure(code);
}

public sealed class WorkProjectSharingAdmissionGate(WorkProjectSharingService sharing) : IWorkPlanAdmissionGate
{
    public Task<Result<IReadOnlyList<WorkPlanAdmissionEvidence>>> ObserveAsync(FlowRunScope scope, WorkPlanSnapshot plan,
        WorkPlanRevision revision, WorkTaskId taskId, CancellationToken cancellationToken) => sharing.ObserveAllocationAsync(scope, plan, revision, taskId, cancellationToken);
}
