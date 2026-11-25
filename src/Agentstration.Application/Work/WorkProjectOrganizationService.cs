using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;

namespace Agentstration.Application.Work;

public sealed record UpdateWorkOrganizationCommand(Guid Id, long ExpectedVersion, WorkOrganizationDefinition Definition);

public sealed class WorkProjectOrganizationService(IWorkDataRepository repository, WorkProjectService projects,
    WorkProjectSharingService sharing, IWorkplaceContext context, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<Result<WorkOrganizationSnapshot>> GetAsync(WorkspaceId workspace, WorkProjectId projectId, CancellationToken token)
    {
        var project = await projects.GetAsync(workspace, projectId, token);
        if (!project.IsSuccess) return Failure(project.ErrorCode!);
        return Result<WorkOrganizationSnapshot>.Success(await repository.GetOrganizationAsync(workspace, context.PrincipalId, projectId, token)
            ?? new(workspace, context.PrincipalId, projectId, new([]), 0, 0, DateTimeOffset.UnixEpoch, []));
    }

    public async Task<Result<WorkOrganizationSnapshot>> UpdateAsync(WorkspaceId workspace, WorkProjectId projectId, UpdateWorkOrganizationCommand command, CancellationToken token)
    {
        var loaded = await GetAsync(workspace, projectId, token);
        if (!loaded.IsSuccess) return loaded;
        if (command.Id == Guid.Empty || command.ExpectedVersion < 0 || command.Definition is null) return Failure("work_organization_command_invalid");
        var normalized = command.Definition.Validate();
        if (!normalized.IsSuccess) return Failure(normalized.ErrorCode!);
        var current = loaded.Value!;
        var digest = WorkPlanDefinition.Digest(normalized.Value);
        var receipt = current.Commands.SingleOrDefault(value => value.Id == command.Id);
        if (receipt is not null) return receipt.Digest == digest ? loaded : Failure("work_organization_command_conflict");
        if (current.Version != command.ExpectedVersion) return Failure("work_organization_conflict");
        if (current.Commands.Count >= 256) return Failure("work_organization_history_limit");
        var project = await projects.GetAsync(workspace, projectId, token);
        if (!project.IsSuccess) return Failure(project.ErrorCode!);
        if (normalized.Value!.Epics.SelectMany(value => value.PlanIds).Any(id => !project.Value!.Definition.PlanIds.Contains(id)))
            return Failure("work_organization_plan_not_associated");
        var changed = WorkPlanDefinition.Digest(current.Definition) != digest;
        var version = current.Version + (changed ? 1 : 0);
        var now = clock.GetUtcNow();
        var value = current with { Definition = normalized.Value, Version = version, StorageVersion = current.StorageVersion + 1,
            UpdatedAt = changed ? now : current.UpdatedAt, Commands = [.. current.Commands, new(command.Id, digest, version, now)] };
        if (await repository.SaveOrganizationAsync(value, current.StorageVersion, project.Value!.Version, token)) return Result<WorkOrganizationSnapshot>.Success(value);
        var concurrent = await GetAsync(workspace, projectId, token);
        var replay = concurrent.Value?.Commands.SingleOrDefault(item => item.Id == command.Id);
        return replay?.Digest == digest ? concurrent : Failure("work_organization_conflict");
    }

    public async Task<Result<WorkSharingOperation>> PublishAsync(WorkspaceId workspace, WorkProjectId projectId,
        WorkSharingOperationId id, long expectedOrganizationVersion, long expectedRecordVersion, CancellationToken token)
    {
        var organization = await GetAsync(workspace, projectId, token);
        if (!organization.IsSuccess) return Result<WorkSharingOperation>.Failure(organization.ErrorCode!);
        if (organization.Value!.Version != expectedOrganizationVersion) return Result<WorkSharingOperation>.Failure("work_organization_conflict");
        var project = await projects.GetAsync(workspace, projectId, token);
        if (!project.IsSuccess) return Result<WorkSharingOperation>.Failure(project.ErrorCode!);
        return await sharing.PublishAsync(workspace, projectId, new(id, project.Value!.Version,
            [new(new(WorkSharingDomain.Organization, "epics"), expectedRecordVersion, JsonSerializer.SerializeToElement(organization.Value.Definition, Json))]), token);
    }

    public async Task<Result<WorkOrganizationSnapshot>> ImportSharedAsync(WorkspaceId workspace, WorkProjectId projectId,
        Guid id, long expectedVersion, string expectedDigest, CancellationToken token)
    {
        if (!WorkSharingFormat.Digest(expectedDigest)) return Failure("work_organization_command_invalid");
        var pulled = await sharing.PullAsync(workspace, projectId, null, token);
        if (!pulled.IsSuccess) return Failure(pulled.ErrorCode!);
        var record = pulled.Value!.Records.SingleOrDefault(value => value.Key == new WorkSharingRecordKey(WorkSharingDomain.Organization, "epics"));
        if (record?.Digest != expectedDigest) return Failure("sharing_record_conflict");
        return await UpdateAsync(workspace, projectId, new(id, expectedVersion, record.Content.Deserialize<WorkOrganizationDefinition>(Json)!), token);
    }

    private static Result<WorkOrganizationSnapshot> Failure(string code) => Result<WorkOrganizationSnapshot>.Failure(code);
}
