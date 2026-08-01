using System.Text.Json;
using Agentstration.Flow;

namespace Agentstration.Work;

public enum WorkItemStatus
{
    Pending,
    Queued,
    Running,
    WaitingForInput,
    WaitingForApproval,
    Completed,
    Failed,
    Cancelled
}

public enum WorkInteractionOrigin { Requester, Agent, System, Runtime }
public enum WorkInteractionKind { Message, Progress, InputRequested, InputProvided, ApprovalRequested, Approved, Rejected, IntermediateResult, Error }
public enum WorkErrorCategory { Validation, Execution, Dependency, Timeout, Cancelled, Unknown }
public enum WorkApprovalDecision { Approved, Rejected }

public sealed record WorkContentReference(string Uri, string? MediaType = null, string? Name = null);
public sealed record WorkAttachment(string Name, WorkContentReference Content, long? Size = null, IReadOnlyDictionary<string, string>? Metadata = null);
public sealed record WorkInput(string? Text = null, JsonElement? Structured = null, IReadOnlyDictionary<string, string>? Metadata = null);
public sealed record WorkMessage(Guid Id, long Sequence, WorkInteractionOrigin Origin, string? AuthorId, string Content, DateTimeOffset CreatedAt);
public sealed record WorkInteraction(Guid Id, long Sequence, WorkInteractionKind Kind, WorkInteractionOrigin Origin, string? AuthorId, string? Content, double? Progress, DateTimeOffset CreatedAt);
public sealed record WorkArtifact(string Name, WorkContentReference Content, IReadOnlyDictionary<string, string>? Metadata = null);
public sealed record WorkResultContent(string? Text = null, JsonElement? Structured = null, string? MediaType = null);
public sealed record WorkResult(IReadOnlyList<WorkResultContent> Contents, IReadOnlyList<WorkArtifact> Artifacts, IReadOnlyDictionary<string, string> Metadata, DateTimeOffset CreatedAt);
public sealed record WorkError(string Code, string Message, WorkErrorCategory Category, bool IsRecoverable, DateTimeOffset OccurredAt, WorkExecutionId? ExecutionId = null, string? TechnicalDetails = null);
public sealed record WorkHistoryEvent(Guid EventId, long Sequence, string Type, WorkInteractionOrigin Origin, DateTimeOffset OccurredAt, IReadOnlyDictionary<string, string>? Metadata = null);

public sealed class WorkValidationException(string code, string message) : ArgumentException(message)
{
    public string Code { get; } = code;
}

public sealed class WorkTransitionException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

public sealed record WorkItemSnapshot(
    WorkItemId Id,
    string Type,
    string? Title,
    string Instruction,
    string? Description,
    WorkItemStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? RequesterIdentity,
    WorkCorrelationId CorrelationId,
    IReadOnlyDictionary<string, string> Metadata,
    string? RequestedAgentId,
    FlowReference? Flow,
    string? SelectedAgentId,
    WorkExecutionId? CurrentExecutionId,
    IReadOnlyList<WorkInput> Inputs,
    IReadOnlyList<WorkAttachment> Attachments,
    IReadOnlyList<WorkMessage> Messages,
    IReadOnlyList<WorkInteraction> Interactions,
    IReadOnlyList<WorkHistoryEvent> History,
    WorkResult? Result,
    WorkError? Error,
    long Version);
