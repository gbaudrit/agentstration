using System.Text.Json;
using Agentstration.Flow;
using Agentstration.Work;

namespace Agentstration.Work.Contracts;

public sealed record CreateWorkItemRequest(
    string Type,
    string Instruction,
    string? Title = null,
    string? Description = null,
    string? RequesterIdentity = null,
    string? CorrelationId = null,
    string? RequestedAgentId = null,
    IReadOnlyDictionary<string, string>? Metadata = null,
    IReadOnlyList<WorkInputRequest>? Inputs = null,
    IReadOnlyList<WorkAttachmentRequest>? Attachments = null,
    FlowReference? Flow = null);

public sealed record WorkInputRequest(string? Text = null, JsonElement? Structured = null, IReadOnlyDictionary<string, string>? Metadata = null);
public sealed record WorkAttachmentRequest(string Name, string Uri, string? MediaType = null, long? Size = null, IReadOnlyDictionary<string, string>? Metadata = null);
public sealed record AddWorkMessageRequest(string Content, string? AuthorId = null);
public sealed record ProvideWorkInputRequest(string? Text = null, JsonElement? Structured = null, string? AuthorId = null, IReadOnlyDictionary<string, string>? Metadata = null);
public sealed record SubmitWorkApprovalRequest(WorkApprovalDecision Decision, string? AuthorId = null, string? Comment = null);

public sealed record WorkItemResponse(
    Guid Id,
    string Type,
    string? Title,
    string Instruction,
    string? Description,
    WorkItemStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? RequesterIdentity,
    string CorrelationId,
    IReadOnlyDictionary<string, string> Metadata,
    string? RequestedAgentId,
    FlowReference? Flow,
    string? SelectedAgentId,
    Guid? CurrentExecutionId,
    IReadOnlyList<WorkInput> Inputs,
    IReadOnlyList<WorkAttachment> Attachments,
    IReadOnlyList<WorkMessage> Messages,
    IReadOnlyList<WorkInteraction> Interactions,
    WorkResultResponse? Result,
    WorkErrorResponse? Error,
    long Version);

public sealed record WorkItemSummaryResponse(Guid Id, string Type, string? Title, WorkItemStatus Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? RequesterIdentity, string? SelectedAgentId, long Version);
public sealed record WorkResultResponse(IReadOnlyList<WorkResultContent> Contents, IReadOnlyList<WorkArtifact> Artifacts, IReadOnlyDictionary<string, string> Metadata, DateTimeOffset CreatedAt);
public sealed record WorkErrorResponse(string Code, string Message, WorkErrorCategory Category, bool IsRecoverable, DateTimeOffset OccurredAt, Guid? ExecutionId);
public sealed record WorkEventResponse(Guid EventId, long Sequence, string Type, WorkInteractionOrigin Origin, DateTimeOffset OccurredAt, IReadOnlyDictionary<string, string>? Metadata);
public sealed record WorkItemPageResponse(IReadOnlyList<WorkItemSummaryResponse> Value, string? NextLink);
