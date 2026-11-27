using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;
using Microsoft.Extensions.Logging;

namespace Agentstration.Application.Work;

public sealed record SaveWorkProjectCommand(Guid CommandId, long ExpectedVersion, WorkProjectDefinition Definition);

public sealed class WorkProjectService(IWorkProjectRepository projects, IWorkPlanRepository plans,
    IWorkItemRepository tasks, WorkProjectContextService bindings, IWorkplaceContext context, TimeProvider clock, ILogger<WorkProjectService> logger)
{
    public Task InitializeAsync(CancellationToken cancellationToken) => projects.InitializeAsync(cancellationToken);

    public Task<Result<WorkProjectSnapshot>> CreateAsync(WorkspaceId workspaceId, WorkProjectId id, string name, ResourceNamespace resourceNamespace,
        SaveWorkProjectCommand command, CancellationToken token) => SaveAsync(workspaceId, id, name, resourceNamespace, command, true, token);

    public Task<Result<WorkProjectSnapshot>> UpdateAsync(WorkspaceId workspaceId, WorkProjectId id,
        SaveWorkProjectCommand command, CancellationToken token) => SaveAsync(workspaceId, id, null, default, command, false, token);

    private async Task<Result<WorkProjectSnapshot>> SaveAsync(WorkspaceId workspaceId, WorkProjectId id, string? name,
        ResourceNamespace resourceNamespace, SaveWorkProjectCommand command, bool create, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!Owns(workspaceId) || id.Value == Guid.Empty) return Failure("work_project_not_found");
        if (command.CommandId == Guid.Empty || command.Definition is null || (create ? command.ExpectedVersion != 0 : command.ExpectedVersion < 1))
            return Failure("work_project_command_invalid");
        var validated = command.Definition.Validate();
        if (!validated.IsSuccess) return Failure(validated.ErrorCode!);
        var definition = validated.Value!;
        if (create && !WorkProjectDefinition.ValidName(name)) return Failure("work_project_name_invalid");
        var current = await projects.GetAsync(workspaceId, context.PrincipalId, id, token);
        if (!create && current is null) return Failure("work_project_not_found");
        name = create ? name! : current!.Name;
        resourceNamespace = create ? resourceNamespace : current!.Namespace;
        // Preserve receipt digests for pre-binding Projects whose original definition had these four fields only.
        object intentDefinition = definition.Bindings.Count == 0 && definition.ConnectionRequirements.Count == 0 && definition.ConnectionAssignments.Count == 0
            ? new { definition.DisplayName, definition.Description, definition.PlanIds, definition.TaskIds } : definition;
        var intent = WorkPlanDefinition.Digest(new { Action = create ? "create" : "update", Name = name, Namespace = resourceNamespace, Definition = intentDefinition });
        var replay = current?.Commands.SingleOrDefault(receipt => receipt.CommandId == command.CommandId);
        if (replay is not null) return replay.IntentDigest == intent ? Result<WorkProjectSnapshot>.Success(current!) : Failure("work_project_command_conflict");
        if (create ? current is not null : current!.Version != command.ExpectedVersion) return Failure("work_project_conflict");
        if (current?.Version >= WorkProjectDefinition.MaximumCommands) return Failure("work_project_history_limit");
        var resolved = await bindings.ValidateAsync(workspaceId, definition, token);
        if (!resolved.IsSuccess) return Failure(resolved.ErrorCode!);
        var work = await ObserveWorkAsync(workspaceId, definition, token);
        if (!work.IsSuccess) return Failure(work.ErrorCode!);
        var now = clock.GetUtcNow();
        var receipt = new WorkProjectCommandReceipt(command.CommandId, intent, command.ExpectedVersion + 1, now);
        var project = new WorkProjectSnapshot(id, workspaceId, context.PrincipalId, name!, resourceNamespace, definition,
            receipt.Version, current?.CreationDigest ?? intent, current?.CreatedAt ?? now, now, [.. current?.Commands ?? [], receipt]);
        var saved = await projects.TrySaveAsync(new(project, command.ExpectedVersion, receipt, work.Value!.Plans, work.Value.Tasks), token);
        if (saved is WorkProjectSaveStatus.Conflict or WorkProjectSaveStatus.WorkChanged)
        {
            current = await projects.GetAsync(workspaceId, context.PrincipalId, id, token);
            replay = current?.Commands.SingleOrDefault(value => value.CommandId == command.CommandId);
            if (replay is not null && replay.IntentDigest != intent) return Failure("work_project_command_conflict");
            return replay?.IntentDigest == intent ? Result<WorkProjectSnapshot>.Success(current!)
                : Failure(saved == WorkProjectSaveStatus.WorkChanged ? "work_project_work_changed" : "work_project_conflict");
        }
        if (logger.IsEnabled(LogLevel.Information)) logger.LogInformation("Saved Project {ProjectId} at version {Version} in Workspace {WorkspaceId}; command {CommandId}",
            id.Value, project.Version, workspaceId.Value, command.CommandId);
        return await GetAsync(workspaceId, id, token);
    }

    public async Task<Result<WorkProjectSnapshot>> GetAsync(WorkspaceId workspaceId, WorkProjectId id, CancellationToken token)
    {
        if (!Owns(workspaceId)) return Failure("work_project_not_found");
        var value = await projects.GetAsync(workspaceId, context.PrincipalId, id, token);
        return value is null ? Failure("work_project_not_found") : Result<WorkProjectSnapshot>.Success(value);
    }

    public async Task<Result<WorkProjectSnapshot>> FindAsync(WorkspaceId workspaceId, ResourceNamespace resourceNamespace, string name, CancellationToken token)
    {
        if (!Owns(workspaceId)) return Failure("work_project_not_found");
        var value = await projects.FindAsync(workspaceId, context.PrincipalId, resourceNamespace, name, token);
        return value is null ? Failure("work_project_not_found") : Result<WorkProjectSnapshot>.Success(value);
    }

    public async Task<Result<WorkProjectPage>> ListAsync(WorkspaceId workspaceId, int skip, int take, CancellationToken token)
    {
        if (!Owns(workspaceId)) return Result<WorkProjectPage>.Failure("work_project_not_found");
        if (skip < 0 || take is < 1 or > 100) return Result<WorkProjectPage>.Failure("work_project_page_invalid");
        return Result<WorkProjectPage>.Success(await projects.ListAsync(workspaceId, context.PrincipalId, skip, take, token));
    }

    public async Task<Result<WorkProjectDefinition>> ValidateAsync(WorkspaceId workspaceId, WorkProjectDefinition definition, CancellationToken token)
    {
        if (!Owns(workspaceId)) return Result<WorkProjectDefinition>.Failure("work_project_not_found");
        var valid = definition.Validate();
        if (!valid.IsSuccess) return valid;
        var resolved = await bindings.ValidateAsync(workspaceId, valid.Value!, token);
        if (!resolved.IsSuccess) return Result<WorkProjectDefinition>.Failure(resolved.ErrorCode!);
        var work = await ObserveWorkAsync(workspaceId, valid.Value!, token);
        return work.IsSuccess ? valid : Result<WorkProjectDefinition>.Failure(work.ErrorCode!);
    }

    private sealed record ObservedWork(IReadOnlyList<WorkProjectPlanObservation> Plans, IReadOnlyList<WorkPlanTaskObservation> Tasks);
    private async Task<Result<ObservedWork>> ObserveWorkAsync(WorkspaceId workspaceId, WorkProjectDefinition definition, CancellationToken token)
    {
        var observedPlans = new List<WorkProjectPlanObservation>();
        var observedTasks = new List<WorkPlanTaskObservation>();
        foreach (var planId in definition.PlanIds)
        {
            var plan = await plans.GetAsync(workspaceId, context.PrincipalId, planId, token);
            if (plan is null) return Result<ObservedWork>.Failure("work_project_work_not_found");
            observedPlans.Add(new(planId, plan.Version));
        }
        foreach (var taskId in definition.TaskIds)
        {
            var task = await tasks.GetAsync(workspaceId, context.PrincipalId, taskId.ToWorkItemId(), token);
            if (task is null || !WorkplaceTaskIdentity.IsTask(task.Value)) return Result<ObservedWork>.Failure("work_project_work_not_found");
            observedTasks.Add(new(taskId, task.Value.Version));
        }
        return Result<ObservedWork>.Success(new(observedPlans, observedTasks));
    }

    private bool Owns(WorkspaceId workspaceId) => workspaceId.Value != Guid.Empty && workspaceId == context.WorkspaceId && context.PrincipalId != Guid.Empty;
    private static Result<WorkProjectSnapshot> Failure(string code) => Result<WorkProjectSnapshot>.Failure(code);
}
