using System.Text.Json;
using Agentstration.Work;

namespace Agentstration.Work.Contracts;

public sealed record WorkTaskOperationsSummary(
    Guid Id,
    string WorkspaceId,
    string EntryId,
    Guid InteractionId,
    string Title,
    string? Description,
    WorkTaskStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    string? CurrentFlowRunId,
    Guid? LatestResultId,
    int PendingActionCount,
    int ResultCount,
    int ArtifactCount,
    int FlowRunCount,
    string? CurrentActivity,
    WorkTaskErrorResponse? Error);

public sealed record WorkTaskOperationsPageResponse(
    IReadOnlyList<WorkTaskOperationsSummary> Items,
    int Page,
    int PageSize,
    int TotalCount);

public sealed record WorkTaskOperationsCountersResponse(
    int Running,
    int ActionRequired,
    int Paused,
    int Failed,
    int CompletedRecently);

public sealed record WorkTaskErrorResponse(
    string Code,
    string Title,
    string Message,
    DateTimeOffset OccurredAt,
    string? FlowRunId,
    bool IsRetryable);

public sealed record WorkTaskFlowRunResponse(
    string Id,
    string FlowId,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string Trigger,
    string? ParentFlowRunId,
    Guid? TriggerMessageId,
    int ResultCount,
    int ArtifactCount);

public sealed record WorkTaskResultResponse(
    Guid Id,
    string? FlowRunId,
    WorkTaskResultKind Kind,
    string Title,
    JsonElement Content,
    DateTimeOffset CreatedAt,
    int Sequence);

public sealed record WorkTaskArtifactResponse(
    Guid Id,
    string? FlowRunId,
    string Name,
    string ContentType,
    long Length,
    DateTimeOffset CreatedAt,
    int Sequence,
    string DownloadUrl);

public sealed record WorkTaskOperationsDetailResponse(
    WorkTaskOperationsSummary Task,
    InteractionResponse Interaction,
    IReadOnlyList<PendingActionContract> PendingActions,
    IReadOnlyList<WorkTaskFlowRunResponse> FlowRuns,
    IReadOnlyList<WorkTaskResultResponse> Results,
    IReadOnlyList<WorkTaskArtifactResponse> Artifacts,
    IReadOnlyList<WorkTaskActivity> Activities,
    IReadOnlyList<ConversationMessage> Messages);
