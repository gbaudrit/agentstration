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
public sealed record PendingActionResolution(PendingAction PendingAction, WorkplaceAction NextAction, WorkplaceInteraction Interaction, WorkTask? Task);
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

public sealed class WorkplaceService(IWorkplaceRepository repository, WorkItemService workItems, TimeProvider timeProvider, IEnumerable<IWorkplaceEventSink> eventSinks, IWorkplaceContext context)
{
    private const string WorkspaceMetadata = "workplace.workspaceId";
    private const string EntryMetadata = "workplace.entryId";
    private const string InteractionMetadata = "workplace.interactionId";
    private const string FlowRunMetadata = "flowRunId";
    private const string TaskMetadata = "workplace.taskId";
    private const string ParentFlowRunMetadata = "workplace.parentFlowRunId";
    private const string TriggerMessageMetadata = "workplace.triggerMessageId";
    private const string ContinuationMetadata = "workplace.continuation";
    private long eventSequence;

    public Task InitializeAsync(CancellationToken cancellationToken) => repository.InitializeAsync(cancellationToken);
    public Task<IReadOnlyList<WorkplaceDashboard>> ListDashboardsAsync(WorkspaceId workspaceId, CancellationToken cancellationToken) => repository.ListDashboardsAsync(workspaceId, cancellationToken);
    public Task<IReadOnlyList<EntryResource>> ListEntriesAsync(WorkspaceId workspaceId, CancellationToken cancellationToken) => repository.ListEntriesAsync(workspaceId, cancellationToken);
    public Task<IReadOnlyList<EntryResource>> ListEntriesAsync(CancellationToken cancellationToken) => ListEntriesAsync(context.WorkspaceId, cancellationToken);
    public Task UpsertEntryAsync(EntryResource entry, CancellationToken cancellationToken) => repository.UpsertEntryAsync(entry, cancellationToken);

    public async Task<WorkplaceDashboard> GetDashboardAsync(WorkspaceId workspaceId, DashboardId id, CancellationToken cancellationToken) =>
        await repository.GetDashboardAsync(workspaceId, id, cancellationToken)
        ?? throw new KeyNotFoundException($"Dashboard '{id}' was not found in Workspace '{workspaceId}'.");
    public async Task<WorkplaceDashboard> GetDefaultDashboardAsync(WorkspaceId workspaceId, CancellationToken cancellationToken) =>
        (await repository.ListDashboardsAsync(workspaceId, cancellationToken)).SingleOrDefault(value => value.IsDefault)
        ?? throw new KeyNotFoundException($"Workspace '{workspaceId}' has no default Dashboard.");
    public async Task<EntryResource> GetEntryAsync(WorkspaceId workspaceId, EntryId id, CancellationToken cancellationToken) => await repository.GetEntryAsync(workspaceId, id, cancellationToken) ?? throw new KeyNotFoundException($"Entry '{id}' was not found in Workspace '{workspaceId}'.");
    public Task<EntryResource> GetEntryAsync(EntryId id, CancellationToken cancellationToken) => GetEntryAsync(context.WorkspaceId, id, cancellationToken);

    public async Task<IReadOnlyList<EntryResource>> ResolveEntriesAsync(WorkspaceId workspaceId, DashboardId dashboardId, CancellationToken cancellationToken)
    {
        var dashboard = await GetDashboardAsync(workspaceId, dashboardId, cancellationToken);
        var entries = new List<EntryResource>(dashboard.Entries.Count);
        foreach (var reference in dashboard.Entries.OrderBy(value => value.Order)) entries.Add(await GetEntryAsync(workspaceId, reference.EntryResourceId, cancellationToken));
        return entries;
    }

    public async Task<EntrySubmission> SubmitAsync(SubmitEntryCommand command, CancellationToken cancellationToken)
    {
        var dashboards = await repository.ListDashboardsAsync(command.WorkspaceId, cancellationToken);
        if (!dashboards.Any(dashboard => dashboard.Entries.Any(reference => reference.EntryResourceId == command.EntryId)))
            throw new WorkValidationException("entry_not_in_workspace", "The Entry is not exposed by a published Dashboard in the selected Workspace.");
        var entry = await GetEntryAsync(command.WorkspaceId, command.EntryId, cancellationToken); WorkplaceValidation.ValidateSubmission(entry, command.Values);
        var now = timeProvider.GetUtcNow(); var interaction = new WorkplaceInteraction { Id = InteractionId.New(), WorkspaceId = command.WorkspaceId, EntryId = command.EntryId, StartedAt = now, LastActivityAt = now, InputValues = command.Values.ToDictionary(value => value.Key, value => value.Value.Clone(), StringComparer.Ordinal), Attachments = command.Attachments ?? [] };
        await repository.CreateInteractionAsync(interaction, cancellationToken);
        var initialMessage = new ConversationMessage(Guid.NewGuid(), command.WorkspaceId, interaction.Id, null, ConversationRole.User, Instruction(entry, command.Values), now, Attachments: command.Attachments);
        await repository.AddMessageAsync(initialMessage, cancellationToken);
        interaction = interaction with { Messages = [initialMessage] };
        await PublishAsync(new InteractionUpdatedEvent(EventId(), command.WorkspaceId.Value, Sequence(), now, interaction.Id.Value, interaction.Status), cancellationToken);
        await PublishAsync(new MessageAddedEvent(EventId(), command.WorkspaceId.Value, Sequence(), now, initialMessage), cancellationToken);

        if (entry.Behavior.TaskCreationMode == TaskCreationMode.Never)
        {
            var response = new RespondAction("Agentstration received your request. You can continue this conversation whenever you like.");
            var agentMessage = await AddAgentMessageAsync(interaction, response.Content, now, cancellationToken);
            interaction = interaction with { Status = InteractionStatus.Idle, ImmediateResult = response, LastActivityAt = now, Messages = [initialMessage, agentMessage], Version = 2 };
            await repository.SaveInteractionAsync(interaction, 1, cancellationToken);
            await PublishInteractionAsync(interaction, cancellationToken);
            return new EntrySubmission(interaction, response, null);
        }

        if (string.Equals(entry.Name, "guided-request", StringComparison.Ordinal))
        {
            var (action, contract) = CreatePendingAction(interaction, PendingActionKind.ChoiceRequired, "Which style should I use?", "Choose once and I will start the work immediately.",
                [new EntryFieldDefinition { Name = "style", Label = "Style", Type = EntryFieldType.Choice, Required = true, Options = [new("concise", "Concise"), new("detailed", "Detailed"), new("technical", "Technical")] }], 10, now);
            await repository.CreatePendingActionAsync(action, cancellationToken); await CreateNotificationAsync(command.WorkspaceId, WorkNotificationKind.ActionRequired, action.Title, action.Description ?? "A response is required.", interaction.Id, null, action.Id, $"/interactions/{interaction.Id}", cancellationToken);
            interaction = interaction with { Status = InteractionStatus.WaitingForUser, PendingActionId = action.Id, ImmediateResult = null, LastActivityAt = now, Version = 2 };
            await repository.SaveInteractionAsync(interaction, 1, cancellationToken); await PublishAsync(new PendingActionCreatedEvent(EventId(), command.WorkspaceId.Value, Sequence(), now, ToContract(action)), cancellationToken);
            return new EntrySubmission(interaction with { ImmediateResult = contract }, contract, null);
        }

        if (string.Equals(entry.Name, "prepare-report", StringComparison.Ordinal))
        {
            var defaults = command.Values.ToDictionary(value => value.Key, value => value.Value.Clone(), StringComparer.Ordinal);
            defaults["detailLevel"] = JsonSerializer.SerializeToElement("standard");
            return await CreateTaskAsync(interaction, entry, defaults, command.Attachments, 1, cancellationToken);
        }

        return await CreateTaskAsync(interaction, entry, command.Values, command.Attachments, 1, cancellationToken);
    }

    public async Task<PendingActionResolution> RespondAsync(WorkspaceId workspaceId, InteractionId interactionId, PendingActionId pendingActionId, string resumeToken, IReadOnlyDictionary<string, JsonElement> values, CancellationToken cancellationToken)
    {
        var interaction = await GetInteractionAsync(workspaceId, interactionId, cancellationToken);
        var action = await repository.GetPendingActionAsync(workspaceId, pendingActionId, cancellationToken) ?? throw new KeyNotFoundException($"PendingAction '{pendingActionId}' was not found in Workspace '{workspaceId}'.");
        if (action.InteractionId != interactionId) throw new KeyNotFoundException($"PendingAction '{pendingActionId}' does not belong to Interaction '{interactionId}'.");
        if (action.Status != PendingActionStatus.Pending) throw new WorkTransitionException("pending_action_already_resolved", "The PendingAction is no longer pending.");
        var now = timeProvider.GetUtcNow(); if (action.ExpiresAt is not null && action.ExpiresAt <= now) { var expired = action with { Status = PendingActionStatus.Expired, ResolvedAt = now, Version = action.Version + 1 }; await repository.SavePendingActionAsync(expired, action.Version, cancellationToken); throw new WorkTransitionException("pending_action_expired", "The PendingAction has expired."); }
        if (!TokenMatches(resumeToken, action.ResumeTokenHash)) throw new WorkValidationException("resume_token_invalid", "The resume token is invalid for this Workspace action.");
        ValidatePendingResponse(action, values);
        var resolved = action with { Status = PendingActionStatus.Completed, ResolvedAt = now, Response = new PendingActionResponse(values.ToDictionary(value => value.Key, value => value.Value.Clone(), StringComparer.Ordinal), now), ResumeTokenHash = HashToken($"used:{Guid.NewGuid():N}"), Version = action.Version + 1 };
        await repository.SavePendingActionAsync(resolved, action.Version, cancellationToken);
        var message = new ConversationMessage(Guid.NewGuid(), workspaceId, interactionId, interaction.TaskId, ConversationRole.User, ResponseText(action, values), now, PendingActionId: action.Id);
        await repository.AddMessageAsync(message, cancellationToken); await PublishAsync(new MessageAddedEvent(EventId(), workspaceId.Value, Sequence(), now, message), cancellationToken);

        if (action.ResumeStep == 1)
        {
            var (confirmation, contract) = CreatePendingAction(interaction, PendingActionKind.ConfirmationRequired, "Generate the report?", "A Task will run the deterministic report flow and create a local artifact.", [], 2, now);
            await repository.CreatePendingActionAsync(confirmation, cancellationToken);
            interaction = interaction with { PendingActionId = confirmation.Id, ImmediateResult = null, LastActivityAt = now, Messages = [.. interaction.Messages, message], Version = interaction.Version + 1 };
            await repository.SaveInteractionAsync(interaction, interaction.Version - 1, cancellationToken); await PublishAsync(new PendingActionCreatedEvent(EventId(), workspaceId.Value, Sequence(), now, ToContract(confirmation)), cancellationToken);
            await PublishAsync(new PendingActionResolvedEvent(EventId(), workspaceId.Value, Sequence(), now, action.Id.Value), cancellationToken);
            return new PendingActionResolution(resolved, contract, interaction with { ImmediateResult = contract }, null);
        }

        if (action.ResumeStep == 10)
        {
            var guidedEntry = await GetEntryAsync(interaction.WorkspaceId, interaction.EntryId, cancellationToken);
            var guidedValues = interaction.InputValues.ToDictionary(value => value.Key, value => value.Value.Clone(), StringComparer.Ordinal);
            foreach (var value in values) guidedValues[value.Key] = value.Value.Clone();
            var guidedSubmission = await CreateTaskAsync(interaction with { Messages = [.. interaction.Messages, message] }, guidedEntry, guidedValues, interaction.Attachments, interaction.Version, cancellationToken);
            resolved = await LinkPendingActionAsync(resolved, guidedSubmission.Task, cancellationToken);
            await PublishAsync(new PendingActionResolvedEvent(EventId(), workspaceId.Value, Sequence(), now, action.Id.Value, resolved.WorkTaskId?.Value), cancellationToken);
            return new PendingActionResolution(resolved, guidedSubmission.Action, guidedSubmission.Interaction, guidedSubmission.Task);
        }

        if (!values.TryGetValue("confirmed", out var confirmed) || confirmed.ValueKind is not JsonValueKind.True)
        {
            var cancelled = new RespondAction("Report generation was cancelled."); interaction = interaction with { Status = InteractionStatus.Cancelled, PendingActionId = null, ImmediateResult = cancelled, LastActivityAt = now, Messages = [.. interaction.Messages, message], Version = interaction.Version + 1 };
            await repository.SaveInteractionAsync(interaction, interaction.Version - 1, cancellationToken); await PublishAsync(new PendingActionResolvedEvent(EventId(), workspaceId.Value, Sequence(), now, action.Id.Value), cancellationToken); return new PendingActionResolution(resolved, cancelled, interaction, null);
        }

        var entry = await GetEntryAsync(interaction.WorkspaceId, interaction.EntryId, cancellationToken); var merged = interaction.InputValues.ToDictionary(value => value.Key, value => value.Value.Clone(), StringComparer.Ordinal);
        foreach (var pending in await repository.ListPendingActionsAsync(workspaceId, interactionId, cancellationToken)) if (pending.Response is not null) foreach (var value in pending.Response.Values) merged[value.Key] = value.Value.Clone();
        var submission = await CreateTaskAsync(interaction with { Messages = [.. interaction.Messages, message] }, entry, merged, interaction.Attachments, interaction.Version, cancellationToken);
        resolved = await LinkPendingActionAsync(resolved, submission.Task, cancellationToken);
        await PublishAsync(new PendingActionResolvedEvent(EventId(), workspaceId.Value, Sequence(), now, action.Id.Value, resolved.WorkTaskId?.Value), cancellationToken);
        return new PendingActionResolution(resolved, submission.Action, submission.Interaction, submission.Task);
    }

    public async Task<WorkplaceInteraction> GetInteractionAsync(WorkspaceId workspaceId, InteractionId interactionId, CancellationToken cancellationToken) => await repository.GetInteractionAsync(workspaceId, interactionId, cancellationToken) ?? throw new KeyNotFoundException($"Interaction '{interactionId}' was not found in Workspace '{workspaceId}'.");
    public Task<IReadOnlyList<WorkplaceInteraction>> ListInteractionsAsync(WorkspaceId workspaceId, int take, CancellationToken cancellationToken) => repository.ListInteractionsAsync(workspaceId, take, cancellationToken);
    public Task<IReadOnlyList<ConversationMessage>> ListMessagesAsync(WorkspaceId workspaceId, InteractionId interactionId, CancellationToken cancellationToken) => repository.ListMessagesAsync(workspaceId, interactionId, cancellationToken);
    public Task<IReadOnlyList<PendingAction>> ListPendingActionsAsync(WorkspaceId workspaceId, InteractionId interactionId, CancellationToken cancellationToken) => repository.ListPendingActionsAsync(workspaceId, interactionId, cancellationToken);

    public async Task<MessageContinuation> AddMessageAsync(WorkspaceId workspaceId, InteractionId interactionId, string content, CancellationToken cancellationToken)
    {
        var interaction = await GetInteractionAsync(workspaceId, interactionId, cancellationToken);
        if (string.IsNullOrWhiteSpace(content)) throw new WorkValidationException("message_required", "A message is required.");
        if (interaction.Status == InteractionStatus.Closed) throw new WorkTransitionException("interaction_closed", "This conversation is closed.");
        if (interaction.Status == InteractionStatus.WaitingForUser) throw new WorkTransitionException("pending_action_required", "Answer the pending question before sending another message.");
        var now = timeProvider.GetUtcNow();
        var message = new ConversationMessage(Guid.NewGuid(), workspaceId, interactionId, interaction.TaskId, ConversationRole.User, content.Trim(), now);
        await repository.AddMessageAsync(message, cancellationToken);
        await PublishAsync(new MessageAddedEvent(EventId(), workspaceId.Value, Sequence(), now, message), cancellationToken);
        var entry = await GetEntryAsync(interaction.WorkspaceId, interaction.EntryId, cancellationToken);
        if (!entry.Behavior.AllowConversation || entry.Behavior.Conversation?.Enabled == false) throw new WorkTransitionException("conversation_disabled", "This Entry does not allow conversational continuation.");

        if (entry.Behavior.TaskCreationMode == TaskCreationMode.Never)
        {
            var response = new RespondAction($"I have added your follow-up: {message.Content}");
            var agentMessage = await AddAgentMessageAsync(interaction, response.Content, now, cancellationToken);
            var updated = interaction with { Status = InteractionStatus.Idle, LastActivityAt = now, LastTriggerMessageId = message.Id, ImmediateResult = response, Messages = [.. interaction.Messages, message, agentMessage], Version = interaction.Version + 1 };
            await repository.SaveInteractionAsync(updated, interaction.Version, cancellationToken);
            await PublishInteractionAsync(updated, cancellationToken);
            return new MessageContinuation(message, updated, response, null);
        }

        if (interaction.TaskId is null)
        {
            var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["request"] = JsonSerializer.SerializeToElement(message.Content) };
            var submission = await CreateTaskAsync(interaction with { Messages = [.. interaction.Messages, message], LastTriggerMessageId = message.Id }, entry, values, [], interaction.Version, cancellationToken);
            return new MessageContinuation(message, submission.Interaction, submission.Action, submission.Task);
        }

        var context = await BuildContinuationContextAsync(interaction, message, cancellationToken);
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [WorkspaceMetadata] = workspaceId.ToString(),
            [EntryMetadata] = interaction.EntryId.Value,
            [InteractionMetadata] = interaction.Id.ToString(),
            [TaskMetadata] = interaction.TaskId.Value.ToString(),
            [TriggerMessageMetadata] = message.Id.ToString("D"),
            [ContinuationMetadata] = bool.TrueString
        };
        if (!string.IsNullOrWhiteSpace(context.LastFlowRunId)) metadata[ParentFlowRunMetadata] = context.LastFlowRunId;
        var target = entry.Behavior.Conversation?.ContinuationTarget ?? entry.ResolvedTarget;
        var stored = await workItems.SubmitAsync(new SubmitWorkItemCommand(
            interaction.WorkspaceId, "entry-continuation", message.Content, entry.DisplayName, $"Continuation of {entry.DisplayName}", Metadata: metadata,
            Inputs: [new WorkInput(Structured: JsonSerializer.SerializeToElement(context))],
            Flow: WorkplaceValidation.FlowReferenceFrom(target)), cancellationToken);
        var task = ToTask(stored.Value, interaction.TaskId);
        var responseText = "I’m creating an updated version from the previous result.";
        var agentResponse = await AddAgentMessageAsync(interaction, responseText, now, cancellationToken);
        var action = new CreateTaskAction(interaction.TaskId.Value, task.Title, task.Description, $"/tasks/{interaction.TaskId.Value}");
        var processing = interaction with
        {
            Status = InteractionStatus.Processing,
            LastActivityAt = now,
            LastTriggerMessageId = message.Id,
            ImmediateResult = action,
            Messages = [.. interaction.Messages, message, agentResponse],
            Version = interaction.Version + 1
        };
        await repository.SaveInteractionAsync(processing, interaction.Version, cancellationToken);
        await PublishInteractionAsync(processing, cancellationToken);
        return new MessageContinuation(message, processing, action, task);
    }

    public async Task<WorkTask> GetTaskAsync(WorkspaceId workspaceId, WorkTaskId taskId, CancellationToken cancellationToken)
    {
        var anchor = (await workItems.GetAsync(workspaceId, taskId.ToWorkItemId(), cancellationToken))?.Value ?? throw new KeyNotFoundException($"Task '{taskId}' was not found.");
        RequireWorkspace(anchor, workspaceId);
        var continuations = await workItems.QueryAsync(new WorkItemQuery(workspaceId, Take: 100, AnchorTaskId: taskId.ToString(), SortBy: WorkItemSortField.CreatedAt), cancellationToken);
        return ProjectTask(anchor, LatestExecution(anchor, continuations.Items.Select(value => value.Value).ToArray()), taskId);
    }

    public async Task<(WorkspaceId WorkspaceId, WorkTask Task)> GetOperationalTaskAsync(WorkspaceId workspaceId, WorkTaskId taskId, CancellationToken cancellationToken)
    {
        var anchor = (await workItems.GetAsync(workspaceId, taskId.ToWorkItemId(), cancellationToken))?.Value ?? throw new KeyNotFoundException($"Task '{taskId}' was not found.");
        if (anchor.Metadata.ContainsKey(TaskMetadata))
            throw new KeyNotFoundException($"Task '{taskId}' was not found.");
        RequireWorkspace(anchor, workspaceId);
        return (workspaceId, await GetTaskAsync(workspaceId, taskId, cancellationToken));
    }

    public async Task<OperationalWorkTaskPage> QueryOperationalTasksAsync(
        WorkspaceId workspaceId, WorkTaskStatus? status, string? search, bool? hasPendingAction,
        int page, int pageSize, WorkItemSortField sort, WorkItemSortDirection direction, CancellationToken cancellationToken,
        DateTimeOffset? updatedFrom = null, DateTimeOffset? updatedTo = null)
    {
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 100);
        var query = new WorkItemQuery(
            workspaceId, Skip: (page - 1) * pageSize, Take: pageSize, Status: status is null ? null : ToItemStatus(status.Value),
            SortBy: sort, SortDirection: direction,
            IsContinuation: false, Search: search, HasPendingAction: hasPendingAction, OperationalTasks: true,
            UpdatedFrom: updatedFrom, UpdatedTo: updatedTo);
        var anchors = await workItems.QueryAsync(query, cancellationToken);
        var tasks = new List<WorkTask>(anchors.Items.Count);
        foreach (var stored in anchors.Items)
        {
            var anchor = stored.Value; var taskId = WorkTaskId.FromWorkItem(anchor.Id);
            RequireWorkspace(anchor, workspaceId);
            var continuations = await workItems.QueryAsync(new WorkItemQuery(workspaceId, Take: 100, AnchorTaskId: taskId.ToString(), SortBy: WorkItemSortField.CreatedAt), cancellationToken);
            tasks.Add(ProjectTask(anchor, LatestExecution(anchor, continuations.Items.Select(value => value.Value).ToArray()), taskId));
        }
        return new OperationalWorkTaskPage(tasks, Math.Max(0, anchors.TotalCount));
    }

    public async Task<IReadOnlyList<WorkTask>> ListTasksAsync(WorkspaceId workspaceId, WorkTaskStatus? status, CancellationToken cancellationToken)
    {
        var page = await workItems.QueryAsync(new WorkItemQuery(workspaceId, Take: 500), cancellationToken);
        var items = page.Items.Select(value => value.Value).Where(value => value.Metadata.TryGetValue(WorkspaceMetadata, out var workspace) && workspace == workspaceId.ToString()).ToArray();
        return items.Where(value => !value.Metadata.ContainsKey(TaskMetadata))
            .Select(anchor => ProjectTask(anchor, LatestExecution(anchor, items), WorkTaskId.FromWorkItem(anchor.Id)))
            .Where(task => status is null || task.Status == status)
            .OrderByDescending(value => value.UpdatedAt)
            .ToArray();
    }

    public async Task<WorkTask> PauseTaskAsync(WorkspaceId workspaceId, WorkTaskId taskId, CancellationToken token) { var current = await GetCurrentExecutionAsync(workspaceId, taskId, token); await workItems.PauseAsync(workspaceId, current.Id, token); return await GetTaskAsync(workspaceId, taskId, token); }
    public async Task<WorkTask> ResumeTaskAsync(WorkspaceId workspaceId, WorkTaskId taskId, CancellationToken token) { var current = await GetCurrentExecutionAsync(workspaceId, taskId, token); await workItems.ResumeAsync(workspaceId, current.Id, token); return await GetTaskAsync(workspaceId, taskId, token); }
    public async Task<WorkTask> CancelTaskAsync(WorkspaceId workspaceId, WorkTaskId taskId, CancellationToken token) { var current = await GetCurrentExecutionAsync(workspaceId, taskId, token); await workItems.CancelAsync(workspaceId, current.Id, null, token); return await GetTaskAsync(workspaceId, taskId, token); }
    public Task<IReadOnlyList<WorkTaskActivity>> ListActivitiesAsync(WorkspaceId workspaceId, WorkTaskId taskId, CancellationToken token) => repository.ListActivitiesAsync(workspaceId, taskId, token);
    public Task<IReadOnlyList<WorkTaskResult>> ListResultsAsync(WorkspaceId workspaceId, WorkTaskId taskId, CancellationToken token) => repository.ListResultsAsync(workspaceId, taskId, token);
    public Task<IReadOnlyList<WorkTaskArtifact>> ListArtifactsAsync(WorkspaceId workspaceId, WorkTaskId taskId, CancellationToken token) => repository.ListArtifactsAsync(workspaceId, taskId, token);
    public async Task<WorkTaskArtifact> GetArtifactAsync(WorkspaceId workspaceId, WorkTaskId taskId, WorkTaskArtifactId artifactId, CancellationToken token) => await repository.GetArtifactAsync(workspaceId, taskId, artifactId, token) ?? throw new KeyNotFoundException($"Artifact '{artifactId}' was not found in Workspace '{workspaceId}'.");
    public Task<IReadOnlyList<WorkNotification>> ListNotificationsAsync(WorkspaceId workspaceId, bool? unreadOnly, CancellationToken token) => repository.ListNotificationsAsync(workspaceId, unreadOnly, token);
    public async Task<int> UnreadCountAsync(WorkspaceId workspaceId, CancellationToken token) => (await repository.ListNotificationsAsync(workspaceId, true, token)).Count;
    public async Task<WorkNotification> MarkNotificationReadAsync(WorkspaceId workspaceId, WorkNotificationId id, CancellationToken token) { var value = await repository.GetNotificationAsync(workspaceId, id, token) ?? throw new KeyNotFoundException($"Notification '{id}' was not found."); if (value.ReadAt is not null) return value; var updated = value with { ReadAt = timeProvider.GetUtcNow(), Version = value.Version + 1 }; await repository.SaveNotificationAsync(updated, value.Version, token); await PublishAsync(new NotificationUpdatedEvent(EventId(), workspaceId.Value, Sequence(), updated.ReadAt.Value, updated), token); return updated; }
    public async Task MarkAllNotificationsReadAsync(WorkspaceId workspaceId, CancellationToken token) { foreach (var value in await repository.ListNotificationsAsync(workspaceId, true, token)) await MarkNotificationReadAsync(workspaceId, value.Id, token); await PublishAsync(new UnreadNotificationCountChangedEvent(EventId(), workspaceId.Value, Sequence(), timeProvider.GetUtcNow(), 0), token); }

    public static WorkplaceAction CurrentAction(WorkTask task) => task.Status switch { WorkTaskStatus.ActionRequired => new RespondAction("A response is required."), WorkTaskStatus.Failed => new ShowErrorAction(task.Error?.Code ?? "Task failed", task.Error?.Message), WorkTaskStatus.Completed => new ShowResultAction("Result", task.Result?.Contents.FirstOrDefault()?.Text, task.Result?.Contents.FirstOrDefault()?.Structured), _ => new RespondAction("Agentstration is working on your request.") };

    private async Task<EntrySubmission> CreateTaskAsync(WorkplaceInteraction interaction, EntryResource entry, IReadOnlyDictionary<string, JsonElement> values, IReadOnlyList<WorkAttachment>? attachments, long expectedInteractionVersion, CancellationToken token)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal) { [WorkspaceMetadata] = interaction.WorkspaceId.ToString(), [EntryMetadata] = interaction.EntryId.Value, [InteractionMetadata] = interaction.Id.ToString() };
        var inputs = values.Select(value => new WorkInput(Structured: JsonSerializer.SerializeToElement(new { name = value.Key, value = value.Value }))).ToArray();
        WorkTask? task = null;
        CreateTaskAction? action = null;
        WorkplaceInteraction? updated = null;
        var stored = await workItems.SubmitAsync(
            new SubmitWorkItemCommand(interaction.WorkspaceId, "entry", Instruction(entry, values), entry.DisplayName, entry.Description, Metadata: metadata, Inputs: inputs, Attachments: attachments, Flow: WorkplaceValidation.FlowReferenceFrom(entry.ResolvedTarget)),
            async (queued, cancellationToken) =>
            {
                task = ToTask(queued.Value);
                action = new CreateTaskAction(task.Id, task.Title, task.Description, $"/tasks/{task.Id}");
                var now = timeProvider.GetUtcNow();
                var response = string.Equals(entry.Name, "prepare-report", StringComparison.Ordinal)
                    ? "I’ll prepare a standard report and highlight the main changes."
                    : "I’ve started the work and will keep this conversation updated.";
                var agentMessage = await AddAgentMessageAsync(interaction with { TaskId = task.Id }, response, now, cancellationToken);
                updated = interaction with { Status = InteractionStatus.Processing, TaskId = task.Id, PendingActionId = null, ImmediateResult = action, LastActivityAt = now, Messages = [.. interaction.Messages, agentMessage], Version = expectedInteractionVersion + 1 };
                await repository.SaveInteractionAsync(updated, expectedInteractionVersion, cancellationToken);
            },
            token);
        task ??= ToTask(stored.Value);
        action ??= new CreateTaskAction(task.Id, task.Title, task.Description, $"/tasks/{task.Id}");
        updated ??= interaction with { Status = InteractionStatus.Processing, TaskId = task.Id, PendingActionId = null, ImmediateResult = action, Version = expectedInteractionVersion + 1 };
        await PublishInteractionAsync(updated, token);
        await PublishAsync(new TaskCreatedEvent(EventId(), interaction.WorkspaceId.Value, Sequence(), updated.LastActivityAt, task.Id.Value), token);
        return new EntrySubmission(updated, action, task);
    }

    private (PendingAction Action, WorkplaceAction Contract) CreatePendingAction(WorkplaceInteraction interaction, PendingActionKind kind, string title, string? description, IReadOnlyList<EntryFieldDefinition> fields, int step, DateTimeOffset now)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_'); var id = PendingActionId.New();
        var action = new PendingAction { Id = id, WorkspaceId = interaction.WorkspaceId, InteractionId = interaction.Id, WorkTaskId = interaction.TaskId, FlowRunId = $"resume-{interaction.Id.Value:N}", Kind = kind, Title = title, Description = description, Fields = fields, CreatedAt = now, ExpiresAt = now.AddHours(24), ResumeTokenHash = HashToken(token), ResumeStep = step };
        WorkplaceAction contract = kind switch { PendingActionKind.ConfirmationRequired => new RequestConfirmationAction(title, description, id, token), PendingActionKind.ChoiceRequired => new RequestChoiceAction(title, description, fields.Single().Options, id, token, fields.Single().Name), _ => new RequestInputAction(title, description, fields, id, token) }; return (action, contract);
    }

    private async Task<InteractionContinuationContext> BuildContinuationContextAsync(WorkplaceInteraction interaction, ConversationMessage trigger, CancellationToken token)
    {
        var recent = (await repository.ListMessagesAsync(interaction.WorkspaceId, interaction.Id, token)).TakeLast(12)
            .Select(value => new ConversationContextMessage(value.Id, value.Role, value.Content, value.CreatedAt)).ToArray();
        var results = interaction.TaskId is null ? [] : (await repository.ListResultsAsync(interaction.WorkspaceId, interaction.TaskId.Value, token))
            .Select(value => new ContinuationResultReference(value.Id, value.Title, value.FlowRunId, value.Sequence)).ToArray();
        var taskArtifacts = interaction.TaskId is null ? [] : (await repository.ListArtifactsAsync(interaction.WorkspaceId, interaction.TaskId.Value, token))
            .Select(value => new ContinuationArtifactReference(value.Id, value.Name, value.FlowRunId, value.Sequence)).ToArray();
        var lastFlowRunId = results.OrderByDescending(value => value.Sequence).Select(value => value.FlowRunId).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? interaction.LastFlowRunId;
        return new InteractionContinuationContext(interaction.WorkspaceId, interaction.Id, interaction.TaskId, lastFlowRunId, trigger.Id, recent, results, taskArtifacts, interaction.EntryId);
    }

    private async Task<ConversationMessage> AddAgentMessageAsync(WorkplaceInteraction interaction, string content, DateTimeOffset now, CancellationToken token)
    {
        var message = new ConversationMessage(Guid.NewGuid(), interaction.WorkspaceId, interaction.Id, interaction.TaskId, ConversationRole.Agentstration, content, now);
        await repository.AddMessageAsync(message, token);
        await PublishAsync(new MessageAddedEvent(EventId(), interaction.WorkspaceId.Value, Sequence(), now, message), token);
        return message;
    }

    private Task PublishInteractionAsync(WorkplaceInteraction interaction, CancellationToken token) =>
        PublishAsync(new InteractionUpdatedEvent(EventId(), interaction.WorkspaceId.Value, Sequence(), interaction.LastActivityAt, interaction.Id.Value, interaction.Status), token);

    private async Task<PendingAction> LinkPendingActionAsync(PendingAction action, WorkTask? task, CancellationToken token)
    {
        if (task is null || action.WorkTaskId is not null) return action;
        var linked = action with { WorkTaskId = task.Id, Version = action.Version + 1 };
        await repository.SavePendingActionAsync(linked, action.Version, token);
        return linked;
    }

    private async Task<WorkItem> GetCurrentExecutionAsync(WorkspaceId workspaceId, WorkTaskId taskId, CancellationToken token)
    {
        var anchor = (await workItems.GetAsync(workspaceId, taskId.ToWorkItemId(), token))?.Value ?? throw new KeyNotFoundException($"Task '{taskId}' was not found.");
        RequireWorkspace(anchor, workspaceId);
        var page = await workItems.QueryAsync(new WorkItemQuery(workspaceId, Take: 100, AnchorTaskId: taskId.ToString(), SortBy: WorkItemSortField.CreatedAt), token);
        return LatestExecution(anchor, page.Items.Select(value => value.Value).ToArray());
    }

    private static WorkItem LatestExecution(WorkItem anchor, IReadOnlyList<WorkItem> items) => items
        .Append(anchor)
        .Where(value => value.Id == anchor.Id || value.Metadata.GetValueOrDefault(TaskMetadata) == WorkTaskId.FromWorkItem(anchor.Id).ToString())
        .OrderByDescending(value => value.CreatedAt)
        .First();

    private static WorkItemStatus ToItemStatus(WorkTaskStatus status) => status switch
    {
        WorkTaskStatus.Draft => WorkItemStatus.Pending,
        WorkTaskStatus.Pending => WorkItemStatus.Pending,
        WorkTaskStatus.Running => WorkItemStatus.Running,
        WorkTaskStatus.ActionRequired => WorkItemStatus.WaitingForInput,
        WorkTaskStatus.Paused => WorkItemStatus.Paused,
        WorkTaskStatus.Completed => WorkItemStatus.Completed,
        WorkTaskStatus.Failed => WorkItemStatus.Failed,
        WorkTaskStatus.Cancelled => WorkItemStatus.Cancelled,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    private static WorkTask ProjectTask(WorkItem anchor, WorkItem latest, WorkTaskId publicId)
    {
        var projected = ToTask(latest, publicId);
        return projected with { Title = anchor.Title ?? anchor.Instruction, Description = anchor.Description, CreatedAt = anchor.CreatedAt };
    }

    private async Task CreateNotificationAsync(WorkspaceId workspaceId, WorkNotificationKind kind, string title, string message, InteractionId? interactionId, WorkTaskId? taskId, PendingActionId? actionId, string? url, CancellationToken token)
    {
        var notification = new WorkNotification { Id = WorkNotificationId.New(), WorkspaceId = workspaceId, Kind = kind, Title = title, Message = message, CreatedAt = timeProvider.GetUtcNow(), InteractionId = interactionId, WorkTaskId = taskId, PendingActionId = actionId, ActionUrl = url }; await repository.CreateNotificationAsync(notification, token); await PublishAsync(new NotificationCreatedEvent(EventId(), workspaceId.Value, Sequence(), notification.CreatedAt, notification), token); await PublishAsync(new UnreadNotificationCountChangedEvent(EventId(), workspaceId.Value, Sequence(), notification.CreatedAt, await UnreadCountAsync(workspaceId, token)), token);
    }

    private static void ValidatePendingResponse(PendingAction action, IReadOnlyDictionary<string, JsonElement> values) { if (action.Kind == PendingActionKind.ConfirmationRequired && (!values.TryGetValue("confirmed", out var confirmed) || confirmed.ValueKind is not JsonValueKind.True and not JsonValueKind.False)) throw new WorkValidationException("confirmation_required", "A boolean confirmation response is required."); WorkplaceValidation.ValidateFields(action.Fields, values); }
    private static string ResponseText(PendingAction action, IReadOnlyDictionary<string, JsonElement> values) => action.Kind == PendingActionKind.ConfirmationRequired ? values["confirmed"].GetBoolean() ? "Confirmed" : "Declined" : string.Join(", ", values.Select(value => $"{value.Key}: {value.Value}"));
    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static bool TokenMatches(string token, string expectedHash) { var actual = Encoding.ASCII.GetBytes(HashToken(token)); var expected = Encoding.ASCII.GetBytes(expectedHash); return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected); }
    private static string Instruction(EntryResource entry, IReadOnlyDictionary<string, JsonElement> values) { var primary = entry.Presentation.Fields.SingleOrDefault(field => field.Role == EntryFieldRole.PrimaryInput); if (primary is not null && values.TryGetValue(primary.Name, out var request) && request.ValueKind == JsonValueKind.String) return request.GetString()!.Trim(); var text = string.Join(Environment.NewLine, values.Where(value => value.Value.ValueKind == JsonValueKind.String).Select(value => $"{value.Key}: {value.Value.GetString()}")); return string.IsNullOrWhiteSpace(text) ? entry.DisplayName : text; }
    private static void RequireWorkspace(WorkItem item, WorkspaceId workspaceId) { if (item.WorkspaceId != workspaceId) throw new KeyNotFoundException($"Task '{item.Id}' was not found in Workspace '{workspaceId}'."); }
    internal static WorkTask ToTask(WorkItem item, WorkTaskId? publicId = null) { if (!item.Metadata.TryGetValue(EntryMetadata, out var entryId) || !item.Metadata.TryGetValue(InteractionMetadata, out var interactionId) || !Guid.TryParse(interactionId, out var interactionGuid)) throw new InvalidOperationException($"Work item '{item.Id}' is not a Workplace Task."); item.Metadata.TryGetValue(FlowRunMetadata, out var flowRunId); if (flowRunId is null) item.Result?.Metadata.TryGetValue(FlowRunMetadata, out flowRunId); return new WorkTask(publicId ?? WorkTaskId.FromWorkItem(item.Id), item.WorkspaceId, new(entryId), new(interactionGuid), item.Title ?? item.Instruction, item.Description, ToTaskStatus(item.Status), item.CreatedAt, item.UpdatedAt, flowRunId, item.Messages, item.Interactions, item.Result?.Artifacts ?? [], item.Result, item.Error, item.Version); }
    internal static WorkTaskStatus ToTaskStatus(WorkItemStatus status) => status switch { WorkItemStatus.Pending or WorkItemStatus.Queued => WorkTaskStatus.Pending, WorkItemStatus.Running => WorkTaskStatus.Running, WorkItemStatus.WaitingForInput or WorkItemStatus.WaitingForApproval => WorkTaskStatus.ActionRequired, WorkItemStatus.Paused => WorkTaskStatus.Paused, WorkItemStatus.Completed => WorkTaskStatus.Completed, WorkItemStatus.Failed => WorkTaskStatus.Failed, WorkItemStatus.Cancelled => WorkTaskStatus.Cancelled, _ => throw new ArgumentOutOfRangeException(nameof(status), status, null) };
    public static PendingActionContract ToContract(PendingAction value) => new(value.Id.Value, value.WorkspaceId.Value, value.InteractionId.Value, value.WorkTaskId?.Value, value.FlowRunId, value.Kind, value.Status, value.Title, value.Description, value.Fields, value.CreatedAt, value.ExpiresAt, value.ResolvedAt, value.Version);
    private async Task PublishAsync(WorkplaceEventContract value, CancellationToken token) { foreach (var sink in eventSinks) await sink.PublishAsync(value, token); }
    private long Sequence() => Interlocked.Increment(ref eventSequence);
    private static string EventId() => Guid.NewGuid().ToString("N");
}
