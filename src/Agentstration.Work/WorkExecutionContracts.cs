using Agentstration.Flow;

namespace Agentstration.Work;

public sealed record WorkExecutionRequest(
    WorkItemId WorkItemId,
    WorkCorrelationId CorrelationId,
    string Type,
    string Instruction,
    string? RequestedAgentId,
    IReadOnlyList<WorkInput> Inputs,
    IReadOnlyList<WorkAttachment> Attachments,
    IReadOnlyDictionary<string, string> Metadata,
    FlowReference? Flow = null,
    FlowRunScope? ExecutionScope = null);

public interface IWorkExecutionScopeAccessor
{
    FlowRunScope? Current { get; }
}

public sealed record WorkExecutionAccepted(WorkExecutionId ExecutionId, string? SelectedAgentId, DateTimeOffset AcceptedAt, Guid EventId);

public abstract record WorkExecutionEvent(Guid EventId, WorkItemId WorkItemId, WorkExecutionId ExecutionId, DateTimeOffset OccurredAt);
public sealed record WorkExecutionStarted(Guid EventId, WorkItemId WorkItemId, WorkExecutionId ExecutionId, DateTimeOffset OccurredAt, string SelectedAgentId) : WorkExecutionEvent(EventId, WorkItemId, ExecutionId, OccurredAt);
public sealed record WorkExecutionProgressed(Guid EventId, WorkItemId WorkItemId, WorkExecutionId ExecutionId, DateTimeOffset OccurredAt, double Progress, string? Message) : WorkExecutionEvent(EventId, WorkItemId, ExecutionId, OccurredAt);
public sealed record WorkExecutionInputRequested(Guid EventId, WorkItemId WorkItemId, WorkExecutionId ExecutionId, DateTimeOffset OccurredAt, string Message) : WorkExecutionEvent(EventId, WorkItemId, ExecutionId, OccurredAt);
public sealed record WorkExecutionApprovalRequested(Guid EventId, WorkItemId WorkItemId, WorkExecutionId ExecutionId, DateTimeOffset OccurredAt, string Message) : WorkExecutionEvent(EventId, WorkItemId, ExecutionId, OccurredAt);
public sealed record WorkExecutionCompleted(Guid EventId, WorkItemId WorkItemId, WorkExecutionId ExecutionId, DateTimeOffset OccurredAt, WorkResult Result) : WorkExecutionEvent(EventId, WorkItemId, ExecutionId, OccurredAt);
public sealed record WorkExecutionFailed(Guid EventId, WorkItemId WorkItemId, WorkExecutionId ExecutionId, DateTimeOffset OccurredAt, WorkError Error) : WorkExecutionEvent(EventId, WorkItemId, ExecutionId, OccurredAt);

public interface IWorkExecutionGateway
{
    Task<WorkExecutionAccepted> RequestExecutionAsync(WorkExecutionRequest request, CancellationToken cancellationToken);
    Task ConfirmQueuedAsync(WorkExecutionAccepted accepted, CancellationToken cancellationToken);
}

public interface IWorkTaskEventSink
{
    Task PublishAsync(WorkItemSnapshot snapshot, CancellationToken cancellationToken);
}
