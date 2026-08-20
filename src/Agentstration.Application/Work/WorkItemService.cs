using System.Diagnostics;
using System.Diagnostics.Metrics;
using Agentstration.Flow;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;
using Microsoft.Extensions.Logging;

namespace Agentstration.Application.Work;

public sealed record SubmitWorkItemCommand(
    WorkspaceId WorkspaceId,
    string Type,
    string Instruction,
    string? Title = null,
    string? Description = null,
    string? RequesterIdentity = null,
    WorkCorrelationId? CorrelationId = null,
    string? RequestedAgentId = null,
    IReadOnlyDictionary<string, string>? Metadata = null,
    IReadOnlyList<WorkInput>? Inputs = null,
    IReadOnlyList<WorkAttachment>? Attachments = null,
    FlowReference? Flow = null,
    WorkItemId? Id = null);

public sealed class WorkItemService(
    IWorkItemRepository repository,
    IWorkExecutionGateway executionGateway,
    TimeProvider timeProvider,
    ILogger<WorkItemService> logger,
    IEnumerable<IWorkTaskEventSink> eventSinks,
    IEnumerable<IWorkExecutionScopeAccessor> executionScopeAccessors)
{
    public static readonly ActivitySource ActivitySource = new("Agentstration.Work");
    public static readonly Meter Meter = new("Agentstration.Work");
    private static readonly Counter<long> SubmittedCounter = Meter.CreateCounter<long>("agentstration.work.submitted");
    private static readonly Counter<long> CompletedCounter = Meter.CreateCounter<long>("agentstration.work.completed");
    private static readonly Counter<long> FailedCounter = Meter.CreateCounter<long>("agentstration.work.failed");
    private static readonly Counter<long> CancelledCounter = Meter.CreateCounter<long>("agentstration.work.cancelled");
    private static readonly Histogram<double> DurationHistogram = Meter.CreateHistogram<double>("agentstration.work.duration", "s");
    private static readonly Action<ILogger, Guid, string, Guid, Exception?> WorkAcceptedLog = LoggerMessage.Define<Guid, string, Guid>(
        LogLevel.Information, new EventId(1001, "WorkAccepted"),
        "Work item {WorkItemId} with correlation {CorrelationId} was accepted as execution {ExecutionId}");
    private static readonly Action<ILogger, string, Guid, long, Exception?> RuntimeEventAppliedLog = LoggerMessage.Define<string, Guid, long>(
        LogLevel.Information, new EventId(1002, "RuntimeEventApplied"),
        "Applied runtime event {RuntimeEventType} to work item {WorkItemId} at version {Version}");

    public Task InitializeAsync(CancellationToken cancellationToken) => repository.InitializeAsync(cancellationToken);

    public Task<StoredWorkItem> SubmitAsync(SubmitWorkItemCommand command, CancellationToken cancellationToken) =>
        SubmitAsync(command, null, cancellationToken);

    internal async Task<StoredWorkItem> SubmitAsync(
        SubmitWorkItemCommand command,
        Func<StoredWorkItem, CancellationToken, Task>? beforeExecutionConfirmed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        using var activity = ActivitySource.StartActivity("work.submit");
        var now = timeProvider.GetUtcNow();
        var item = WorkItem.Create(
            command.Id ?? WorkItemId.New(), command.WorkspaceId, command.Type, command.Instruction, now, command.Title, command.Description,
            command.RequesterIdentity, command.CorrelationId, command.Metadata, command.RequestedAgentId, command.Inputs, command.Attachments, command.Flow);
        activity?.SetTag("work.item.id", item.Id.ToString());
        activity?.SetTag("work.correlation.id", item.CorrelationId.ToString());
        var executionScope = executionScopeAccessors.Select(accessor => accessor.Current).FirstOrDefault(scope => scope is not null);
        if (item.Flow is not null && executionScope is null)
            throw new WorkValidationException("work_execution_scope_required", "Flow-backed work requires an authenticated execution scope.");
        await repository.CreateAsync(item, cancellationToken);

        var accepted = await executionGateway.RequestExecutionAsync(new WorkExecutionRequest(
            item.Id, item.WorkspaceId, item.CorrelationId, item.Type, item.Instruction, item.RequestedAgentId, item.Inputs, item.Attachments, item.Metadata, item.Flow,
            executionScope), cancellationToken);
        var expectedVersion = item.Version;
        item.MarkQueued(accepted.ExecutionId, accepted.SelectedAgentId, accepted.EventId, accepted.AcceptedAt);
        var stored = await repository.SaveAsync(item, expectedVersion, cancellationToken);
        if (beforeExecutionConfirmed is not null) await beforeExecutionConfirmed(stored, cancellationToken);
        await executionGateway.ConfirmQueuedAsync(accepted, cancellationToken);
        SubmittedCounter.Add(1);
        WorkAcceptedLog(logger, item.Id.Value, item.CorrelationId.Value, accepted.ExecutionId.Value, null);
        await PublishAsync(stored.Value, cancellationToken);
        return stored;
    }

    public Task<StoredWorkItem?> GetAsync(WorkspaceId workspaceId, WorkItemId id, CancellationToken cancellationToken) => repository.GetAsync(workspaceId, id, cancellationToken);
    public Task<WorkItemPage> QueryAsync(WorkItemQuery query, CancellationToken cancellationToken) => repository.QueryAsync(query, cancellationToken);

    public async Task<StoredWorkItem> ApplyExecutionEventAsync(WorkExecutionEvent executionEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(executionEvent);
        using var activity = ActivitySource.StartActivity("work.apply-runtime-event");
        activity?.SetTag("work.item.id", executionEvent.WorkItemId.ToString());
        activity?.SetTag("agentstration.workspace.id", executionEvent.WorkspaceId.ToString());
        activity?.SetTag("work.execution.id", executionEvent.ExecutionId.ToString());
        var stored = await GetRequiredAsync(executionEvent.WorkspaceId, executionEvent.WorkItemId, cancellationToken);
        if (stored.Value.WorkspaceId != executionEvent.WorkspaceId)
            throw new WorkTransitionException("workspace_mismatch", "The runtime event belongs to another workspace.");
        var expectedVersion = stored.Value.Version;
        if (!stored.Value.ApplyRuntimeEvent(executionEvent)) return stored;
        StoredWorkItem updated;
        try
        {
            updated = await repository.SaveAsync(stored.Value, expectedVersion, cancellationToken);
        }
        catch (WorkItemConcurrencyException)
        {
            var latest = await GetRequiredAsync(executionEvent.WorkspaceId, executionEvent.WorkItemId, cancellationToken);
            if (latest.Value.History.Any(value => value.EventId == executionEvent.EventId)) return latest;
            throw;
        }
        if (updated.Value.Status == WorkItemStatus.Completed)
        {
            CompletedCounter.Add(1);
            DurationHistogram.Record((updated.Value.UpdatedAt - updated.Value.CreatedAt).TotalSeconds);
        }
        if (updated.Value.Status == WorkItemStatus.Failed) FailedCounter.Add(1);
        RuntimeEventAppliedLog(logger, executionEvent.GetType().Name, executionEvent.WorkItemId.Value, updated.Value.Version, null);
        await PublishAsync(updated.Value, cancellationToken);
        return updated;
    }

    public async Task<StoredWorkItem> CancelAsync(WorkspaceId workspaceId, WorkItemId id, string? authorId, CancellationToken cancellationToken)
    {
        var stored = await GetRequiredAsync(workspaceId, id, cancellationToken);
        var expectedVersion = stored.Value.Version;
        stored.Value.Cancel(authorId, Guid.NewGuid(), timeProvider.GetUtcNow());
        var updated = await repository.SaveAsync(stored.Value, expectedVersion, cancellationToken);
        CancelledCounter.Add(1);
        await PublishAsync(updated.Value, cancellationToken);
        return updated;
    }

    public async Task<StoredWorkItem> AddMessageAsync(WorkspaceId workspaceId, WorkItemId id, string content, string? authorId, CancellationToken cancellationToken)
    {
        var stored = await GetRequiredAsync(workspaceId, id, cancellationToken);
        var expectedVersion = stored.Value.Version;
        stored.Value.AddMessage(content, authorId, Guid.NewGuid(), timeProvider.GetUtcNow());
        var updated = await repository.SaveAsync(stored.Value, expectedVersion, cancellationToken);
        await PublishAsync(updated.Value, cancellationToken);
        return updated;
    }

    public async Task<StoredWorkItem> PauseAsync(WorkspaceId workspaceId, WorkItemId id, CancellationToken cancellationToken)
    {
        var stored = await GetRequiredAsync(workspaceId, id, cancellationToken);
        var expectedVersion = stored.Value.Version;
        if (!stored.Value.Pause(Guid.NewGuid(), timeProvider.GetUtcNow())) return stored;
        var updated = await repository.SaveAsync(stored.Value, expectedVersion, cancellationToken);
        await PublishAsync(updated.Value, cancellationToken);
        return updated;
    }

    public async Task<StoredWorkItem> ResumeAsync(WorkspaceId workspaceId, WorkItemId id, CancellationToken cancellationToken)
    {
        var stored = await GetRequiredAsync(workspaceId, id, cancellationToken);
        var expectedVersion = stored.Value.Version;
        if (!stored.Value.Resume(Guid.NewGuid(), timeProvider.GetUtcNow())) return stored;
        var updated = await repository.SaveAsync(stored.Value, expectedVersion, cancellationToken);
        await PublishAsync(updated.Value, cancellationToken);
        return updated;
    }

    public async Task<StoredWorkItem> ProvideInputAsync(WorkspaceId workspaceId, WorkItemId id, WorkInput input, string? authorId, CancellationToken cancellationToken)
    {
        var stored = await GetRequiredAsync(workspaceId, id, cancellationToken);
        var expectedVersion = stored.Value.Version;
        stored.Value.ProvideInput(input, authorId, Guid.NewGuid(), timeProvider.GetUtcNow());
        var updated = await repository.SaveAsync(stored.Value, expectedVersion, cancellationToken);
        await PublishAsync(updated.Value, cancellationToken);
        return updated;
    }

    public async Task<StoredWorkItem> SubmitApprovalAsync(WorkspaceId workspaceId, WorkItemId id, WorkApprovalDecision decision, string? authorId, string? comment, CancellationToken cancellationToken)
    {
        var stored = await GetRequiredAsync(workspaceId, id, cancellationToken);
        var expectedVersion = stored.Value.Version;
        stored.Value.SubmitApproval(decision, authorId, comment, Guid.NewGuid(), timeProvider.GetUtcNow());
        var updated = await repository.SaveAsync(stored.Value, expectedVersion, cancellationToken);
        await PublishAsync(updated.Value, cancellationToken);
        return updated;
    }

    private async Task<StoredWorkItem> GetRequiredAsync(WorkspaceId workspaceId, WorkItemId id, CancellationToken cancellationToken) =>
        await repository.GetAsync(workspaceId, id, cancellationToken) ?? throw new KeyNotFoundException($"Work item '{id}' was not found in workspace '{workspaceId}'.");

    private async Task PublishAsync(WorkItem item, CancellationToken cancellationToken)
    {
        foreach (var sink in eventSinks) await sink.PublishAsync(item.ToSnapshot(), cancellationToken);
    }
}
