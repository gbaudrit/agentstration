using System.Diagnostics;
using System.Diagnostics.Metrics;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;
using Agentstration.Flow;
using Microsoft.Extensions.Logging;

namespace Agentstration.Application.Work;

public sealed record SubmitWorkItemCommand(
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
    FlowReference? Flow = null);

public sealed class WorkItemService(
    IWorkItemRepository repository,
    IWorkExecutionGateway executionGateway,
    TimeProvider timeProvider,
    ILogger<WorkItemService> logger,
    IEnumerable<IWorkTaskEventSink> eventSinks)
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
            WorkItemId.New(), command.Type, command.Instruction, now, command.Title, command.Description,
            command.RequesterIdentity, command.CorrelationId, command.Metadata, command.RequestedAgentId, command.Inputs, command.Attachments, command.Flow);
        activity?.SetTag("work.item.id", item.Id.ToString());
        activity?.SetTag("work.correlation.id", item.CorrelationId.ToString());
        await repository.CreateAsync(item, cancellationToken);

        var accepted = await executionGateway.RequestExecutionAsync(new WorkExecutionRequest(
            item.Id, item.CorrelationId, item.Type, item.Instruction, item.RequestedAgentId, item.Inputs, item.Attachments, item.Metadata, item.Flow), cancellationToken);
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

    public Task<StoredWorkItem?> GetAsync(WorkItemId id, CancellationToken cancellationToken) => repository.GetAsync(id, cancellationToken);
    public Task<WorkItemPage> QueryAsync(WorkItemQuery query, CancellationToken cancellationToken) => repository.QueryAsync(query, cancellationToken);

    public async Task<StoredWorkItem> ApplyExecutionEventAsync(WorkExecutionEvent executionEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(executionEvent);
        using var activity = ActivitySource.StartActivity("work.apply-runtime-event");
        activity?.SetTag("work.item.id", executionEvent.WorkItemId.ToString());
        activity?.SetTag("work.execution.id", executionEvent.ExecutionId.ToString());
        var stored = await GetRequiredAsync(executionEvent.WorkItemId, cancellationToken);
        var expectedVersion = stored.Value.Version;
        if (!stored.Value.ApplyRuntimeEvent(executionEvent)) return stored;
        StoredWorkItem updated;
        try
        {
            updated = await repository.SaveAsync(stored.Value, expectedVersion, cancellationToken);
        }
        catch (WorkItemConcurrencyException)
        {
            var latest = await GetRequiredAsync(executionEvent.WorkItemId, cancellationToken);
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

    public async Task<StoredWorkItem> CancelAsync(WorkItemId id, string? authorId, CancellationToken cancellationToken)
    {
        var stored = await GetRequiredAsync(id, cancellationToken);
        var expectedVersion = stored.Value.Version;
        stored.Value.Cancel(authorId, Guid.NewGuid(), timeProvider.GetUtcNow());
        var updated = await repository.SaveAsync(stored.Value, expectedVersion, cancellationToken);
        CancelledCounter.Add(1);
        await PublishAsync(updated.Value, cancellationToken);
        return updated;
    }

    public async Task<StoredWorkItem> AddMessageAsync(WorkItemId id, string content, string? authorId, CancellationToken cancellationToken)
    {
        var stored = await GetRequiredAsync(id, cancellationToken);
        var expectedVersion = stored.Value.Version;
        stored.Value.AddMessage(content, authorId, Guid.NewGuid(), timeProvider.GetUtcNow());
        var updated = await repository.SaveAsync(stored.Value, expectedVersion, cancellationToken);
        await PublishAsync(updated.Value, cancellationToken);
        return updated;
    }

    public async Task<StoredWorkItem> PauseAsync(WorkItemId id, CancellationToken cancellationToken)
    {
        var stored = await GetRequiredAsync(id, cancellationToken);
        var expectedVersion = stored.Value.Version;
        if (!stored.Value.Pause(Guid.NewGuid(), timeProvider.GetUtcNow())) return stored;
        var updated = await repository.SaveAsync(stored.Value, expectedVersion, cancellationToken);
        await PublishAsync(updated.Value, cancellationToken);
        return updated;
    }

    public async Task<StoredWorkItem> ResumeAsync(WorkItemId id, CancellationToken cancellationToken)
    {
        var stored = await GetRequiredAsync(id, cancellationToken);
        var expectedVersion = stored.Value.Version;
        if (!stored.Value.Resume(Guid.NewGuid(), timeProvider.GetUtcNow())) return stored;
        var updated = await repository.SaveAsync(stored.Value, expectedVersion, cancellationToken);
        await PublishAsync(updated.Value, cancellationToken);
        return updated;
    }

    public async Task<StoredWorkItem> ProvideInputAsync(WorkItemId id, WorkInput input, string? authorId, CancellationToken cancellationToken)
    {
        var stored = await GetRequiredAsync(id, cancellationToken);
        var expectedVersion = stored.Value.Version;
        stored.Value.ProvideInput(input, authorId, Guid.NewGuid(), timeProvider.GetUtcNow());
        var updated = await repository.SaveAsync(stored.Value, expectedVersion, cancellationToken);
        await PublishAsync(updated.Value, cancellationToken);
        return updated;
    }

    public async Task<StoredWorkItem> SubmitApprovalAsync(WorkItemId id, WorkApprovalDecision decision, string? authorId, string? comment, CancellationToken cancellationToken)
    {
        var stored = await GetRequiredAsync(id, cancellationToken);
        var expectedVersion = stored.Value.Version;
        stored.Value.SubmitApproval(decision, authorId, comment, Guid.NewGuid(), timeProvider.GetUtcNow());
        var updated = await repository.SaveAsync(stored.Value, expectedVersion, cancellationToken);
        await PublishAsync(updated.Value, cancellationToken);
        return updated;
    }

    private async Task<StoredWorkItem> GetRequiredAsync(WorkItemId id, CancellationToken cancellationToken) =>
        await repository.GetAsync(id, cancellationToken) ?? throw new KeyNotFoundException($"Work item '{id}' was not found.");

    private async Task PublishAsync(WorkItem item, CancellationToken cancellationToken)
    {
        foreach (var sink in eventSinks) await sink.PublishAsync(item.ToSnapshot(), cancellationToken);
    }
}
