using System.Security.Cryptography;
using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;
using Microsoft.Extensions.Logging;

namespace Agentstration.Application.Work;

public sealed record CorrectWorkUsageCommand(Guid CommandId, WorkUsageId ExpectedPreviousId, string ExpectedPreviousDigest,
    WorkUsageMeasurement Measurement, WorkUsageTotals Totals, string Reason, string EvidenceReference);

public sealed class WorkUsageService(IWorkUsageRepository repository, IWorkProjectRepository projects, IWorkPlanRepository plans,
    IWorkItemRepository tasks, IWorkUsageExecutionReader executions, IWorkProjectSharingRepository sharingRepository,
    WorkProjectSharingService sharing, IWorkplaceContext context, TimeProvider clock, ILogger<WorkUsageService> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public Task InitializeAsync(CancellationToken token) => repository.InitializeAsync(token);

    public async Task<Result<WorkUsageCapture>> CaptureAsync(WorkspaceId workspace, WorkProjectId projectId, WorkPlanId planId, Guid attemptId, CancellationToken token)
    {
        if (await OwnedAsync(workspace, projectId, token) is null) return Result<WorkUsageCapture>.Failure("work_project_not_found");
        var plan = await plans.GetAsync(workspace, context.PrincipalId, planId, token);
        var attempt = plan?.Attempts.SingleOrDefault(value => value.Id == attemptId);
        if (attempt?.ProjectContext?.ProjectId != projectId || plan?.Execution?.Scope is not { } scope
            || scope.WorkspaceId != workspace || scope.PrincipalId != context.PrincipalId) return Result<WorkUsageCapture>.Failure("usage_attempt_not_found");
        if (attempt.Status is WorkPlanAttemptStatus.Admitted or WorkPlanAttemptStatus.Submitted) return Result<WorkUsageCapture>.Failure("usage_execution_not_final");
        var revision = await plans.GetRevisionAsync(workspace, context.PrincipalId, planId, attempt.PlanRevision, token);
        if (revision?.Digest != attempt.PlanDigest || !revision.Definition.TaskIds.Contains(attempt.TaskId)) return Result<WorkUsageCapture>.Failure("usage_attempt_invalid");
        var ancestors = new List<WorkTaskId>();
        var task = (await tasks.GetAsync(workspace, context.PrincipalId, attempt.TaskId.ToWorkItemId(), token))?.Value;
        if (task?.TaskHierarchy is null) return Result<WorkUsageCapture>.Failure("work_task_not_found");
        while (task.TaskHierarchy.ParentTaskId is { } parent)
        {
            if (ancestors.Count >= WorkTaskTreeDefinition.MaximumDepth || ancestors.Contains(parent)) return Result<WorkUsageCapture>.Failure("usage_hierarchy_invalid");
            ancestors.Add(parent);
            task = (await tasks.GetAsync(workspace, context.PrincipalId, parent.ToWorkItemId(), token))?.Value;
            if (task?.TaskHierarchy is null) return Result<WorkUsageCapture>.Failure("usage_hierarchy_invalid");
        }
        var read = await executions.ReadAsync(scope, projectId, planId, attempt, token);
        if (!read.IsSuccess) return Result<WorkUsageCapture>.Failure(read.ErrorCode!);
        if (read.Value is not { Count: > 0 and <= 32 }) return Result<WorkUsageCapture>.Failure("usage_source_invalid");
        var entries = new List<WorkUsageEntry>();
        foreach (var source in read.Value)
        {
            var id = new WorkUsageId(StableId(new
            {
                Kind = "work-usage/1.0",
                workspace,
                Principal = context.PrincipalId,
                projectId,
                planId,
                attemptId,
                SourceKind = source.Source.Kind,
                source.Source.AssignmentId,
                source.Source.AssignmentAttemptId,
                source.Source.InvocationId
            }));
            var record = new WorkUsageRecord(id, id, 1, planId, attempt.PlanRevision, attempt.PlanDigest, attempt.TaskId, attempt.TaskVersion, attempt.TaskDigest,
                source.Source, source.Totals.Inclusive.ReportedInvocationCount > 0 ? WorkUsageMeasurement.Measured : WorkUsageMeasurement.Unknown,
                source.Totals, attempt.AdmittedAt)
            {
                ProjectId = attempt.ProjectContext.Sharing?.ProjectId,
                ParticipantId = attempt.ProjectContext.Sharing?.ParticipantId,
                AncestorTaskIds = ancestors.ToArray(),
                Observations = source.Observations
            };
            if (!WorkUsageFormat.Valid(record) || source.Source.AttemptId != attempt.Id || source.Source.WorkItemId != attempt.WorkItemId.Value
                || source.Source.FlowRunId != attempt.FlowRunId || source.Source.Outcome != attempt.Status) return Result<WorkUsageCapture>.Failure("usage_source_invalid");
            entries.Add(new(workspace, context.PrincipalId, projectId, record, WorkUsageFormat.Digest(record), clock.GetUtcNow()));
        }
        var status = await repository.CaptureAsync(entries, token);
        if (status == WorkUsageSaveStatus.Conflict) return Result<WorkUsageCapture>.Failure("usage_capture_conflict");
        var retained = new List<WorkUsageEntry>();
        foreach (var entry in entries) retained.Add((await repository.GetAsync(workspace, context.PrincipalId, projectId, entry.Record.Id, token))!);
        if (logger.IsEnabled(LogLevel.Information)) logger.LogInformation("Captured {UsageCount} usage records for Plan {PlanId}, attempt {AttemptId}, Flow Run {FlowRunId}, Project {ProjectId} in Workspace {WorkspaceId}",
            entries.Count, planId.Value, attemptId, attempt.FlowRunId, projectId.Value, workspace.Value);
        return Result<WorkUsageCapture>.Success(new(retained, status == WorkUsageSaveStatus.Replayed));
    }

    public async Task<Result<WorkUsageEntry>> GetAsync(WorkspaceId workspace, WorkProjectId project, WorkUsageId id, CancellationToken token)
    {
        if (await OwnedAsync(workspace, project, token) is null) return Result<WorkUsageEntry>.Failure("work_project_not_found");
        var entry = await repository.GetAsync(workspace, context.PrincipalId, project, id, token);
        return entry is null ? Result<WorkUsageEntry>.Failure("usage_record_not_found") : Result<WorkUsageEntry>.Success(entry);
    }
    public async Task<Result<WorkUsagePage>> ListAsync(WorkspaceId workspace, WorkProjectId project, int skip, int take, CancellationToken token)
    {
        if (await OwnedAsync(workspace, project, token) is null) return Result<WorkUsagePage>.Failure("work_project_not_found");
        return skip < 0 || take is < 1 or > 100 ? Result<WorkUsagePage>.Failure("usage_page_invalid")
            : Result<WorkUsagePage>.Success(await repository.ListAsync(workspace, context.PrincipalId, project, skip, take, token));
    }
    public async Task<Result<WorkUsageEntry>> CorrectAsync(WorkspaceId workspace, WorkProjectId project, WorkUsageId rootId, CorrectWorkUsageCommand command, CancellationToken token)
    {
        if (await OwnedAsync(workspace, project, token) is null) return Result<WorkUsageEntry>.Failure("work_project_not_found");
        if (command is null || command.CommandId == Guid.Empty) return Result<WorkUsageEntry>.Failure("usage_correction_invalid");
        var intent = WorkPlanDefinition.Digest(new { rootId, Command = command });
        var id = new WorkUsageId(StableId(new { Kind = "work-usage-correction/1.0", workspace, Principal = context.PrincipalId, project, command.CommandId }));
        var replay = await repository.GetAsync(workspace, context.PrincipalId, project, id, token);
        if (replay is not null) return replay.Record.Correction?.IntentDigest == intent ? Result<WorkUsageEntry>.Success(replay) : Result<WorkUsageEntry>.Failure("usage_correction_conflict");
        var previous = await repository.GetAsync(workspace, context.PrincipalId, project, command.ExpectedPreviousId, token);
        if (previous is null || previous.Record.RootId != rootId || previous.Digest != command.ExpectedPreviousDigest) return Result<WorkUsageEntry>.Failure("usage_correction_conflict");
        var record = previous.Record with
        {
            Id = id,
            Revision = previous.Record.Revision + 1,
            PreviousId = previous.Record.Id,
            PreviousDigest = previous.Digest,
            Measurement = command.Measurement,
            Totals = command.Totals,
            RecordedAt = clock.GetUtcNow(),
            Correction = new(command.CommandId, intent, command.Reason, command.EvidenceReference)
        };
        // A correction may replace counters and quality, but cannot invent another execution or change its invocation denominator.
        if (!WorkUsageFormat.Valid(record) || record.Totals.Direct.InvocationCount != previous.Record.Totals.Direct.InvocationCount
            || record.Totals.Inclusive.InvocationCount != previous.Record.Totals.Inclusive.InvocationCount) return Result<WorkUsageEntry>.Failure("usage_correction_invalid");
        var entry = previous with { Record = record, Digest = WorkUsageFormat.Digest(record), CreatedAt = clock.GetUtcNow() };
        return await repository.AppendCorrectionAsync(entry, token) is WorkUsageSaveStatus.Saved or WorkUsageSaveStatus.Replayed
            ? Result<WorkUsageEntry>.Success(entry) : Result<WorkUsageEntry>.Failure("usage_correction_conflict");
    }
    public async Task<Result<WorkUsageSummary>> SummarizeAsync(WorkspaceId workspace, WorkProjectId project, WorkUsageSummaryQuery query, CancellationToken token)
    {
        var owned = await OwnedAsync(workspace, project, token);
        if (owned is null) return Result<WorkUsageSummary>.Failure("work_project_not_found");
        if (query?.PlanIds is null || query.PlanIds.Count > 100 || query.PlanIds.Any(id => id.Value == Guid.Empty) || !Enum.IsDefined(query.Source))
            return Result<WorkUsageSummary>.Failure("usage_query_invalid");
        if (query.Source == WorkUsageReadSource.Local)
        {
            var entries = await repository.ReadProjectAsync(workspace, context.PrincipalId, project, query.PlanIds.Distinct().ToArray(), WorkUsageFormat.MaximumSummaryRecords + 1, token);
            return WorkUsageAccounting.Summarize(entries.Select(value => value.Record).ToArray(), query);
        }
        var state = await sharingRepository.GetStateAsync(workspace, context.PrincipalId, project, token);
        if (state is null || state.Connection != owned.Definition.Sharing) return Result<WorkUsageSummary>.Failure("usage_shared_state_required");
        var records = new List<WorkUsageRecord>(); var legacy = 0;
        foreach (var item in state.Records.Where(value => value.Key.Domain == WorkSharingDomain.Usage))
        {
            if (!WorkSharingFormat.Validate(item).IsSuccess || !WorkSharingContent.Normalize(new(item.Key, item.Version - 1, item.Content)).IsSuccess) return Result<WorkUsageSummary>.Failure("usage_records_invalid");
            WorkSharingUsage? usage;
            try { usage = item.Content.Deserialize<WorkSharingUsage>(Json); }
            catch (JsonException) { return Result<WorkUsageSummary>.Failure("usage_records_invalid"); }
            if (usage?.Accounting is not { } accounting) { legacy++; continue; }
            if (accounting.ProjectId != state.Connection.ProjectId || accounting.ParticipantId != item.ParticipantId)
                return Result<WorkUsageSummary>.Failure("usage_identity_invalid");
            records.Add(accounting);
        }
        return WorkUsageAccounting.Summarize(records, query, legacy);
    }
    public async Task<Result<WorkSharingOperation>> PublishAsync(WorkspaceId workspace, WorkProjectId project, WorkUsageId id, CancellationToken token)
    {
        var owned = await OwnedAsync(workspace, project, token);
        if (owned is null) return Result<WorkSharingOperation>.Failure("work_project_not_found");
        var entry = await repository.GetAsync(workspace, context.PrincipalId, project, id, token);
        if (entry is null) return Result<WorkSharingOperation>.Failure("usage_record_not_found");
        if (owned.Definition.Sharing is not { } connection || entry.Record.ProjectId != connection.ProjectId || entry.Record.ParticipantId != connection.ParticipantId)
            return Result<WorkSharingOperation>.Failure("usage_sharing_identity_changed");
        // Publish every immutable ancestor first; each call has its own durable, recoverable sharing operation.
        var chain = new List<WorkUsageEntry> { entry };
        while (entry.Record.PreviousId is { } previous)
        {
            if (chain.Count >= WorkUsageFormat.MaximumSummaryRecords) return Result<WorkSharingOperation>.Failure("usage_lineage_limit");
            var parent = await repository.GetAsync(workspace, context.PrincipalId, project, previous, token);
            if (parent is null || parent.Digest != entry.Record.PreviousDigest || !WorkUsageAccounting.SameIdentity(parent.Record, entry.Record))
                return Result<WorkSharingOperation>.Failure("usage_lineage_conflict");
            chain.Add(parent); entry = parent;
        }
        Result<WorkSharingOperation>? result = null;
        foreach (var item in chain.AsEnumerable().Reverse())
        {
            var portable = new WorkSharingUsage(item.Record.Id.Value, item.Record.PlanId, item.Record.TaskId, new Dictionary<string, long>()) { Accounting = item.Record };
            var operation = new WorkSharingOperationId(StableId(new { Kind = "work-usage-publication/1.0", workspace, Principal = context.PrincipalId, project, item.Record.Id, item.Digest }));
            result = await sharing.PublishAsync(workspace, project, new(operation, owned.Version,
                [new(new(WorkSharingDomain.Usage, item.Record.Id.ToString()), 0, JsonSerializer.SerializeToElement(portable, Json))]), token);
            if (!result.IsSuccess || result.Value!.Status != WorkSharingOperationStatus.Succeeded) return result;
        }
        return result!;
    }
    private Task<WorkProjectSnapshot?> OwnedAsync(WorkspaceId workspace, WorkProjectId project, CancellationToken token) => workspace == context.WorkspaceId
        && context.PrincipalId != Guid.Empty ? projects.GetAsync(workspace, context.PrincipalId, project, token) : Task.FromResult<WorkProjectSnapshot?>(null);
    private static Guid StableId<T>(T value) => new(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)).AsSpan(0, 16));
}
