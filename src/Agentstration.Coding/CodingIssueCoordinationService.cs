using System.Text.Json;
using Agentstration.Application.Work;
using Agentstration.Coding.Contracts;
using Agentstration.Resources;
using Agentstration.Work;

namespace Agentstration.Coding;

public sealed partial class CodingIssueCoordinationService(WorkProjectService projects, WorkPlanService plans,
    WorkProjectSharingService sharing, WorkTaskProviderService operations, ICodingIssueBindingPolicy bindings,
    IWorkplaceContext context, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<Result<CodingIssueIntakeResponse>> AcknowledgeAsync(WorkspaceId workspaceId, WorkProjectId projectId,
        AcknowledgeCodingIssueRequest command, CancellationToken token)
    {
        var owned = await projects.GetAsync(workspaceId, projectId, token);
        if (!owned.IsSuccess) return IntakeFailure(owned.ErrorCode!);
        var need = owned.Value!.Definition.ConnectionRequirements.SingleOrDefault(value => value.Name == command.TrackingConnection);
        var issue = new WorkSharingIssueReference(command.Provider, command.Repository, command.IssueId, command.PlanLabelPrefix, command.ParticipantLabelPrefix)
        { ConnectionName = command.TrackingConnection, Tracking = need?.Target };
        if (command.Id == Guid.Empty || command.PlanId == Guid.Empty || command.ExpectedProjectVersion < 1 || command.ExpectedSharedVersion < 0
            || !WorkSharingContent.ValidIssue(issue) || command.Allocation is null || command.Allocation.Count > 100)
            return IntakeFailure("coding_issue_command_invalid");
        var connected = await ConnectAsync(workspaceId, projectId, issue, token);
        if (!connected.IsSuccess) return IntakeFailure(connected.ErrorCode!);
        var connection = connected.Value!; var project = connection.Project; var planId = new WorkPlanId(command.PlanId);
        if (!project.Definition.PlanIds.Contains(planId)) return IntakeFailure("work_project_plan_not_associated");
        var local = await plans.GetAsync(workspaceId, planId, token);
        if (!local.IsSuccess) return IntakeFailure(local.ErrorCode!);
        var writeId = new WorkTaskOperationId(CommandId(projectId, command.Id, "issue-acknowledgment"));
        var existing = await operations.GetAsync(workspaceId, projectId, writeId, token);
        if (!existing.IsSuccess && existing.ErrorCode != "task_provider_operation_not_found") return IntakeFailure(existing.ErrorCode!);
        if (existing.Value is null && project.Version != command.ExpectedProjectVersion) return IntakeFailure("work_project_conflict");
        var participant = project.Definition.Sharing!.ParticipantId;
        if (existing.Value is { } prior && (prior.PlanId != planId || prior.Route.Binding.Name != issue.ConnectionName
            || WorkPlanDefinition.Digest(prior.Input) != WorkPlanDefinition.Digest(bindings.Acknowledgment(issue, planId, participant, prior.Input.ExpectedRevision!)))) return IntakeFailure("coding_issue_command_conflict");
        if (existing.Value is { } retained && !SameRoute(retained.Route, connection.Route)) return IntakeFailure("coding_issue_connection_changed");
        var native = await ReadIssueAsync(connection, issue, token);
        if (!native.IsSuccess) return IntakeFailure(native.ErrorCode!);
        var nativePlan = (Valid: native.Value!.AssociationValid, Id: native.Value.PlanId);
        if (!nativePlan.Valid || nativePlan.Id is { } nativeId && nativeId != planId)
            return Intake(command, projectId, CodingIssueIntakeStatus.Conflict, null, existing.Value?.Id.Value, "coding_issue_plan_label_conflict", native.Value!.Revision, nativePlan.Id);
        var pulled = await sharing.PullAsync(workspaceId, projectId, null, token);
        if (!pulled.IsSuccess) return IntakeFailure(pulled.ErrorCode!);
        if (!SharedConnectionMatches(project, pulled.Value!, issue)) return IntakeFailure("coding_issue_shared_connection_mismatch");
        var shared = SharedPlans(pulled.Value!);
        var associated = shared.Where(value => SameIssue(value.Plan.Issue, issue)).ToArray();
        if (associated.Length > 1 || associated.Length == 1 && associated[0].Plan.PlanId != planId)
            return Intake(command, projectId, CodingIssueIntakeStatus.Conflict, null, existing.Value?.Id.Value, "coding_issue_shared_plan_conflict", native.Value!.Revision, associated.Length == 1 ? associated[0].Plan.PlanId : null);
        var shareId = new WorkSharingOperationId(CommandId(projectId, command.Id, "issue-plan-association"));
        var previousPublication = await sharing.GetOperationAsync(workspaceId, projectId, shareId, token);
        if (!previousPublication.IsSuccess && previousPublication.ErrorCode != "sharing_operation_not_found") return IntakeFailure(previousPublication.ErrorCode!);
        Guid? publicationId = previousPublication.Value?.Id.Value;
        if (associated.Length == 0 || previousPublication.Value is not null)
        {
            var portable = await sharing.ExportPlanAsync(workspaceId, planId, command.Allocation, token, issue);
            if (!portable.IsSuccess) return IntakeFailure(portable.ErrorCode!);
            publicationId = shareId.Value;
            var change = new WorkSharingChange(new(WorkSharingDomain.Plan, planId.ToString()), command.ExpectedSharedVersion,
                JsonSerializer.SerializeToElement(portable.Value!.Plan, Json));
            var publication = await sharing.PublishAsync(workspaceId, projectId, new(shareId, project.Version, [change]), token);
            if (!publication.IsSuccess) return IntakeFailure(publication.ErrorCode!);
            if (publication.Value!.Status != WorkSharingOperationStatus.Succeeded)
                return Intake(command, projectId, publication.Value.Status is WorkSharingOperationStatus.Pending or WorkSharingOperationStatus.Uncertain
                    ? CodingIssueIntakeStatus.Pending : CodingIssueIntakeStatus.Conflict, publicationId, existing.Value?.Id.Value, publication.Value.ErrorCode, native.Value!.Revision);
        }
        else
        {
            var authoritative = associated[0].Plan;
            if (authoritative.Issue != issue) return IntakeFailure("coding_issue_policy_conflict");
            var exported = await sharing.ExportPlanAsync(workspaceId, planId, authoritative.Allocation, token, issue);
            if (!exported.IsSuccess || WorkPlanDefinition.Digest(JsonSerializer.SerializeToElement(exported.Value!.Plan, Json)) != associated[0].Record.Digest)
                return IntakeFailure("coding_issue_local_plan_conflict");
        }
        // Shared CAS established the association; native labels are additive observations, not a GitHub lock.
        // Reserve and replay the exact same native write intent through the canonical durable operation service.
        Result<WorkTaskOperation> written;
        if (existing.Value is not null) written = await operations.ResumeAsync(workspaceId, projectId, writeId, token);
        else
        {
            native = await ReadIssueAsync(connection, issue, token);
            if (!native.IsSuccess) return IntakeFailure(native.ErrorCode!);
            nativePlan = (native.Value!.AssociationValid, native.Value.PlanId);
            if (!nativePlan.Valid || nativePlan.Id is { } current && current != planId)
                return Intake(command, projectId, CodingIssueIntakeStatus.Conflict, publicationId, null, "coding_issue_plan_label_conflict", native.Value!.Revision, nativePlan.Id);
            written = await operations.BeginAsync(workspaceId, projectId, new(writeId, project.Version, connection.Binding.Name,
                bindings.Acknowledgment(issue, planId, participant, native.Value!.Revision),
                planId, local.Value!.Revision.Number, local.Value.Revision.Digest)
            { ExpectedRouteDigest = WorkPlanDefinition.Digest(connection.Route) }, token);
        }
        if (!written.IsSuccess) return IntakeFailure(written.ErrorCode!);
        if (written.Value!.Status != WorkTaskOperationStatus.Succeeded)
            return Intake(command, projectId, written.Value.Status == WorkTaskOperationStatus.Rejected ? CodingIssueIntakeStatus.Conflict : CodingIssueIntakeStatus.Pending,
                publicationId, writeId.Value, written.Value.ErrorCode, written.Value.Task?.Revision);
        native = await ReadIssueAsync(connection, issue, token);
        if (!native.IsSuccess) return Intake(command, projectId, CodingIssueIntakeStatus.Pending, publicationId, writeId.Value, native.ErrorCode, null);
        var confirmed = (Valid: native.Value!.AssociationValid, Id: native.Value.PlanId);
        if (!confirmed.Valid || confirmed.Id != planId || !native.Value!.Acknowledged)
            return Intake(command, projectId, CodingIssueIntakeStatus.Conflict, publicationId, writeId.Value, "coding_issue_acknowledgment_changed", native.Value!.Revision);
        // Recheck shared authority after the external write as another participant may have edited it.
        pulled = await sharing.PullAsync(workspaceId, projectId, null, token);
        if (!pulled.IsSuccess) return Intake(command, projectId, CodingIssueIntakeStatus.Pending, publicationId, writeId.Value, pulled.ErrorCode, native.Value.Revision);
        associated = SharedPlans(pulled.Value!).Where(value => SameIssue(value.Plan.Issue, issue)).ToArray();
        if (associated.Length != 1 || associated[0].Plan.PlanId != planId || associated[0].Plan.Issue != issue)
            return Intake(command, projectId, CodingIssueIntakeStatus.Conflict, publicationId, writeId.Value, "coding_issue_shared_plan_conflict", native.Value.Revision);
        var confirmedOperation = await sharing.SetPlanOperationAsync(workspaceId, projectId, planId, writeId, token);
        if (!confirmedOperation.IsSuccess) return IntakeFailure(confirmedOperation.ErrorCode!);
        return Intake(command, projectId, CodingIssueIntakeStatus.Confirmed, publicationId, writeId.Value, null, native.Value.Revision);
    }

    private async Task<Result<Connection>> ConnectAsync(WorkspaceId workspaceId, WorkProjectId id, WorkSharingIssueReference issue, CancellationToken token)
    {
        var loaded = await projects.GetAsync(workspaceId, id, token);
        if (!loaded.IsSuccess) return Result<Connection>.Failure(loaded.ErrorCode!);
        var project = loaded.Value!;
        if (project.Definition.Sharing is null) return Result<Connection>.Failure("coding_issue_sharing_required");
        var need = project.Definition.ConnectionRequirements.SingleOrDefault(value => value.Name == issue.ConnectionName);
        var assignment = project.Definition.ConnectionAssignments.SingleOrDefault(value => value.Name == issue.ConnectionName);
        if (need is null || assignment is null || need.Contract != WorkProjectConnectionRules.Tracking || need.Target != issue.Tracking
            || !need.RequiredOperations.Contains("read") || !need.RequiredOperations.Contains("update"))
            return Result<Connection>.Failure("coding_issue_connection_invalid");
        var selected = WorkProjectConnectionRules.Binding(new(need, assignment));
        var policy = await bindings.ResolveAsync(workspaceId, context.PrincipalId, selected, issue, token);
        return policy.IsSuccess ? Result<Connection>.Success(new(project, policy.Value!.Route.Binding, policy.Value.Route)) : Result<Connection>.Failure(policy.ErrorCode!);
    }
    private async Task<Result<CodingIssueObservation>> ReadIssueAsync(Connection connection, WorkSharingIssueReference issue, CancellationToken token)
    {
        var read = await operations.BeginAsync(connection.Project.WorkspaceId, connection.Project.Id,
            new(new(Guid.NewGuid()), connection.Project.Version, connection.Binding.Name,
                new(ExternalTaskOperationKind.Read, TaskId: issue.TaskId) { Tracking = issue.Tracking })
            { ExpectedRouteDigest = WorkPlanDefinition.Digest(connection.Route) }, token);
        return read is { IsSuccess: true, Value.Status: WorkTaskOperationStatus.Succeeded, Value.Task: { } task }
            ? bindings.Observe(task, issue, connection.Project.Definition.Sharing!.ParticipantId)
            : Result<CodingIssueObservation>.Failure(read.ErrorCode ?? read.Value?.ErrorCode ?? "coding_issue_authority_unavailable");
    }
    private static bool SameIssue(WorkSharingIssueReference? left, WorkSharingIssueReference right) => left is not null
        && left.Tracking == right.Tracking && left.TaskId == right.TaskId;
    private static bool SharedConnectionMatches(WorkProjectSnapshot project, WorkProjectSharingState shared, WorkSharingIssueReference issue)
    {
        var metadata = shared.Records.SingleOrDefault(value => value.Key == WorkSharingProjectMetadata.Key)?.Content.Deserialize<WorkSharingProjectMetadata>(Json);
        var declared = metadata?.ConnectionRequirements.SingleOrDefault(value => value.Name == issue.ConnectionName);
        var own = project.Definition.ConnectionRequirements.SingleOrDefault(value => value.Name == issue.ConnectionName);
        return declared is not null && own is not null && WorkPlanDefinition.Digest(declared) == WorkPlanDefinition.Digest(own);
    }
    private static IReadOnlyList<(WorkSharingPlan Plan, WorkSharingRecord Record)> SharedPlans(WorkProjectSharingState state) => state.Records
        .Where(record => record.Key.Domain == WorkSharingDomain.Plan).Select(record => (record.Content.Deserialize<WorkSharingPlan>(Json)!, record)).ToArray();
    private static Guid CommandId(WorkProjectId projectId, Guid id, string phase) => new(Convert.FromHexString(WorkPlanDefinition.Digest(new { projectId, id, phase })).AsSpan(0, 16));
    private Result<CodingIssueIntakeResponse> Intake(AcknowledgeCodingIssueRequest command, WorkProjectId projectId, CodingIssueIntakeStatus status,
        Guid? publicationId, Guid? operationId, string? error, string? revision, WorkPlanId? conflictingPlan = null) => Result<CodingIssueIntakeResponse>.Success(new(command.Id, projectId.Value, command.PlanId,
            status, publicationId, operationId, error, revision, clock.GetUtcNow())
        { ConflictingPlanId = conflictingPlan?.Value });
    private static Result<CodingIssueIntakeResponse> IntakeFailure(string code) => Result<CodingIssueIntakeResponse>.Failure(code);
    private sealed record Connection(WorkProjectSnapshot Project, WorkProjectContextBinding Binding, WorkTaskProviderRoute Route);
}
