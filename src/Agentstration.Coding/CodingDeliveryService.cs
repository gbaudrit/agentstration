using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Application.Work;
using Agentstration.Coding.Contracts;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;
using Microsoft.Extensions.Logging;

namespace Agentstration.Coding;

public sealed partial class CodingDeliveryService(IWorkTaskDeliveryRepository repository, WorkProjectService projects,
    WorkPlanService plans, IWorkItemRepository work, ICodingDeliveryExecutionReader executions, ICodingCodePublicationGateway code, ICodingForgeGateway forge,
    CodingRepositoryService repositories, CodingIssueCoordinationService issues, WorkProjectSharingService sharing,
    WorkUsageService usage, WorkTaskExecutionEvidenceService evidence, IWorkplaceRepository workplace,
    IWorkplaceContext context, TimeProvider clock, ILogger<CodingDeliveryService> logger, WorkProjectContextService? projectContexts = null)
{
    private async Task<string?> RevalidateNativeAsync(WorkspaceId workspace, WorkProjectContextSnapshot snapshot, CancellationToken token)
    {
        foreach (var binding in snapshot.Bindings.Where(value => value.Binding.NativeService is not null))
        {
            var validated = projectContexts is null ? null : await projectContexts.RevalidateAsync(workspace, binding, token);
            if (validated?.IsSuccess != true) return validated?.ErrorCode ?? "work_project_connection_unsupported";
        }
        return null;
    }
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public Task InitializeAsync(CancellationToken token) => repository.InitializeAsync(token);

    public async Task<Result<WorkTaskDeliveryOperation>> BeginAsync(WorkspaceId workspace, WorkProjectId projectId, BeginCodingDeliveryRequest command, CancellationToken token)
    {
        var owned = await projects.GetAsync(workspace, projectId, token);
        if (!owned.IsSuccess) return Failure(owned.ErrorCode!);
        if (command is null || command.Id == Guid.Empty || command.PlanId == Guid.Empty || command.AttemptId == Guid.Empty
            || command.ExpectedProjectVersion < 1 || command.ExpectedPlanVersion < 1 || command.ExpectedTaskVersion < 1 || command.ExpectedSharedPlanVersion < 1
            || !WorkSharingFormat.Digest(command.ExpectedSharedPlanDigest) || string.IsNullOrWhiteSpace(command.Title) || command.Title.Length > 512
            || command.Title.Any(char.IsControl) || command.Body is null || Encoding.UTF8.GetByteCount(command.Body) > 16 * 1024
            || !WorkProjectDefinition.ValidName(command.TrackingConnection) || !WorkProjectDefinition.ValidName(command.ForgeConnection)
            || !WorkProjectDefinition.ValidName(command.CodePublicationConnection) || !WorkProjectDefinition.ValidName(command.SharingConnection))
            return Failure("coding_delivery_command_invalid");
        var project = owned.Value!; var id = new WorkTaskDeliveryId(command.Id);
        var prior = await repository.GetAsync(workspace, context.PrincipalId, projectId, id, token);
        if (prior is not null) return SameCommand(prior, command) ? await ResumeAsync(workspace, projectId, id, token) : Failure("coding_delivery_command_conflict");
        var view = await plans.GetAsync(workspace, new(command.PlanId), token);
        var attempt = view.Value?.Plan.Attempts.SingleOrDefault(value => value.Id == command.AttemptId);
        if (!view.IsSuccess || attempt?.ProjectContext?.ProjectId != projectId || attempt.ProjectContext.Sharing is null
            || view.Value!.Plan.Execution?.Scope is not { } scope || scope.WorkspaceId != workspace || scope.PrincipalId != context.PrincipalId)
            return Failure("coding_delivery_attempt_not_found");
        if (attempt.Status is WorkPlanAttemptStatus.Admitted or WorkPlanAttemptStatus.Submitted) return Failure("coding_delivery_execution_not_final");
        if (project.Version != command.ExpectedProjectVersion || view.Value.Plan.Version != command.ExpectedPlanVersion
            || view.Value.Revision.Number != attempt.PlanRevision || view.Value.Revision.Digest != attempt.PlanDigest) return Failure("coding_delivery_review_stale");
        var task = (await work.GetAsync(workspace, context.PrincipalId, attempt.TaskId.ToWorkItemId(), token))?.Value;
        if (task?.TaskHierarchy?.Kind != WorkTaskKind.Atomic || task.Version != command.ExpectedTaskVersion || task.CurrentExecutionId != new WorkExecutionId(attempt.Id))
            return Failure("coding_delivery_task_stale");
        if (!SameBindings(project, attempt.ProjectContext)
            || Binding(attempt.ProjectContext, WorkProjectBindingPurpose.CodePublication)?.Name != command.CodePublicationConnection
            || Binding(attempt.ProjectContext, WorkProjectBindingPurpose.Sharing)?.Name != command.SharingConnection
            || Binding(attempt.ProjectContext, WorkProjectBindingPurpose.Repository) is null) return Failure("coding_delivery_connection_changed");
        var nativeProofError = await RevalidateNativeAsync(workspace, attempt.ProjectContext, token);
        if (nativeProofError is not null) return Failure(nativeProofError);
        var tracking = attempt.ProjectContext.Bindings.SingleOrDefault(value => value.Binding.Connection?.Requirement.Name == command.TrackingConnection);
        var selectedForge = attempt.ProjectContext.Bindings.SingleOrDefault(value => value.Binding.Connection?.Requirement.Name == command.ForgeConnection);
        if (tracking?.Binding.Connection?.Requirement.Contract != WorkProjectConnectionRules.Tracking
            || selectedForge?.Binding.Connection?.Requirement.Contract != WorkProjectConnectionRules.Forge)
            return Failure("coding_delivery_connection_not_captured");
        var previousAttempt = await repository.GetAttemptAsync(workspace, context.PrincipalId, projectId, attempt.Id, token);
        if (previousAttempt is not null) return Failure("coding_delivery_attempt_already_retained");
        // Accounting is durable even when the native result cannot supply publishable code.
        var captured = await usage.CaptureAsync(workspace, projectId, view.Value.Plan.Id, attempt.Id, token);
        if (!captured.IsSuccess) return Failure(captured.ErrorCode!);
        var execution = await executions.ReadAsync(scope, projectId, view.Value.Plan.Id, attempt, token);
        if (!execution.IsSuccess) return Failure(execution.ErrorCode!);
        var source = execution.Value!; var native = source.Result;
        var pulled = await sharing.PullAsync(workspace, projectId, null, token);
        if (!pulled.IsSuccess) return Failure(pulled.ErrorCode!);
        var record = pulled.Value!.Records.SingleOrDefault(value => value.Key == new WorkSharingRecordKey(WorkSharingDomain.Plan, view.Value.Plan.Id.ToString()));
        if (record is null || record.Version != command.ExpectedSharedPlanVersion || record.Digest != command.ExpectedSharedPlanDigest)
            return Failure("coding_delivery_shared_plan_stale");
        var portable = record.Content.Deserialize<WorkSharingPlan>(Json)!;
        var participant = attempt.ProjectContext.Sharing.ParticipantId;
        if (portable.Issue is not { } issue || issue.ConnectionName != command.TrackingConnection
            || tracking.Binding.Connection!.Requirement.Target != issue.Tracking
            || portable.Allocation.SingleOrDefault(value => value.TaskId == attempt.TaskId)?.ParticipantId != participant
            || !attempt.AdmissionEvidence.Any(value => value.Kind == "project-sharing" && value.Reference == $"{view.Value.Plan.Id}:{attempt.TaskId}:{participant}"
                && value.Revision.EndsWith(":" + record.Digest, StringComparison.Ordinal))
            || !attempt.AdmissionEvidence.Any(value => value.Kind == "repository-issue" && value.Reference == $"{issue.Repository}#{issue.TaskId}:{view.Value.Plan.Id}"
                && WorkSharingFormat.Digest(value.Revision))) return Failure("coding_delivery_admission_proof_invalid");
        var readiness = await issues.ReadinessAsync(workspace, projectId, view.Value.Plan.Id, token);
        if (!readiness.IsSuccess || !readiness.Value!.Eligible || readiness.Value.Repository != issue.Repository || readiness.Value.IssueId != issue.TaskId)
            return Failure(readiness.ErrorCode ?? "coding_issue_dependencies_blocked");
        var resolved = await repositories.ResolveAsync(workspace, projectId, new(attempt.TaskId.Value, [source.Repository], task.Version), token);
        if (!resolved.IsSuccess) return Failure(resolved.ErrorCode!);
        var identity = resolved.Value!.Repositories.Single();
        var input = attempt.Input.Deserialize<CodingTaskInput>(Json)!;
        if (!attempt.AdmissionEvidence.Any(value => value.Kind == CodingExecutionProtocol.AdmissionKind && value.Reference == attempt.TaskId.ToString()
            && value.Revision == CodingExecutionValidation.AdmissionDigest(source.Configuration, input, identity))) return Failure("coding_delivery_repository_changed");
        if (attempt.Status == WorkPlanAttemptStatus.Completed && identity.Revision != native.BaseRevision) return Failure("coding_delivery_base_stale");
        var resultIds = (await workplace.ListResultsAsync(workspace, attempt.TaskId, token)).Where(value => value.FlowRunId == attempt.FlowRunId)
            .Select(value => value.Id).Distinct().OrderBy(value => value.Value).Take(65).ToArray();
        var artifactIds = (await workplace.ListArtifactsAsync(workspace, attempt.TaskId, token)).Where(value => value.FlowRunId == attempt.FlowRunId)
            .Select(value => value.Id).Distinct().OrderBy(value => value.Value).Take(65).ToArray();
        WorkForgeDeliveryRoute? forgeRoute = null;
        if (attempt.Status == WorkPlanAttemptStatus.Completed)
        {
            var reviewedForge = await forge.InspectAsync(workspace, context.PrincipalId, selectedForge, identity, token);
            if (!reviewedForge.IsSuccess) return Failure(reviewedForge.ErrorCode!);
            forgeRoute = reviewedForge.Value!;
        }
        var intent = new WorkTaskDeliveryIntent(view.Value.Plan.Id, view.Value.Plan.Version, attempt.PlanRevision, attempt.PlanDigest, attempt.TaskId,
            task.Version, attempt.TaskVersion, attempt.TaskDigest, attempt.ProjectContext, issue, record.Version, record.Digest,
            new(attempt.Id, attempt.WorkItemId, attempt.FlowRunId, native.Coordinates.AssignmentId, native.Coordinates.AssignmentAttemptId,
                native.Coordinates.InvocationId, source.Digest, attempt.Status, native.Diagnostic), identity.Repository, identity.Provider, identity.NativeId, identity.CloneUrl,
            native.BaseRevision, identity.DefaultBranch, $"refs/heads/agentstration/{view.Value.Plan.Id.Value:N}/{attempt.TaskId.Value:N}/{attempt.Id:N}",
            command.Title.Trim(), command.Body, attempt.Status == WorkPlanAttemptStatus.Completed
                ? native.CodeChanges!.Select(value => new WorkCodeFile(value.Path, value.ContentBase64, value.Executable)).ToArray() : [],
            native.Checks.Select(value => new WorkCodeCheck(value.Name, value.ExitCode, value.Completed, value.Diagnostic)).ToArray())
        {
            ResultIds = resultIds,
            ArtifactIds = artifactIds,
            ExistingEvidenceId = attempt.Evidence?.EventId,
            ForgeConnection = command.ForgeConnection,
            CodePublicationConnection = command.CodePublicationConnection,
            SharingConnection = command.SharingConnection,
            Forge = forgeRoute
        };
        var now = clock.GetUtcNow();
        var operation = new WorkTaskDeliveryOperation(id, workspace, context.PrincipalId, projectId, project.Version, intent, WorkTaskDeliveryFormat.Digest(intent),
            WorkTaskDeliveryStatus.Pending, WorkTaskDeliveryStage.Prepared, 1, now, now);
        if (!WorkTaskDeliveryFormat.Valid(operation)) return Failure("coding_delivery_intent_invalid");
        var admitted = await repository.AdmitAsync(operation, token);
        if (admitted is not (WorkTaskDeliverySaveStatus.Saved or WorkTaskDeliverySaveStatus.Replayed))
            return Failure(admitted == WorkTaskDeliverySaveStatus.WorkChanged ? "coding_delivery_review_stale" : "coding_delivery_command_conflict");
        return await ResumeAsync(workspace, projectId, id, token);
    }

    public async Task<Result<WorkTaskDeliveryOperation>> GetAsync(WorkspaceId workspace, WorkProjectId project, WorkTaskDeliveryId id, CancellationToken token)
    {
        var owned = await projects.GetAsync(workspace, project, token);
        if (!owned.IsSuccess) return Failure(owned.ErrorCode!);
        var value = await repository.GetAsync(workspace, context.PrincipalId, project, id, token);
        return value is null ? Failure("coding_delivery_not_found") : Result<WorkTaskDeliveryOperation>.Success(value);
    }
    public async Task<Result<WorkTaskDeliveryPage>> ListAsync(WorkspaceId workspace, WorkProjectId project, int skip, int take, CancellationToken token)
    {
        var owned = await projects.GetAsync(workspace, project, token);
        if (!owned.IsSuccess) return Result<WorkTaskDeliveryPage>.Failure(owned.ErrorCode!);
        if (skip < 0 || take is < 1 or > 100) return Result<WorkTaskDeliveryPage>.Failure("coding_delivery_page_invalid");
        return Result<WorkTaskDeliveryPage>.Success(await repository.ListAsync(workspace, context.PrincipalId, project, skip, take, token));
    }
    private static bool SameCommand(WorkTaskDeliveryOperation value, BeginCodingDeliveryRequest command) => value.ProjectVersion == command.ExpectedProjectVersion
        && value.Intent.PlanId.Value == command.PlanId && value.Intent.Source.AttemptId == command.AttemptId && value.Intent.PlanVersion == command.ExpectedPlanVersion
        && value.Intent.TaskVersion == command.ExpectedTaskVersion && value.Intent.SharedPlanVersion == command.ExpectedSharedPlanVersion
        && value.Intent.SharedPlanDigest == command.ExpectedSharedPlanDigest && value.Intent.Title == command.Title.Trim() && value.Intent.Body == command.Body
        && value.Intent.Issue.ConnectionName == command.TrackingConnection && value.Intent.ForgeConnection == command.ForgeConnection
        && value.Intent.CodePublicationConnection == command.CodePublicationConnection && value.Intent.SharingConnection == command.SharingConnection;
    private static WorkProjectContextBinding? Binding(WorkProjectContextSnapshot context, WorkProjectBindingPurpose purpose) =>
        context.Bindings.SingleOrDefault(value => value.Binding.Purpose == purpose)?.Binding;
    private static bool SameBindings(WorkProjectSnapshot project, WorkProjectContextSnapshot captured) => project.Definition.Sharing == captured.Sharing
        && captured.Bindings.Where(value => value.Binding.Purpose is WorkProjectBindingPurpose.Repository or WorkProjectBindingPurpose.Sharing
            or WorkProjectBindingPurpose.TaskOperations or WorkProjectBindingPurpose.CodePublication).All(value => project.Definition.Bindings.Contains(value.Binding))
        && captured.Bindings.Where(value => value.Binding.Connection is not null).All(value => WorkProjectConnectionRules.Contains(project.Definition, value.Binding.Connection!));
    private static string Key(WorkTaskDeliveryOperation operation, string kind) => WorkPlanDefinition.Digest(new
    { Kind = kind, operation.WorkspaceId, operation.OwnerPrincipalId, operation.ProjectId, operation.Id });
    private static Guid Identifier(WorkTaskDeliveryOperation operation, string kind) => new(Convert.FromHexString(Key(operation, kind)).AsSpan(0, 16));
    private static Result<WorkTaskDeliveryOperation> Failure(string code) => Result<WorkTaskDeliveryOperation>.Failure(code);
}
