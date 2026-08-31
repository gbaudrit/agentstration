using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.Flow;
using Agentstration.Resources;

namespace Agentstration.Work;

public sealed record WorkTask(
    WorkTaskId Id,
    WorkspaceId WorkspaceId,
    EntryId? EntryId,
    InteractionId? InteractionId,
    string Title,
    string? Description,
    WorkTaskStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? FlowRunId,
    IReadOnlyList<WorkMessage> Conversation,
    IReadOnlyList<WorkInteraction> Activities,
    IReadOnlyList<WorkArtifact> Artifacts,
    WorkResult? Result,
    WorkError? Error,
    long Version);
