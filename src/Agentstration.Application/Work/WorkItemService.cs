using System.Diagnostics;
using System.Diagnostics.Metrics;
using Agentstration.Flows;
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
    WorkItemId? Id = null)
{
    public ResourceScopeRef? ExecutionScopeRef { get; init; }
}

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

    internal Task<IReadOnlyDictionary<WorkTaskId, StoredWorkItem>> ListLatestContinuationsAsync(
        WorkspaceId workspaceId,
        IReadOnlyCollection<WorkTaskId> taskIds,
        CancellationToken cancellationToken) => repository.ListLatestContinuationsAsync(workspaceId, CurrentExecutionScope().PrincipalId, taskIds, cancellationToken);

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
        var executionScope = CurrentExecutionScope();
        var requestedScopeRef = command.ExecutionScopeRef ?? (command.WorkspaceId.Value == Guid.Empty
            ? executionScope.ScopeRef
            : ResourceScopeRef.Workspace(command.WorkspaceId.Value));
        if (requestedScopeRef != executionScope.ScopeRef)
            throw new WorkValidationException("work_execution_scope_mismatch", "The requested execution scope must match the authenticated scope.");
        var item = WorkItem.Create(
            command.Id ?? WorkItemId.New(), executionScope.Exact, command.Type, command.Instruction, now, command.Title, command.Description,
            command.RequesterIdentity, command.CorrelationId, command.Metadata, command.RequestedAgentId, command.Inputs, command.Attachments, command.Flow);
        activity?.SetTag("work.item.id", item.Id.ToString());
        activity?.SetTag("work.correlation.id", item.CorrelationId.ToString());
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

    public Task<StoredWorkItem?> GetAsync(WorkspaceId workspaceId, WorkItemId id, CancellationToken cancellationToken) =>
        repository.GetAsync(workspaceId, CurrentExecutionScope().PrincipalId, id, cancellationToken);

    public Task<StoredWorkItem?> GetExactAsync(ResourceScopeRef scopeRef, WorkItemId id, CancellationToken cancellationToken)
    {
        var scope = CurrentExecutionScope();
        if (scope.ScopeRef != scopeRef)
            throw new WorkValidationException("work_execution_scope_mismatch", "Work access requires the authenticated exact execution scope.");
        return repository.GetExactAsync(scopeRef, scope.PrincipalId, id, cancellationToken);
    }

    public Task<WorkItemPage> QueryAsync(WorkItemQuery query, CancellationToken cancellationToken)
    {
        if (query.OwnerPrincipalId != CurrentExecutionScope().PrincipalId)
            throw new WorkValidationException("work_owner_scope_mismatch", "Work queries must use the authenticated principal as owner.");
        if (query.ScopeRef != CurrentExecutionScope().ScopeRef)
            throw new WorkValidationException("work_execution_scope_mismatch", "Work queries must use the authenticated exact execution scope.");
        return repository.QueryAsync(query, cancellationToken);
    }

    public async Task<StoredWorkItem> ApplyExecutionEventAsync(WorkExecutionEvent executionEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(executionEvent);
        using var activity = ActivitySource.StartActivity("work.apply-runtime-event");
        activity?.SetTag("work.item.id", executionEvent.WorkItemId.ToString());
        activity?.SetTag("agentstration.execution.scope", executionEvent.ExecutionScopeRef.Value);
        activity?.SetTag("work.execution.id", executionEvent.ExecutionId.ToString());
        var stored = await GetRequiredForExecutionExactAsync(executionEvent.ExecutionScopeRef, executionEvent.WorkItemId, cancellationToken);
        if (stored.Value.ExecutionScopeRef != executionEvent.ExecutionScopeRef)
            throw new WorkTransitionException("execution_scope_mismatch", "The runtime event belongs to another execution scope.");
        var expectedVersion = stored.Value.Version;
        if (!stored.Value.ApplyRuntimeEvent(executionEvent)) return stored;
        StoredWorkItem updated;
        try
        {
            updated = await repository.SaveAsync(stored.Value, expectedVersion, cancellationToken);
        }
        catch (WorkItemConcurrencyException)
        {
            var latest = await GetRequiredForExecutionExactAsync(executionEvent.ExecutionScopeRef, executionEvent.WorkItemId, cancellationToken);
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
        => await CancelExactAsync(ResourceScopeRef.Workspace(workspaceId.Value), id, authorId, cancellationToken);

    public async Task<StoredWorkItem> CancelExactAsync(ResourceScopeRef scopeRef, WorkItemId id, string? authorId, CancellationToken cancellationToken)
    {
        var stored = await GetRequiredExactAsync(scopeRef, id, cancellationToken);
        var expectedVersion = stored.Value.Version;
        stored.Value.Cancel(authorId, Guid.NewGuid(), timeProvider.GetUtcNow());
        var updated = await repository.SaveAsync(stored.Value, expectedVersion, cancellationToken);
        CancelledCounter.Add(1);
        await PublishAsync(updated.Value, cancellationToken);
        return updated;
    }

    public async Task<StoredWorkItem> AddMessageAsync(WorkspaceId workspaceId, WorkItemId id, string content, string? authorId, CancellationToken cancellationToken)
        => await AddMessageExactAsync(ResourceScopeRef.Workspace(workspaceId.Value), id, content, authorId, cancellationToken);

    public async Task<StoredWorkItem> AddMessageExactAsync(ResourceScopeRef scopeRef, WorkItemId id, string content, string? authorId, CancellationToken cancellationToken)
    {
        var stored = await GetRequiredExactAsync(scopeRef, id, cancellationToken);
        var expectedVersion = stored.Value.Version;
        stored.Value.AddMessage(content, authorId, Guid.NewGuid(), timeProvider.GetUtcNow());
        var updated = await repository.SaveAsync(stored.Value, expectedVersion, cancellationToken);
        await PublishAsync(updated.Value, cancellationToken);
        return updated;
    }

    public async Task<StoredWorkItem> PauseAsync(WorkspaceId workspaceId, WorkItemId id, CancellationToken cancellationToken)
        => await PauseExactAsync(ResourceScopeRef.Workspace(workspaceId.Value), id, cancellationToken);

    public async Task<StoredWorkItem> PauseExactAsync(ResourceScopeRef scopeRef, WorkItemId id, CancellationToken cancellationToken)
    {
        var stored = await GetRequiredExactAsync(scopeRef, id, cancellationToken);
        var expectedVersion = stored.Value.Version;
        if (!stored.Value.Pause(Guid.NewGuid(), timeProvider.GetUtcNow())) return stored;
        var updated = await repository.SaveAsync(stored.Value, expectedVersion, cancellationToken);
        await PublishAsync(updated.Value, cancellationToken);
        return updated;
    }

    public async Task<StoredWorkItem> ResumeAsync(WorkspaceId workspaceId, WorkItemId id, CancellationToken cancellationToken)
        => await ResumeExactAsync(ResourceScopeRef.Workspace(workspaceId.Value), id, cancellationToken);

    public async Task<StoredWorkItem> ResumeExactAsync(ResourceScopeRef scopeRef, WorkItemId id, CancellationToken cancellationToken)
    {
        var stored = await GetRequiredExactAsync(scopeRef, id, cancellationToken);
        var expectedVersion = stored.Value.Version;
        if (!stored.Value.Resume(Guid.NewGuid(), timeProvider.GetUtcNow())) return stored;
        var updated = await repository.SaveAsync(stored.Value, expectedVersion, cancellationToken);
        await PublishAsync(updated.Value, cancellationToken);
        return updated;
    }

    public async Task<StoredWorkItem> ProvideInputAsync(WorkspaceId workspaceId, WorkItemId id, WorkInput input, string? authorId, CancellationToken cancellationToken)
        => await ProvideInputExactAsync(ResourceScopeRef.Workspace(workspaceId.Value), id, input, authorId, cancellationToken);

    public async Task<StoredWorkItem> ProvideInputExactAsync(ResourceScopeRef scopeRef, WorkItemId id, WorkInput input, string? authorId, CancellationToken cancellationToken)
    {
        var stored = await GetRequiredExactAsync(scopeRef, id, cancellationToken);
        var expectedVersion = stored.Value.Version;
        stored.Value.ProvideInput(input, authorId, Guid.NewGuid(), timeProvider.GetUtcNow());
        var updated = await repository.SaveAsync(stored.Value, expectedVersion, cancellationToken);
        await PublishAsync(updated.Value, cancellationToken);
        return updated;
    }

    public async Task<StoredWorkItem> SubmitApprovalAsync(WorkspaceId workspaceId, WorkItemId id, WorkApprovalDecision decision, string? authorId, string? comment, CancellationToken cancellationToken)
        => await SubmitApprovalExactAsync(ResourceScopeRef.Workspace(workspaceId.Value), id, decision, authorId, comment, cancellationToken);

    public async Task<StoredWorkItem> SubmitApprovalExactAsync(ResourceScopeRef scopeRef, WorkItemId id, WorkApprovalDecision decision, string? authorId, string? comment, CancellationToken cancellationToken)
    {
        var stored = await GetRequiredExactAsync(scopeRef, id, cancellationToken);
        var expectedVersion = stored.Value.Version;
        stored.Value.SubmitApproval(decision, authorId, comment, Guid.NewGuid(), timeProvider.GetUtcNow());
        var updated = await repository.SaveAsync(stored.Value, expectedVersion, cancellationToken);
        await PublishAsync(updated.Value, cancellationToken);
        return updated;
    }

    private async Task<StoredWorkItem> GetRequiredAsync(WorkspaceId workspaceId, WorkItemId id, CancellationToken cancellationToken) =>
        await repository.GetAsync(workspaceId, CurrentExecutionScope().PrincipalId, id, cancellationToken) ?? throw new KeyNotFoundException($"Work item '{id}' was not found in workspace '{workspaceId}'.");

    private async Task<StoredWorkItem> GetRequiredExactAsync(ResourceScopeRef scopeRef, WorkItemId id, CancellationToken cancellationToken)
    {
        var current = CurrentExecutionScope();
        if (current.ScopeRef != scopeRef)
            throw new WorkValidationException("work_execution_scope_mismatch", "Work access requires the authenticated exact execution scope.");
        return await repository.GetExactAsync(scopeRef, current.PrincipalId, id, cancellationToken)
            ?? throw new KeyNotFoundException($"Work item '{id}' was not found in execution scope '{scopeRef}'.");
    }

    private async Task<StoredWorkItem> GetRequiredForExecutionAsync(WorkspaceId workspaceId, WorkItemId id, CancellationToken cancellationToken) =>
        await repository.GetForExecutionAsync(workspaceId, id, cancellationToken) ?? throw new KeyNotFoundException($"Work item '{id}' was not found in workspace '{workspaceId}'.");

    private async Task<StoredWorkItem> GetRequiredForExecutionExactAsync(ResourceScopeRef scopeRef, WorkItemId id, CancellationToken cancellationToken) =>
        await repository.GetForExecutionExactAsync(scopeRef, id, cancellationToken) ?? throw new KeyNotFoundException($"Work item '{id}' was not found in execution scope '{scopeRef}'.");

    private FlowRunScope CurrentExecutionScope() =>
        executionScopeAccessors.Select(accessor => accessor.Current).FirstOrDefault(scope => scope is not null)
        ?? throw new WorkValidationException("work_execution_scope_required", "Work access requires an authenticated execution scope.");

    private async Task PublishAsync(WorkItem item, CancellationToken cancellationToken)
    {
        foreach (var sink in eventSinks) await sink.PublishAsync(item.ToSnapshot(), cancellationToken);
    }
}
