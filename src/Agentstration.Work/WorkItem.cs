using Agentstration.Flow;

namespace Agentstration.Work;

public sealed class WorkItem
{
    private readonly List<WorkInput> _inputs;
    private readonly List<WorkAttachment> _attachments;
    private readonly List<WorkMessage> _messages;
    private readonly List<WorkInteraction> _interactions;
    private readonly List<WorkHistoryEvent> _history;
    private readonly HashSet<Guid> _appliedRuntimeEvents;

    private WorkItem(WorkItemSnapshot snapshot)
    {
        Id = snapshot.Id;
        Type = snapshot.Type;
        Title = snapshot.Title;
        Instruction = snapshot.Instruction;
        Description = snapshot.Description;
        Status = snapshot.Status;
        CreatedAt = snapshot.CreatedAt;
        UpdatedAt = snapshot.UpdatedAt;
        RequesterIdentity = snapshot.RequesterIdentity;
        CorrelationId = snapshot.CorrelationId;
        Metadata = snapshot.Metadata;
        RequestedAgentId = snapshot.RequestedAgentId;
        Flow = snapshot.Flow;
        SelectedAgentId = snapshot.SelectedAgentId;
        CurrentExecutionId = snapshot.CurrentExecutionId;
        Result = snapshot.Result;
        Error = snapshot.Error;
        Version = snapshot.Version;
        _inputs = [.. snapshot.Inputs];
        _attachments = [.. snapshot.Attachments];
        _messages = [.. snapshot.Messages];
        _interactions = [.. snapshot.Interactions];
        _history = [.. snapshot.History];
        _appliedRuntimeEvents = snapshot.History.Select(value => value.EventId).ToHashSet();
    }

    public WorkItemId Id { get; }
    public string Type { get; }
    public string? Title { get; }
    public string Instruction { get; }
    public string? Description { get; }
    public WorkItemStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public string? RequesterIdentity { get; }
    public WorkCorrelationId CorrelationId { get; }
    public IReadOnlyDictionary<string, string> Metadata { get; }
    public string? RequestedAgentId { get; }
    public FlowReference? Flow { get; }
    public string? SelectedAgentId { get; private set; }
    public WorkExecutionId? CurrentExecutionId { get; private set; }
    public IReadOnlyList<WorkInput> Inputs => _inputs;
    public IReadOnlyList<WorkAttachment> Attachments => _attachments;
    public IReadOnlyList<WorkMessage> Messages => _messages;
    public IReadOnlyList<WorkInteraction> Interactions => _interactions;
    public IReadOnlyList<WorkHistoryEvent> History => _history;
    public WorkResult? Result { get; private set; }
    public WorkError? Error { get; private set; }
    public long Version { get; private set; }

    public static WorkItem Create(
        WorkItemId id,
        string type,
        string instruction,
        DateTimeOffset now,
        string? title = null,
        string? description = null,
        string? requesterIdentity = null,
        WorkCorrelationId? correlationId = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        string? requestedAgentId = null,
        IReadOnlyList<WorkInput>? inputs = null,
        IReadOnlyList<WorkAttachment>? attachments = null,
        FlowReference? flow = null)
    {
        if (id.Value == Guid.Empty) throw new WorkValidationException("workitem_id_required", "A work item identifier is required.");
        if (string.IsNullOrWhiteSpace(type)) throw new WorkValidationException("workitem_type_required", "A work item type is required.");
        if (string.IsNullOrWhiteSpace(instruction)) throw new WorkValidationException("workitem_instruction_required", "A work item instruction is required.");
        if (instruction.Length > 100_000) throw new WorkValidationException("workitem_instruction_too_long", "The work item instruction cannot exceed 100000 characters.");
        if (flow is not null) FlowValidator.ValidateReference(flow);

        var submittedId = Guid.NewGuid();
        return new WorkItem(new WorkItemSnapshot(
            id,
            type.Trim(),
            Normalize(title),
            instruction.Trim(),
            Normalize(description),
            WorkItemStatus.Pending,
            now,
            now,
            Normalize(requesterIdentity),
            correlationId is null || string.IsNullOrWhiteSpace(correlationId.Value.Value) ? WorkCorrelationId.New() : correlationId.Value,
            metadata is null ? new Dictionary<string, string>() : new Dictionary<string, string>(metadata, StringComparer.Ordinal),
            Normalize(requestedAgentId),
            flow,
            null,
            null,
            inputs is null ? [] : [.. inputs],
            attachments is null ? [] : [.. attachments],
            [],
            [],
            [new WorkHistoryEvent(submittedId, 1, "WorkItemSubmitted", WorkInteractionOrigin.Requester, now)],
            null,
            null,
            1));
    }

    public static WorkItem Restore(WorkItemSnapshot snapshot) => new(snapshot);

    public WorkItemSnapshot ToSnapshot() => new(
        Id, Type, Title, Instruction, Description, Status, CreatedAt, UpdatedAt, RequesterIdentity, CorrelationId,
        Metadata, RequestedAgentId, Flow, SelectedAgentId, CurrentExecutionId, [.. _inputs], [.. _attachments], [.. _messages],
        [.. _interactions], [.. _history], Result, Error, Version);

    public bool ApplyRuntimeEvent(WorkExecutionEvent executionEvent)
    {
        ArgumentNullException.ThrowIfNull(executionEvent);
        if (executionEvent.WorkItemId != Id) throw new WorkTransitionException("workitem_mismatch", "The runtime event belongs to another work item.");
        if (_appliedRuntimeEvents.Contains(executionEvent.EventId)) return false;
        if (CurrentExecutionId is not null && executionEvent.ExecutionId != CurrentExecutionId)
            throw new WorkTransitionException("execution_mismatch", "The runtime event does not belong to the current execution.");

        switch (executionEvent)
        {
            case WorkExecutionStarted started: Start(started.ExecutionId, started.SelectedAgentId, started.EventId, started.OccurredAt); break;
            case WorkExecutionProgressed progressed: Progress(progressed.Progress, progressed.Message, progressed.EventId, progressed.OccurredAt); break;
            case WorkExecutionInputRequested input: RequestInput(input.Message, input.EventId, input.OccurredAt); break;
            case WorkExecutionApprovalRequested approval: RequestApproval(approval.Message, approval.EventId, approval.OccurredAt); break;
            case WorkExecutionCompleted completed: Complete(completed.Result, completed.EventId, completed.OccurredAt); break;
            case WorkExecutionFailed failed: Fail(failed.Error, failed.EventId, failed.OccurredAt); break;
            default: throw new NotSupportedException($"Runtime event '{executionEvent.GetType().Name}' is not supported.");
        }
        return true;
    }

    public void MarkQueued(WorkExecutionId executionId, string? selectedAgentId, Guid eventId, DateTimeOffset now)
    {
        RequireStatus(WorkItemStatus.Pending, "queue_not_allowed");
        if (executionId.Value == Guid.Empty) throw new WorkValidationException("execution_id_required", "An execution identifier is required.");
        CurrentExecutionId = executionId;
        SelectedAgentId = Normalize(selectedAgentId);
        Transition(WorkItemStatus.Queued, "WorkItemQueued", eventId, WorkInteractionOrigin.Runtime, now);
    }

    public void ProvideInput(WorkInput input, string? authorId, Guid eventId, DateTimeOffset now)
    {
        RequireStatus(WorkItemStatus.WaitingForInput, "input_not_expected");
        _inputs.Add(input);
        AddInteraction(WorkInteractionKind.InputProvided, WorkInteractionOrigin.Requester, authorId, input.Text, null, now);
        Transition(WorkItemStatus.Running, "WorkItemInputProvided", eventId, WorkInteractionOrigin.Requester, now);
    }

    public void SubmitApproval(WorkApprovalDecision decision, string? authorId, string? comment, Guid eventId, DateTimeOffset now)
    {
        RequireStatus(WorkItemStatus.WaitingForApproval, "approval_not_expected");
        if (decision == WorkApprovalDecision.Approved)
        {
            AddInteraction(WorkInteractionKind.Approved, WorkInteractionOrigin.Requester, authorId, comment, null, now);
            Transition(WorkItemStatus.Running, "WorkItemApproved", eventId, WorkInteractionOrigin.Requester, now);
            return;
        }

        AddInteraction(WorkInteractionKind.Rejected, WorkInteractionOrigin.Requester, authorId, comment, null, now);
        Error = new WorkError("approval_rejected", comment ?? "The requested approval was rejected.", WorkErrorCategory.Validation, false, now, CurrentExecutionId);
        Transition(WorkItemStatus.Failed, "WorkItemRejected", eventId, WorkInteractionOrigin.Requester, now);
    }

    public void AddMessage(string content, string? authorId, Guid eventId, DateTimeOffset now)
    {
        if (IsTerminal(Status)) throw new WorkTransitionException("message_not_allowed", "Messages cannot be added to a terminal work item.");
        if (string.IsNullOrWhiteSpace(content)) throw new WorkValidationException("message_required", "A message is required.");
        _messages.Add(new WorkMessage(Guid.NewGuid(), _messages.Count + 1L, WorkInteractionOrigin.Requester, Normalize(authorId), content.Trim(), now));
        Touch("WorkMessageAdded", eventId, WorkInteractionOrigin.Requester, now);
    }

    public void Cancel(string? authorId, Guid eventId, DateTimeOffset now)
    {
        if (IsTerminal(Status)) throw new WorkTransitionException("cancel_not_allowed", $"A work item in state '{Status}' cannot be cancelled.");
        Error = new WorkError("work_cancelled", "The work item was cancelled.", WorkErrorCategory.Cancelled, false, now, CurrentExecutionId);
        AddInteraction(WorkInteractionKind.Error, WorkInteractionOrigin.Requester, authorId, "Cancelled", null, now);
        Transition(WorkItemStatus.Cancelled, "WorkItemCancelled", eventId, WorkInteractionOrigin.Requester, now);
    }

    private void Start(WorkExecutionId executionId, string selectedAgentId, Guid eventId, DateTimeOffset now)
    {
        RequireStatus(WorkItemStatus.Queued, "start_not_allowed");
        CurrentExecutionId = executionId;
        SelectedAgentId = Normalize(selectedAgentId) ?? throw new WorkValidationException("selected_agent_required", "The selected agent is required.");
        Transition(WorkItemStatus.Running, "WorkItemStarted", eventId, WorkInteractionOrigin.Runtime, now);
    }

    private void Progress(double progress, string? message, Guid eventId, DateTimeOffset now)
    {
        RequireStatus(WorkItemStatus.Running, "progress_not_allowed");
        if (progress is < 0 or > 1) throw new WorkValidationException("progress_out_of_range", "Progress must be between zero and one.");
        AddInteraction(WorkInteractionKind.Progress, WorkInteractionOrigin.Agent, SelectedAgentId, message, progress, now);
        Touch("WorkItemProgressed", eventId, WorkInteractionOrigin.Runtime, now);
    }

    private void RequestInput(string message, Guid eventId, DateTimeOffset now)
    {
        RequireStatus(WorkItemStatus.Running, "input_request_not_allowed");
        AddInteraction(WorkInteractionKind.InputRequested, WorkInteractionOrigin.Agent, SelectedAgentId, message, null, now);
        Transition(WorkItemStatus.WaitingForInput, "WorkItemInputRequested", eventId, WorkInteractionOrigin.Runtime, now);
    }

    private void RequestApproval(string message, Guid eventId, DateTimeOffset now)
    {
        RequireStatus(WorkItemStatus.Running, "approval_request_not_allowed");
        AddInteraction(WorkInteractionKind.ApprovalRequested, WorkInteractionOrigin.System, null, message, null, now);
        Transition(WorkItemStatus.WaitingForApproval, "WorkItemApprovalRequested", eventId, WorkInteractionOrigin.Runtime, now);
    }

    private void Complete(WorkResult result, Guid eventId, DateTimeOffset now)
    {
        RequireStatus(WorkItemStatus.Running, "complete_not_allowed");
        if (Result is not null) throw new WorkTransitionException("result_already_recorded", "A final result has already been recorded.");
        Result = result;
        Error = null;
        Transition(WorkItemStatus.Completed, "WorkItemCompleted", eventId, WorkInteractionOrigin.Runtime, now);
    }

    private void Fail(WorkError error, Guid eventId, DateTimeOffset now)
    {
        if (IsTerminal(Status)) throw new WorkTransitionException("fail_not_allowed", $"A work item in state '{Status}' cannot fail.");
        Error = error;
        AddInteraction(WorkInteractionKind.Error, WorkInteractionOrigin.Runtime, null, error.Message, null, now);
        Transition(WorkItemStatus.Failed, "WorkItemFailed", eventId, WorkInteractionOrigin.Runtime, now);
    }

    private void RequireStatus(WorkItemStatus expected, string code)
    {
        if (Status != expected) throw new WorkTransitionException(code, $"Expected state '{expected}', but the work item is '{Status}'.");
    }

    private void Transition(WorkItemStatus status, string eventType, Guid eventId, WorkInteractionOrigin origin, DateTimeOffset now)
    {
        Status = status;
        Touch(eventType, eventId, origin, now);
    }

    private void Touch(string eventType, Guid eventId, WorkInteractionOrigin origin, DateTimeOffset now)
    {
        if (eventId == Guid.Empty) throw new WorkValidationException("event_id_required", "An event identifier is required.");
        _appliedRuntimeEvents.Add(eventId);
        UpdatedAt = now;
        Version++;
        _history.Add(new WorkHistoryEvent(eventId, _history.Count + 1L, eventType, origin, now));
    }

    private void AddInteraction(WorkInteractionKind kind, WorkInteractionOrigin origin, string? authorId, string? content, double? progress, DateTimeOffset now) =>
        _interactions.Add(new WorkInteraction(Guid.NewGuid(), _interactions.Count + 1L, kind, origin, Normalize(authorId), Normalize(content), progress, now));

    private static bool IsTerminal(WorkItemStatus status) => status is WorkItemStatus.Completed or WorkItemStatus.Failed or WorkItemStatus.Cancelled;
    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
