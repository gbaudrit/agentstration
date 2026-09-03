using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Agentstration.Work.Storage.Abstractions;

namespace Agentstration.Application.Work;

public sealed partial class WorkplaceService
{
    public async Task<EntrySubmission> SubmitAsync(SubmitEntryCommand command, CancellationToken cancellationToken)
    {
        var dashboards = await repository.ListDashboardsAsync(command.WorkspaceId, cancellationToken);
        if (!dashboards.Any(dashboard => dashboard.Entries.Any(reference => reference.EntryResourceId == command.EntryId)))
            throw new WorkValidationException("entry_not_in_workspace", "The Entry is not exposed by a published Dashboard in the selected Workspace.");
        var entry = await GetEntryAsync(command.WorkspaceId, command.EntryId, cancellationToken); WorkplaceValidation.ValidateSubmission(entry, command.Values);
        var now = timeProvider.GetUtcNow(); var interaction = new WorkplaceInteraction { Id = InteractionId.New(), WorkspaceId = command.WorkspaceId, EntryId = command.EntryId, EntrySnapshot = entry, StartedAt = now, LastActivityAt = now, InputValues = command.Values.ToDictionary(value => value.Key, value => value.Value.Clone(), StringComparer.Ordinal), Attachments = command.Attachments ?? [] };
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

    public Task<PendingActionResolution> RespondAsync(
        WorkspaceId workspaceId,
        InteractionId interactionId,
        PendingActionId pendingActionId,
        string resumeToken,
        IReadOnlyDictionary<string, JsonElement> values,
        CancellationToken cancellationToken) =>
        RespondAsync(workspaceId, interactionId, pendingActionId, resumeToken, values, "workplace-user", cancellationToken);

    public async Task<PendingActionResolution> RespondAsync(
        WorkspaceId workspaceId,
        InteractionId interactionId,
        PendingActionId pendingActionId,
        string resumeToken,
        IReadOnlyDictionary<string, JsonElement> values,
        string principalId,
        CancellationToken cancellationToken)
    {
        var interaction = await GetInteractionAsync(workspaceId, interactionId, cancellationToken);
        var action = await repository.GetPendingActionAsync(workspaceId, pendingActionId, cancellationToken) ?? throw new KeyNotFoundException($"PendingAction '{pendingActionId}' was not found in Workspace '{workspaceId}'.");
        if (action.InteractionId != interactionId) throw new KeyNotFoundException($"PendingAction '{pendingActionId}' does not belong to Interaction '{interactionId}'.");
        if (action.Status != PendingActionStatus.Pending) throw new WorkTransitionException("pending_action_already_resolved", "The PendingAction is no longer pending.");
        var now = timeProvider.GetUtcNow(); if (action.ExpiresAt is not null && action.ExpiresAt <= now) { var expired = action with { Status = PendingActionStatus.Expired, ResolvedAt = now, Version = action.Version + 1 }; await repository.SavePendingActionAsync(expired, action.Version, cancellationToken); throw new WorkTransitionException("pending_action_expired", "The PendingAction has expired."); }
        if (!TokenMatches(resumeToken, action.ResumeTokenHash)) throw new WorkValidationException("resume_token_invalid", "The resume token is invalid for this Workspace action.");
        ValidatePendingResponse(action, values);
        if (action.ExternalInputRequestId is not null)
        {
            var responder = externalInputResponders.SingleOrDefault(value => value.CanRespond(action))
                ?? throw new WorkTransitionException("external_input_responder_unavailable", "The runtime input responder is not available.");
            await responder.RespondAsync(action, values, principalId, cancellationToken);
        }
        var resolved = action with { Status = PendingActionStatus.Completed, ResolvedAt = now, Response = new PendingActionResponse(values.ToDictionary(value => value.Key, value => value.Value.Clone(), StringComparer.Ordinal), now), ResumeTokenHash = HashToken($"used:{Guid.NewGuid():N}"), Version = action.Version + 1 };
        await repository.SavePendingActionAsync(resolved, action.Version, cancellationToken);
        var message = new ConversationMessage(Guid.NewGuid(), workspaceId, interactionId, interaction.TaskId, ConversationRole.User, ResponseText(action, values), now, PendingActionId: action.Id);
        await repository.AddMessageAsync(message, cancellationToken); await PublishAsync(new MessageAddedEvent(EventId(), workspaceId.Value, Sequence(), now, message), cancellationToken);

        if (action.ExternalInputRequestId is not null)
        {
            var next = new RespondAction("Thanks. The workflow is continuing with your response.");
            interaction = interaction with
            {
                Status = InteractionStatus.Processing,
                PendingActionId = null,
                ImmediateResult = next,
                LastActivityAt = now,
                Messages = [.. interaction.Messages, message],
                Version = interaction.Version + 1
            };
            await repository.SaveInteractionAsync(interaction, interaction.Version - 1, cancellationToken);
            await PublishAsync(new PendingActionResolvedEvent(EventId(), workspaceId.Value, Sequence(), now, action.Id.Value, action.WorkTaskId?.Value), cancellationToken);
            await PublishInteractionAsync(interaction, cancellationToken);
            var task = action.WorkTaskId is null ? null : await GetTaskAsync(workspaceId, action.WorkTaskId.Value, cancellationToken);
            return new PendingActionResolution(resolved, next, interaction, task);
        }

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
            var guidedEntry = await ResolveInteractionEntryAsync(interaction, cancellationToken);
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

        var entry = await ResolveInteractionEntryAsync(interaction, cancellationToken); var merged = interaction.InputValues.ToDictionary(value => value.Key, value => value.Value.Clone(), StringComparer.Ordinal);
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

    public Task<IReadOnlyList<PendingAction>> ListPendingActionsForTaskAsync(WorkspaceId workspaceId, WorkTaskId taskId, CancellationToken cancellationToken) => repository.ListPendingActionsForTaskAsync(workspaceId, taskId, cancellationToken);

    public async Task<PendingActionResolution> RespondTaskPendingActionAsync(WorkspaceId workspaceId, WorkTaskId taskId, PendingActionId pendingActionId, IReadOnlyDictionary<string, JsonElement> values, string principalId, CancellationToken cancellationToken)
    {
        var action = await repository.GetPendingActionAsync(workspaceId, pendingActionId, cancellationToken) ?? throw new KeyNotFoundException($"PendingAction '{pendingActionId}' was not found in Workspace '{workspaceId}'.");
        if (action.WorkTaskId != taskId || action.InteractionId is not null) throw new KeyNotFoundException($"PendingAction '{pendingActionId}' does not belong to autonomous Task '{taskId}'.");
        if (action.Status != PendingActionStatus.Pending) throw new WorkTransitionException("pending_action_already_resolved", "The PendingAction is no longer pending.");
        var now = timeProvider.GetUtcNow();
        if (action.ExpiresAt is not null && action.ExpiresAt <= now) throw new WorkTransitionException("pending_action_expired", "The PendingAction has expired.");
        ValidatePendingResponse(action, values);
        var responder = externalInputResponders.SingleOrDefault(value => value.CanRespond(action)) ?? throw new WorkTransitionException("external_input_responder_unavailable", "The runtime input responder is not available.");
        await responder.RespondAsync(action, values, principalId, cancellationToken);
        var resolved = action with { Status = PendingActionStatus.Completed, ResolvedAt = now, Response = new PendingActionResponse(values.ToDictionary(value => value.Key, value => value.Value.Clone(), StringComparer.Ordinal), now), ResumeTokenHash = HashToken($"used:{Guid.NewGuid():N}"), Version = action.Version + 1 };
        await repository.SavePendingActionAsync(resolved, action.Version, cancellationToken);
        await PublishAsync(new PendingActionResolvedEvent(EventId(), workspaceId.Value, Sequence(), now, action.Id.Value, taskId.Value), cancellationToken);
        return new(resolved, new RespondAction("Thanks. The automated work is continuing with your response."), null, null);
    }

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
        var entry = await ResolveInteractionEntryAsync(interaction, cancellationToken);
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
        var responseMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["presentationKey"] = "ContinuationStarted"
        };
        var agentResponse = await AddAgentMessageAsync(interaction, responseText, now, cancellationToken, responseMetadata);
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
                updated = interaction with { Status = InteractionStatus.Processing, TaskId = task.Id, PendingActionId = null, ImmediateResult = action, LastActivityAt = now, Version = expectedInteractionVersion + 1 };
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

    private Task<EntryResource> ResolveInteractionEntryAsync(WorkplaceInteraction interaction, CancellationToken cancellationToken) =>
        interaction.EntrySnapshot is not null
            ? Task.FromResult(interaction.EntrySnapshot)
            : GetEntryAsync(interaction.WorkspaceId, interaction.EntryId, cancellationToken);

    private async Task<ConversationMessage> AddAgentMessageAsync(
        WorkplaceInteraction interaction,
        string content,
        DateTimeOffset now,
        CancellationToken token,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        var message = new ConversationMessage(Guid.NewGuid(), interaction.WorkspaceId, interaction.Id, interaction.TaskId, ConversationRole.Agentstration, content, now, Metadata: metadata);
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

    private static void ValidatePendingResponse(PendingAction action, IReadOnlyDictionary<string, JsonElement> values) { if (action.Kind == PendingActionKind.ConfirmationRequired && (!values.TryGetValue("confirmed", out var confirmed) || confirmed.ValueKind is not JsonValueKind.True and not JsonValueKind.False)) throw new WorkValidationException("confirmation_required", "A boolean confirmation response is required."); WorkplaceValidation.ValidateFields(action.Fields, values); }

    private static string ResponseText(PendingAction action, IReadOnlyDictionary<string, JsonElement> values) => action.Kind == PendingActionKind.ConfirmationRequired ? values["confirmed"].GetBoolean() ? "Confirmed" : "Declined" : string.Join(", ", values.Select(value => $"{value.Key}: {value.Value}"));

    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static bool TokenMatches(string token, string expectedHash) { var actual = Encoding.ASCII.GetBytes(HashToken(token)); var expected = Encoding.ASCII.GetBytes(expectedHash); return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected); }

    private static string Instruction(EntryResource entry, IReadOnlyDictionary<string, JsonElement> values) { var primary = entry.Presentation.Fields.SingleOrDefault(field => field.Role == EntryFieldRole.PrimaryInput); if (primary is not null && values.TryGetValue(primary.Name, out var request) && request.ValueKind == JsonValueKind.String) return request.GetString()!.Trim(); var text = string.Join(Environment.NewLine, values.Where(value => value.Value.ValueKind == JsonValueKind.String).Select(value => $"{value.Key}: {value.Value.GetString()}")); return string.IsNullOrWhiteSpace(text) ? entry.DisplayName : text; }

    public static PendingActionContract ToContract(PendingAction value) => new(value.Id.Value, value.WorkspaceId.Value, value.InteractionId?.Value, value.WorkTaskId?.Value, value.FlowRunId, value.Kind, value.Status, value.Title, value.Description, value.Fields, value.CreatedAt, value.ExpiresAt, value.ResolvedAt, value.Version);
}

