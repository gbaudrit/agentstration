using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;

namespace Agentstration.Application.Work;

public sealed record PublishWorkSharingCommand(WorkSharingOperationId Id, long ExpectedProjectVersion, IReadOnlyList<WorkSharingChange> Changes);

public sealed partial class WorkProjectSharingService(IWorkProjectSharingRepository repository, WorkProjectService projects,
    WorkPlanService plans, WorkTaskTreeService trees, IWorkProjectSharingGateway gateway, IWorkplaceContext context, TimeProvider clock, IWorkDocumentCodec documents)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public Task InitializeAsync(CancellationToken token) => repository.InitializeAsync(token);

    public async Task<Result<WorkProjectSharingState>> PullAsync(WorkspaceId workspaceId, WorkProjectId projectId, string? revision, CancellationToken token)
    {
        var connection = await ConnectAsync(workspaceId, projectId, token);
        if (!connection.IsSuccess) return Result<WorkProjectSharingState>.Failure(connection.ErrorCode!);
        var project = connection.Value!;
        var snapshot = await ReadAsync(project, revision, token);
        if (!snapshot.IsSuccess) return Result<WorkProjectSharingState>.Failure(snapshot.ErrorCode!);
        var prior = await repository.GetStateAsync(workspaceId, context.PrincipalId, projectId, token);
        var state = new WorkProjectSharingState(workspaceId, context.PrincipalId, projectId, project.Version, project.Definition.Sharing!,
            snapshot.Value!.Revision, snapshot.Value.Space, snapshot.Value.Records, (prior?.Version ?? 0) + 1, clock.GetUtcNow());
        return await repository.SaveStateAsync(state, prior?.Version ?? 0, token) ? Result<WorkProjectSharingState>.Success(state)
            : Result<WorkProjectSharingState>.Failure("sharing_state_conflict");
    }

    public async Task<Result<WorkSharingOperation>> PublishAsync(WorkspaceId workspaceId, WorkProjectId projectId, PublishWorkSharingCommand command, CancellationToken token)
    {
        var connected = await ConnectAsync(workspaceId, projectId, token);
        if (!connected.IsSuccess) return Failure(connected.ErrorCode!);
        var project = connected.Value!;
        if (command.Id.Value == Guid.Empty || command.Changes is not { Count: > 0 and <= 32 }) return Failure("sharing_command_invalid");
        var normalized = command.Changes.Select(WorkSharingContent.Normalize).ToArray();
        if (normalized.Any(value => !value.IsSuccess)) return Failure("sharing_content_invalid");
        var changes = normalized.Select(value => value.Value!).OrderBy(value => value.Key.Domain).ThenBy(value => value.Key.Name, StringComparer.Ordinal).ToArray();
        if (changes.Select(value => value.Key).Distinct().Count() != changes.Length
            || JsonSerializer.SerializeToUtf8Bytes(changes, Json).Length > 16 * 1024) return Failure("sharing_command_invalid");
        var digest = WorkPlanDefinition.Digest(new { Project = project.Definition.Sharing!.ProjectId, Changes = changes });
        var prior = await repository.GetOperationAsync(workspaceId, context.PrincipalId, projectId, command.Id, token);
        if (prior is not null) return prior.IntentDigest != digest ? Failure("sharing_command_conflict") : await ResumeAsync(workspaceId, projectId, command.Id, token);
        if (project.Version != command.ExpectedProjectVersion) return Failure("work_project_conflict");
        foreach (var change in changes.Where(value => value.Key.Domain == WorkSharingDomain.Usage))
        {
            var usage = change.Content.Deserialize<WorkSharingUsage>(Json)!;
            if (usage.Accounting is { } accounting && (accounting.ProjectId != project.Definition.Sharing!.ProjectId
                || accounting.ParticipantId != project.Definition.Sharing.ParticipantId)) return Failure("usage_identity_invalid");
        }
        var observations = new List<WorkSharingPlanObservation>();
        foreach (var change in changes.Where(value => value.Key.Domain == WorkSharingDomain.Plan))
        {
            var portable = change.Content.Deserialize<WorkSharingPlan>(Json)!;
            if (!project.Definition.PlanIds.Contains(portable.PlanId)) return Failure("work_project_plan_not_associated");
            var local = await ExportPlanAsync(workspaceId, portable.PlanId, portable.Allocation, token, portable.Issue);
            if (!local.IsSuccess || WorkPlanDefinition.Digest(JsonSerializer.SerializeToElement(local.Value!.Plan, Json)) != WorkPlanDefinition.Digest(change.Content))
                return Failure("sharing_local_plan_changed");
            observations.Add(new(portable.PlanId, local.Value!.Version));
        }
        var now = clock.GetUtcNow();
        var operation = new WorkSharingOperation(command.Id, workspaceId, context.PrincipalId, projectId, project.Version,
            project.Definition.Sharing!, Binding(project), changes, digest, WorkSharingOperationStatus.Pending, 1, now, now, [])
        { Plans = observations };
        var saved = await repository.AdmitAsync(operation, token);
        if (saved != WorkSharingSaveStatus.Saved)
        {
            prior = await repository.GetOperationAsync(workspaceId, context.PrincipalId, projectId, command.Id, token);
            if (prior is null || prior.IntentDigest != digest) return Failure(saved == WorkSharingSaveStatus.WorkChanged ? "sharing_local_work_changed" : "sharing_command_conflict");
        }
        return await ResumeAsync(workspaceId, projectId, command.Id, token);
    }

    public async Task<Result<WorkSharingOperation>> ResumeAsync(WorkspaceId workspaceId, WorkProjectId projectId, WorkSharingOperationId id, CancellationToken token)
    {
        using var activity = WorkItemService.ActivitySource.StartActivity("work.project-sharing.resume");
        activity?.SetTag("workspace.id", workspaceId.ToString()); activity?.SetTag("work.project.id", projectId.ToString()); activity?.SetTag("work.sharing.operation.id", id.ToString());
        var connected = await ConnectAsync(workspaceId, projectId, token);
        if (!connected.IsSuccess) return Failure(connected.ErrorCode!);
        var project = connected.Value!;
        var operation = await repository.GetOperationAsync(workspaceId, context.PrincipalId, projectId, id, token);
        if (operation is null) return Failure("sharing_operation_not_found");
        if (operation.ProjectVersion != project.Version || operation.Connection != project.Definition.Sharing
            || WorkPlanDefinition.Digest(operation.Binding) != WorkPlanDefinition.Digest(Binding(project))) return Failure("sharing_connection_changed");
        if (operation.Status is WorkSharingOperationStatus.Succeeded or WorkSharingOperationStatus.Conflict or WorkSharingOperationStatus.Rejected)
            return Result<WorkSharingOperation>.Success(operation);
        // A prior attempt must be recovered before another native publication can be prepared.
        var recover = operation.Attempts.Count > 0;
        for (var iteration = 0; iteration < 9; iteration++)
        {
            if (operation.Attempts.Count == 0 || !recover)
            {
                var snapshot = await ReadAsync(project, null, token);
                if (!snapshot.IsSuccess) return Failure(snapshot.ErrorCode!);
                var prepared = Prepare(operation, snapshot.Value!);
                if (!prepared.IsSuccess) return await SaveAsync(operation, WorkSharingOperationStatus.Conflict, null, prepared.ErrorCode, token);
                if (prepared.Value!.Records.Count == 0) return await SaveAsync(operation, WorkSharingOperationStatus.Succeeded, snapshot.Value!.Revision, null, token);
                if (operation.Attempts.Count >= 8) return await SaveAsync(operation, WorkSharingOperationStatus.Conflict, null, "sharing_retry_limit", token);
                var next = operation with
                {
                    Attempts = [.. operation.Attempts, prepared.Value],
                    Version = operation.Version + 1,
                    UpdatedAt = clock.GetUtcNow(),
                    Status = WorkSharingOperationStatus.Pending,
                    ErrorCode = null
                };
                if (!await repository.UpdateOperationAsync(next, operation.Version, token)) return Failure("sharing_operation_conflict");
                operation = next;
            }
            var attempt = operation.Attempts[^1];
            var result = await gateway.PublishAsync(workspaceId, context.PrincipalId, operation.Binding, operation.Connection, attempt, recover, token);
            if (result.OperationKey != attempt.OperationKey || result.IntentDigest != attempt.IntentDigest || !Enum.IsDefined(result.Status))
                return await SaveAsync(operation, WorkSharingOperationStatus.Uncertain, null, "sharing_provider_response_invalid", token);
            if (result.Status == WorkSharingProviderStatus.Missing && recover)
                result = await gateway.PublishAsync(workspaceId, context.PrincipalId, operation.Binding, operation.Connection, attempt, false, token);
            if (result.OperationKey != attempt.OperationKey || result.IntentDigest != attempt.IntentDigest || !Enum.IsDefined(result.Status))
                return await SaveAsync(operation, WorkSharingOperationStatus.Uncertain, null, "sharing_provider_response_invalid", token);
            if (result.Status == WorkSharingProviderStatus.Conflict) { recover = false; continue; }
            var status = result.Status switch
            {
                WorkSharingProviderStatus.Succeeded when !string.IsNullOrWhiteSpace(result.Revision) => WorkSharingOperationStatus.Succeeded,
                WorkSharingProviderStatus.Rejected => WorkSharingOperationStatus.Rejected,
                _ => WorkSharingOperationStatus.Uncertain
            };
            return await SaveAsync(operation, status, result.Revision, result.ErrorCode, token);
        }
        return await SaveAsync(operation, WorkSharingOperationStatus.Conflict, null, "sharing_retry_limit", token);
    }

    public async Task<Result<WorkSharingOperation>> GetOperationAsync(WorkspaceId workspaceId, WorkProjectId projectId, WorkSharingOperationId id, CancellationToken token)
    {
        var project = await projects.GetAsync(workspaceId, projectId, token);
        if (!project.IsSuccess) return Failure(project.ErrorCode!);
        var value = await repository.GetOperationAsync(workspaceId, context.PrincipalId, projectId, id, token);
        return value is null ? Failure("sharing_operation_not_found") : Result<WorkSharingOperation>.Success(value);
    }
    public async Task<Result<WorkSharingOperationPage>> ListOperationsAsync(WorkspaceId workspaceId, WorkProjectId projectId, int skip, int take, CancellationToken token)
    {
        var project = await projects.GetAsync(workspaceId, projectId, token);
        if (!project.IsSuccess) return Result<WorkSharingOperationPage>.Failure(project.ErrorCode!);
        if (skip < 0 || take is < 1 or > 100) return Result<WorkSharingOperationPage>.Failure("sharing_page_invalid");
        var items = await repository.ListOperationsAsync(workspaceId, context.PrincipalId, projectId, skip, take + 1, token);
        return Result<WorkSharingOperationPage>.Success(new(items.Take(take).ToArray(), items.Count > take, skip, take));
    }

    private async Task<Result<WorkProjectSnapshot>> ConnectAsync(WorkspaceId workspaceId, WorkProjectId projectId, CancellationToken token)
    {
        var loaded = await projects.GetAsync(workspaceId, projectId, token);
        if (!loaded.IsSuccess) return loaded;
        var project = loaded.Value!;
        if (project.Definition.Sharing is not { } connection) return Result<WorkProjectSnapshot>.Failure("sharing_connection_required");
        var capabilities = await gateway.DiscoverAsync(workspaceId, context.PrincipalId, Binding(project), connection, token);
        return capabilities.Value is
        {
            Version: WorkSharingFormat.Version, Read: true, Write: true, ExactRevisions: true,
            CompareAndSwap: true, History: true, IdempotentOperations: true, Recovery: true
        } && capabilities.IsSuccess
            ? loaded : Result<WorkProjectSnapshot>.Failure(capabilities.ErrorCode ?? "sharing_capability_missing");
    }
    private async Task<Result<WorkSharingSnapshot>> ReadAsync(WorkProjectSnapshot project, string? revision, CancellationToken token)
    {
        var result = await gateway.ReadAsync(project.WorkspaceId, context.PrincipalId, Binding(project), project.Definition.Sharing!, revision, token);
        if (!result.IsSuccess) return result;
        var snapshot = result.Value!;
        if (snapshot.Space is null || !WorkSharingFormat.Validate(snapshot.Space).IsSuccess || snapshot.Space.ProjectId != project.Definition.Sharing!.ProjectId
            || snapshot.Records is null || snapshot.Records.Any(record => record is null) || snapshot.Records.Count != snapshot.Space.Records.Count
            || snapshot.Records.Select(record => record.Key).Distinct().Count() != snapshot.Records.Count
            || snapshot.Records.Any(record => record is null || !WorkSharingFormat.Validate(record).IsSuccess
                || !WorkSharingContent.Normalize(new(record.Key, record.Version - 1, record.Content)).IsSuccess
                || !snapshot.Space.Records.Any(entry => entry.Key == record.Key && entry.Version == record.Version && entry.Digest == record.Digest))
            || snapshot.Records.Where(record => record.Key.Domain == WorkSharingDomain.Usage).Any(record =>
                record.Content.Deserialize<WorkSharingUsage>(Json)?.Accounting is { } accounting
                    && (accounting.ProjectId != snapshot.Space.ProjectId || accounting.ParticipantId != record.ParticipantId))
            || JsonSerializer.SerializeToUtf8Bytes(snapshot, Json).Length > WorkSharingFormat.MaximumSnapshotBytes
            || snapshot.Revision?.Length is > 256 || revision is not null && snapshot.Revision != revision)
            return Result<WorkSharingSnapshot>.Failure("sharing_snapshot_invalid");
        var prior = await repository.GetStateAsync(project.WorkspaceId, context.PrincipalId, project.Id, token);
        if (revision is null && prior?.Connection == project.Definition.Sharing && prior.Records.Count > 0 && snapshot.Records.Count == 0)
            return Result<WorkSharingSnapshot>.Failure("sharing_space_reset");
        return result;
    }
    private Result<WorkSharingTransportAttempt> Prepare(WorkSharingOperation operation, WorkSharingSnapshot snapshot)
    {
        var entries = snapshot.Space.Records.ToDictionary(entry => entry.Key);
        var records = new List<WorkSharingRecord>();
        foreach (var change in operation.Changes)
        {
            entries.TryGetValue(change.Key, out var prior);
            var digest = WorkPlanDefinition.Digest(change.Content);
            if (change.Key.Domain == WorkSharingDomain.Plan && prior is not null)
            {
                var previousPlan = snapshot.Records.Single(record => record.Key == change.Key).Content.Deserialize<WorkSharingPlan>(Json)!;
                var proposedPlan = change.Content.Deserialize<WorkSharingPlan>(Json)!;
                if (previousPlan.Issue is not null && previousPlan.Issue != proposedPlan.Issue)
                    return Result<WorkSharingTransportAttempt>.Failure("sharing_issue_association_immutable");
            }
            if (prior?.Digest == digest) continue;
            if ((prior?.Version ?? 0) != change.ExpectedVersion
                || prior is not null && change.Key.Domain is WorkSharingDomain.Contribution or WorkSharingDomain.Usage)
                return Result<WorkSharingTransportAttempt>.Failure("sharing_record_conflict");
            var record = new WorkSharingRecord(change.Key, change.ExpectedVersion + 1, change.Content, digest, operation.Connection.ParticipantId, operation.CreatedAt);
            records.Add(record); entries[record.Key] = new(record.Key, record.Version, record.Digest, documents.Write(record).Length);
        }
        var merged = snapshot.Records.ToDictionary(record => record.Key);
        foreach (var record in records) merged[record.Key] = record;
        var issuePlans = merged.Values.Where(record => record.Key.Domain == WorkSharingDomain.Plan)
            .Select(record => record.Content.Deserialize<WorkSharingPlan>(Json)!).Where(plan => plan.Issue is not null);
        if (issuePlans.GroupBy(plan => (plan.Issue!.Provider, Repository: plan.Issue.Repository.ToUpperInvariant(), plan.Issue.TaskId))
            .Any(group => group.Select(plan => plan.PlanId).Distinct().Count() > 1))
            return Result<WorkSharingTransportAttempt>.Failure("sharing_issue_plan_conflict");
        var space = snapshot.Space with { Records = entries.Values.OrderBy(value => value.Key.Domain).ThenBy(value => value.Key.Name, StringComparer.Ordinal).ToArray() };
        if (!WorkSharingFormat.Validate(space).IsSuccess || entries.Values.Sum(entry => (long)entry.SizeBytes) > WorkSharingFormat.MaximumSnapshotBytes - 64 * 1024)
            return Result<WorkSharingTransportAttempt>.Failure("sharing_space_limit");
        if (JsonSerializer.SerializeToUtf8Bytes(new { Space = space, Records = records }, Json).Length > 40 * 1024)
            return Result<WorkSharingTransportAttempt>.Failure("sharing_publication_limit");
        var number = operation.Attempts.Count + 1;
        var key = WorkPlanDefinition.Digest(new { operation.Id, operation.IntentDigest, Attempt = number });
        var intent = WorkPlanDefinition.Digest(new { operation.Connection.ProjectId, operation.Connection.Location, snapshot.Revision, Space = space, Records = records });
        return Result<WorkSharingTransportAttempt>.Success(new(number, key, intent, snapshot.Revision, space, records));
    }
    private async Task<Result<WorkSharingOperation>> SaveAsync(WorkSharingOperation operation, WorkSharingOperationStatus status, string? revision, string? error, CancellationToken token)
    {
        var updated = operation with { Version = operation.Version + 1, Status = status, RemoteRevision = revision, ErrorCode = error, UpdatedAt = clock.GetUtcNow() };
        return await repository.UpdateOperationAsync(updated, operation.Version, token) ? Result<WorkSharingOperation>.Success(updated) : Failure("sharing_operation_conflict");
    }
    private static WorkProjectContextBinding Binding(WorkProjectSnapshot project) => project.Definition.Bindings.Single(binding => binding.Name == project.Definition.Sharing!.BindingName);
    private static Result<WorkSharingOperation> Failure(string code) => Result<WorkSharingOperation>.Failure(code);
}
public sealed record WorkSharingOperationPage(IReadOnlyList<WorkSharingOperation> Items, bool HasMore, int Skip, int Take);
