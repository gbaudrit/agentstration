using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;
using Microsoft.Extensions.Logging;

namespace Agentstration.Application.Work;

public interface IWorkTaskProviderGateway
{
    Task<Result<WorkTaskProviderRoute>> ResolveAsync(WorkspaceId workspaceId, Guid principalId,
        WorkProjectContextBinding binding, ExternalTaskOperationKind operation, CancellationToken cancellationToken);
    Task<Result<WorkTaskProviderRoute>> PrepareAsync(WorkTaskProviderRoute route, ExternalTaskOperationInput input, CancellationToken cancellationToken)
        => Task.FromResult(Result<WorkTaskProviderRoute>.Success(route));
    Task<ExternalTaskProviderResponse> InvokeAsync(WorkTaskOperation operation, bool recover, CancellationToken cancellationToken);
}

public sealed record BeginWorkTaskOperation(WorkTaskOperationId Id, long ExpectedProjectVersion, string BindingName,
    ExternalTaskOperationInput Input, WorkPlanId? PlanId = null, long? PlanRevision = null, string? PlanDigest = null);

public sealed class WorkTaskProviderService(IWorkTaskOperationRepository operations, IWorkProjectRepository projects,
    WorkPlanService plans, IWorkTaskProviderGateway provider, IWorkplaceContext context,
    IEnumerable<IWorkExecutionScopeAccessor> scopes, TimeProvider clock, ILogger<WorkTaskProviderService> logger)
{
    public Task InitializeAsync(CancellationToken token) => operations.InitializeAsync(token);

    public async Task<Result<WorkTaskProviderRoute>> DiscoverAsync(WorkspaceId workspaceId, WorkProjectId projectId, string bindingName,
        ExternalTaskOperationKind operation, CancellationToken token)
    {
        var project = Owns(workspaceId) ? await projects.GetAsync(workspaceId, context.PrincipalId, projectId, token) : null;
        if (project is null) return Result<WorkTaskProviderRoute>.Failure("work_project_not_found");
        var binding = project.Definition.Bindings.SingleOrDefault(value => value.Name == bindingName && value.Purpose is WorkProjectBindingPurpose.TaskOperations or WorkProjectBindingPurpose.Tracking);
        return binding is null ? Result<WorkTaskProviderRoute>.Failure("task_provider_binding_missing")
            : await provider.ResolveAsync(workspaceId, context.PrincipalId, binding, operation, token);
    }

    public async Task<Result<WorkTaskOperation>> BeginAsync(WorkspaceId workspaceId, WorkProjectId projectId,
        BeginWorkTaskOperation command, CancellationToken token)
    {
        var scope = WorkExecutionScopeSelection.Select(scopes);
        if (!Owns(workspaceId) || scope is null || scope.WorkspaceId != workspaceId || scope.PrincipalId != context.PrincipalId
            || scope.TenantId == Guid.Empty) return Failure("task_provider_operation_not_found");
        if (command.Id.Value == Guid.Empty || command.ExpectedProjectVersion < 1 || !WorkProjectDefinition.ValidName(command.BindingName)
            || command.Input is null || !command.Input.Validate().IsSuccess
            || (command.PlanId is null ? command.PlanRevision is not null || command.PlanDigest is not null
                : command.PlanId.Value.Value == Guid.Empty || command.PlanRevision is not >= 1 || command.PlanDigest?.Length != 64))
            return Failure("task_provider_command_invalid");
        var digest = WorkPlanDefinition.Digest(new
        {
            projectId,
            command.ExpectedProjectVersion,
            command.BindingName,
            command.Input,
            command.PlanId,
            command.PlanRevision,
            command.PlanDigest
        });
        var existing = await operations.GetAsync(workspaceId, context.PrincipalId, command.Id, token);
        if (existing is not null)
            return existing.ProjectId != projectId || existing.IntentDigest != digest ? Failure("task_provider_operation_conflict") : await DriveAsync(existing, true, token);
        var project = await projects.GetAsync(workspaceId, context.PrincipalId, projectId, token);
        if (project is null) return Failure("work_project_not_found");
        if (project.Version != command.ExpectedProjectVersion) return Failure("work_project_conflict");
        var binding = project.Definition.Bindings.SingleOrDefault(value => value.Name == command.BindingName && value.Purpose is WorkProjectBindingPurpose.TaskOperations or WorkProjectBindingPurpose.Tracking);
        if (binding is null) return Failure("task_provider_binding_missing");
        WorkProjectPlanObservation? planObservation = null;
        if (command.PlanId is { } planId)
        {
            if (!project.Definition.PlanIds.Contains(planId)) return Failure("work_project_plan_not_associated");
            var plan = await plans.GetAsync(workspaceId, planId, token);
            if (!plan.IsSuccess) return Failure(plan.ErrorCode!);
            if (plan.Value!.Revision.Number != command.PlanRevision || plan.Value.Revision.Digest != command.PlanDigest)
                return Failure("work_plan_revision_conflict");
            planObservation = new(planId, plan.Value.Plan.Version);
        }
        var route = await provider.ResolveAsync(workspaceId, context.PrincipalId, binding, command.Input.Operation, token);
        if (!route.IsSuccess) return Failure(route.ErrorCode!);
        route = await provider.PrepareAsync(route.Value!, command.Input, token);
        if (!route.IsSuccess) return Failure(route.ErrorCode!);
        var now = clock.GetUtcNow();
        var operationKey = WorkPlanDefinition.Digest(new { scope.TenantId, workspaceId, context.PrincipalId, command.Id });
        var operation = new WorkTaskOperation(command.Id, workspaceId, context.PrincipalId, scope.TenantId, projectId, project.Version,
            command.PlanId, command.PlanRevision, command.PlanDigest, command.Input, digest, operationKey, route.Value!,
            WorkTaskOperationStatus.Pending, 1, now, now);
        var saved = await operations.TryCreateAsync(new(operation, project.Version, planObservation), token);
        if (saved == WorkTaskOperationSaveStatus.WorkChanged) return Failure("task_provider_work_changed");
        if (saved == WorkTaskOperationSaveStatus.Conflict)
        {
            existing = await operations.GetAsync(workspaceId, context.PrincipalId, command.Id, token);
            return existing is null || existing.IntentDigest != digest || existing.ProjectId != projectId ? Failure("task_provider_operation_conflict")
                : await DriveAsync(existing, true, token);
        }
        return await DriveAsync(operation, false, token);
    }

    public async Task<Result<WorkTaskOperation>> ResumeAsync(WorkspaceId workspaceId, WorkProjectId projectId, WorkTaskOperationId id, CancellationToken token)
    {
        var existing = Owns(workspaceId) ? await operations.GetAsync(workspaceId, context.PrincipalId, id, token) : null;
        return existing is null || existing.ProjectId != projectId ? Failure("task_provider_operation_not_found") : await DriveAsync(existing, true, token);
    }

    public async Task<Result<WorkTaskOperation>> GetAsync(WorkspaceId workspaceId, WorkProjectId projectId, WorkTaskOperationId id, CancellationToken token)
    {
        var existing = Owns(workspaceId) ? await operations.GetAsync(workspaceId, context.PrincipalId, id, token) : null;
        return existing is null || existing.ProjectId != projectId ? Failure("task_provider_operation_not_found") : Result<WorkTaskOperation>.Success(existing);
    }

    public async Task<Result<WorkTaskOperationPage>> ListAsync(WorkspaceId workspaceId, WorkProjectId projectId, int skip, int take, CancellationToken token)
    {
        if (!Owns(workspaceId)) return Result<WorkTaskOperationPage>.Failure("work_project_not_found");
        if (skip < 0 || take is < 1 or > 100) return Result<WorkTaskOperationPage>.Failure("task_provider_page_invalid");
        if (await projects.GetAsync(workspaceId, context.PrincipalId, projectId, token) is null) return Result<WorkTaskOperationPage>.Failure("work_project_not_found");
        return Result<WorkTaskOperationPage>.Success(await operations.ListAsync(workspaceId, context.PrincipalId, projectId, skip, take, token));
    }

    private async Task<Result<WorkTaskOperation>> DriveAsync(WorkTaskOperation operation, bool recover, CancellationToken token)
    {
        if (operation.Status is WorkTaskOperationStatus.Succeeded or WorkTaskOperationStatus.Rejected) return Result<WorkTaskOperation>.Success(operation);
        var project = await projects.GetAsync(operation.WorkspaceId, context.PrincipalId, operation.ProjectId, token);
        if (project?.Definition.Bindings.Any(binding => WorkPlanDefinition.Digest(binding) == WorkPlanDefinition.Digest(operation.Route.Binding)) != true) return Failure("task_provider_binding_changed");
        var route = await provider.ResolveAsync(operation.WorkspaceId, context.PrincipalId, operation.Route.Binding, operation.Input.Operation, token);
        if (!route.IsSuccess) return Failure(route.ErrorCode!);
        if (route.Value!.Tool != operation.Route.Tool || route.Value.Provider != operation.Route.Provider
            || WorkPlanDefinition.Digest(route.Value.Tracking is { } fresh ? fresh with { WireIntentDigest = null } : null)
                != WorkPlanDefinition.Digest(operation.Route.Tracking is { } captured ? captured with { WireIntentDigest = null } : null)) return Failure("task_provider_route_changed");
        ExternalTaskProviderResponse response;
        try
        {
            token.ThrowIfCancellationRequested();
            response = await provider.InvokeAsync(operation, recover, token);
            if (response.Status == ExternalTaskProviderStatus.Missing && recover
                && response.OperationKey == operation.OperationKey && response.IntentDigest == operation.IntentDigest
                && response.Task is null && response.ErrorCode is null
                && operation.Route.Capabilities.IdempotentOperations && operation.Route.Capabilities.OperationRecovery)
            {
                // A missing receipt permits a retry only under the same idempotent provider identity.
                // A newer local plan revision must not publish an obsolete intent.
                if (operation.PlanId is { } planId)
                {
                    var plan = await plans.GetAsync(operation.WorkspaceId, planId, token);
                    if (!plan.IsSuccess || !project.Definition.PlanIds.Contains(planId)
                        || plan.Value!.Revision.Number != operation.PlanRevision || plan.Value.Revision.Digest != operation.PlanDigest)
                        return Failure("task_provider_work_changed");
                }
                response = await provider.InvokeAsync(operation, false, token);
            }
        }
        catch (OperationCanceledException)
        {
            await PersistAsync(operation, WorkTaskOperationStatus.Uncertain, null, "task_provider_cancelled", CancellationToken.None);
            throw;
        }
        var valid = response.OperationKey == operation.OperationKey && response.IntentDigest == operation.IntentDigest
            && Enum.IsDefined(response.Status) && (response.ErrorCode is null || response.ErrorCode is { Length: > 0 and <= 128 }
                && response.ErrorCode.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character is '_' or '-' or '.'))
            && (response.Status != ExternalTaskProviderStatus.Succeeded || ValidSnapshot(response.Task, operation.Input));
        var status = valid ? response.Status switch
        {
            ExternalTaskProviderStatus.Succeeded => WorkTaskOperationStatus.Succeeded,
            ExternalTaskProviderStatus.Rejected => WorkTaskOperationStatus.Rejected,
            _ => WorkTaskOperationStatus.Uncertain
        } : WorkTaskOperationStatus.Uncertain;
        return await PersistAsync(operation, status, status == WorkTaskOperationStatus.Succeeded ? response.Task : null,
            valid ? status == WorkTaskOperationStatus.Succeeded ? null : response.ErrorCode ?? (status == WorkTaskOperationStatus.Rejected
                ? "task_provider_operation_rejected" : "task_provider_outcome_uncertain") : "task_provider_response_invalid", token);
    }

    private async Task<Result<WorkTaskOperation>> PersistAsync(WorkTaskOperation before, WorkTaskOperationStatus status,
        ExternalTaskSnapshot? task, string? code, CancellationToken token)
    {
        var changed = before with { Version = before.Version + 1, Status = status, Task = task, ErrorCode = code, UpdatedAt = clock.GetUtcNow() };
        if (!await operations.TryUpdateAsync(changed, before.Version, token))
        {
            var current = await operations.GetAsync(before.WorkspaceId, before.OwnerPrincipalId, before.Id, token);
            return current is not null && current.IntentDigest == before.IntentDigest
                ? Result<WorkTaskOperation>.Success(current) : Failure("task_provider_operation_conflict");
        }
        if (logger.IsEnabled(LogLevel.Information)) logger.LogInformation("Task provider operation {OperationId} for Project {ProjectId} in Workspace {WorkspaceId} is {Status}",
            changed.Id.Value, changed.ProjectId.Value, changed.WorkspaceId.Value, status);
        return Result<WorkTaskOperation>.Success(changed);
    }

    private static bool ValidSnapshot(ExternalTaskSnapshot? task, ExternalTaskOperationInput input) => task is not null
        && (input.Tracking is null ? task.Tracking is null : task.Tracking is { RelationsComplete: true } observation
            && observation.Identity.ProviderId == input.Tracking.ProviderId && observation.Identity.TargetId == input.Tracking.TargetId
            && observation.Identity.ItemId == task.TaskId)
        && task.Repository == input.Repository && (input.Operation == ExternalTaskOperationKind.Publish || task.TaskId == input.TaskId)
        && task.TaskId is { Length: > 0 and <= 256 } && task.Revision is { Length: > 0 and <= 256 }
        && task.Title is { Length: > 0 and <= 512 } && task.Body?.Length is not > 32_768 && task.State is { Length: > 0 and <= 128 }
        && task.CompletionReason?.Length is not > 128 && task.CompletionReason?.Any(char.IsControl) != true
        && task.Labels is { Count: <= 32 } && task.Labels.All(value => value is { Length: > 0 and <= 128 })
        && task.Relations is { Count: <= 100 } && task.Relations.All(value => value is not null && value.Kind is { Length: > 0 and <= 64 }
            && value.Repository is { Length: > 0 and <= 512 } && value.TaskId is { Length: > 0 and <= 256 })
        && (task.Url is null || task.Url.IsAbsoluteUri && task.Url.Scheme is "https" or "http" && string.IsNullOrEmpty(task.Url.UserInfo));
    private bool Owns(WorkspaceId workspaceId) => workspaceId.Value != Guid.Empty && workspaceId == context.WorkspaceId && context.PrincipalId != Guid.Empty;
    private static Result<WorkTaskOperation> Failure(string code) => Result<WorkTaskOperation>.Failure(code);
}
