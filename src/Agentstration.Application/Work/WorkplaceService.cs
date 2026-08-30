using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Agentstration.Work.Storage.Abstractions;

namespace Agentstration.Application.Work;

public sealed record SubmitEntryCommand(WorkspaceId WorkspaceId, EntryId EntryId, IReadOnlyDictionary<string, JsonElement> Values, IReadOnlyList<WorkAttachment>? Attachments = null);
public sealed record EntrySubmission(WorkplaceInteraction Interaction, WorkplaceAction Action, WorkTask? Task);
public sealed record PendingActionResolution(PendingAction PendingAction, WorkplaceAction NextAction, WorkplaceInteraction? Interaction, WorkTask? Task);
public sealed record ConversationContextMessage(Guid Id, ConversationRole Role, string Content, DateTimeOffset CreatedAt);
public sealed record ContinuationResultReference(WorkTaskResultId Id, string Title, string? FlowRunId, int Sequence);
public sealed record ContinuationArtifactReference(WorkTaskArtifactId Id, string Name, string? FlowRunId, int Sequence);
public sealed record InteractionContinuationContext(
    WorkspaceId WorkspaceId,
    InteractionId InteractionId,
    WorkTaskId? CurrentTaskId,
    string? LastFlowRunId,
    Guid TriggerMessageId,
    IReadOnlyList<ConversationContextMessage> RecentMessages,
    IReadOnlyList<ContinuationResultReference> ResultReferences,
    IReadOnlyList<ContinuationArtifactReference> ArtifactReferences,
    EntryId EntryResourceId);
public sealed record MessageContinuation(ConversationMessage Message, WorkplaceInteraction Interaction, WorkplaceAction Action, WorkTask? Task);
public sealed record OperationalWorkTaskPage(IReadOnlyList<WorkTask> Items, int TotalCount);

public interface IWorkplaceEventSink
{
    Task PublishAsync(WorkplaceEventContract workplaceEvent, CancellationToken cancellationToken);
}
public interface IWorkplaceExternalInputResponder
{
    bool CanRespond(PendingAction action);
    Task RespondAsync(PendingAction action, IReadOnlyDictionary<string, JsonElement> values, string principalId, CancellationToken cancellationToken);
}

public sealed partial class WorkplaceService(
    IWorkplaceRepository repository,
    WorkItemService workItems,
    TimeProvider timeProvider,
    IEnumerable<IWorkplaceEventSink> eventSinks,
    IEnumerable<IWorkplaceExternalInputResponder> externalInputResponders,
    IWorkplaceContext context)
{
    private const int WorkItemQueryPageSize = 200;
    private const string WorkspaceMetadata = "workplace.workspaceId";
    private const string EntryMetadata = "workplace.entryId";
    private const string InteractionMetadata = "workplace.interactionId";
    private const string FlowRunMetadata = "flowRunId";
    private const string TaskMetadata = "workplace.taskId";
    private const string ParentFlowRunMetadata = "workplace.parentFlowRunId";
    private const string TriggerMessageMetadata = "workplace.triggerMessageId";
    private const string ContinuationMetadata = "workplace.continuation";
    private long eventSequence;































    public async Task<(WorkspaceId WorkspaceId, WorkTask Task)> GetOperationalTaskAsync(WorkspaceId workspaceId, WorkTaskId taskId, CancellationToken cancellationToken)
    {
        var anchor = (await workItems.GetAsync(workspaceId, taskId.ToWorkItemId(), cancellationToken))?.Value ?? throw new KeyNotFoundException($"Task '{taskId}' was not found.");
        if (anchor.Metadata.ContainsKey(TaskMetadata))
            throw new KeyNotFoundException($"Task '{taskId}' was not found.");
        RequireWorkspace(anchor, workspaceId);
        return (workspaceId, await GetTaskAsync(workspaceId, taskId, cancellationToken));
    }





















    private (PendingAction Action, WorkplaceAction Contract) CreatePendingAction(WorkplaceInteraction interaction, PendingActionKind kind, string title, string? description, IReadOnlyList<EntryFieldDefinition> fields, int step, DateTimeOffset now)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_'); var id = PendingActionId.New();
        var action = new PendingAction { Id = id, WorkspaceId = interaction.WorkspaceId, InteractionId = interaction.Id, WorkTaskId = interaction.TaskId, FlowRunId = $"resume-{interaction.Id.Value:N}", Kind = kind, Title = title, Description = description, Fields = fields, CreatedAt = now, ExpiresAt = now.AddHours(24), ResumeTokenHash = HashToken(token), ResumeStep = step };
        WorkplaceAction contract = kind switch { PendingActionKind.ConfirmationRequired => new RequestConfirmationAction(title, description, id, token), PendingActionKind.ChoiceRequired => new RequestChoiceAction(title, description, fields.Single().Options, id, token, fields.Single().Name), _ => new RequestInputAction(title, description, fields, id, token) }; return (action, contract);
    }



































}
