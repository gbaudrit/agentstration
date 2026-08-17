using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Application.Work;
using Agentstration.Flow;
using Agentstration.Flow.Application;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Agentstration.Work.Storage.Abstractions;
using Microsoft.Extensions.Logging;

namespace Agentstration.Infrastructure.Flows;

public sealed class WorkplaceFlowInputProjectionSink(
    IWorkplaceRepository repository,
    WorkItemService workItems,
    TimeProvider timeProvider,
    IEnumerable<IWorkplaceEventSink> eventSinks,
    ILogger<WorkplaceFlowInputProjectionSink> logger) : IFlowInputRequestSink
{
    private long eventSequence;

    public async Task PublishRequestedAsync(FlowRun run, InputRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(run.WorkplaceWorkspaceId)
            || !Guid.TryParse(run.InteractionId, out var interactionId))
            return;

        try
        {
            var workspaceId = new WorkplaceWorkspaceId(run.WorkplaceWorkspaceId);
            var interaction = await repository.GetInteractionAsync(workspaceId, new(interactionId), cancellationToken);
            if (interaction is null) return;

            var actionId = new PendingActionId(DeterministicGuid(request.Id));
            var existing = await repository.GetPendingActionAsync(workspaceId, actionId, cancellationToken);
            if (existing is not null && interaction.PendingActionId == actionId && HasAction(interaction.ImmediateResult, actionId))
                return;

            var token = NewToken();
            var fields = Fields(request);
            var kind = request.Type switch
            {
                InputRequestType.Choice => PendingActionKind.ChoiceRequired,
                InputRequestType.Confirmation => PendingActionKind.ConfirmationRequired,
                _ => PendingActionKind.InputRequired
            };
            var action = existing ?? new PendingAction
            {
                Id = actionId,
                WorkspaceId = workspaceId,
                InteractionId = interaction.Id,
                WorkTaskId = Guid.TryParse(run.WorkTaskId, out var taskId) ? new WorkTaskId(taskId) : interaction.TaskId,
                FlowRunId = run.Id,
                ExternalInputRequestId = request.Id,
                Kind = kind,
                Title = request.Prompt,
                Description = "The workflow needs your response before it can continue.",
                Fields = fields,
                CreatedAt = request.CreatedAt,
                ExpiresAt = request.ExpiresAt,
                ResumeTokenHash = HashToken(token)
            };
            if (existing is null)
            {
                await repository.CreatePendingActionAsync(action, cancellationToken);
            }
            else
            {
                action = existing with { ResumeTokenHash = HashToken(token), Version = existing.Version + 1 };
                await repository.SavePendingActionAsync(action, existing.Version, cancellationToken);
            }

            var contract = ToAction(action, token);
            var now = timeProvider.GetUtcNow();
            var updated = interaction with
            {
                Status = InteractionStatus.WaitingForUser,
                PendingActionId = action.Id,
                ImmediateResult = contract,
                LastActivityAt = now,
                Version = interaction.Version + 1
            };
            await repository.SaveInteractionAsync(updated, interaction.Version, cancellationToken);

            if (action.WorkTaskId is { } workTaskId)
            {
                var stored = await workItems.GetAsync(workTaskId.ToWorkItemId(), cancellationToken);
                if (stored?.Value is { Status: WorkItemStatus.Running, CurrentExecutionId: { } executionId })
                {
                    await workItems.ApplyExecutionEventAsync(new WorkExecutionInputRequested(
                        Guid.NewGuid(), stored.Value.Id, executionId, now, request.Prompt), cancellationToken);
                }
            }

            if (existing is null)
            {
                var notification = new WorkNotification
                {
                    Id = WorkNotificationId.New(),
                    WorkspaceId = workspaceId,
                    Kind = WorkNotificationKind.ActionRequired,
                    Title = request.Prompt,
                    Message = "A workflow is waiting for your response.",
                    CreatedAt = now,
                    InteractionId = interaction.Id,
                    WorkTaskId = action.WorkTaskId,
                    PendingActionId = action.Id,
                    ActionUrl = $"/interactions/{interaction.Id}"
                };
                await repository.CreateNotificationAsync(notification, cancellationToken);
                await PublishAsync(new NotificationCreatedEvent(EventId(), workspaceId.Value, Sequence(), now, notification), cancellationToken);
            }
            await PublishAsync(new PendingActionCreatedEvent(EventId(), workspaceId.Value, Sequence(), now, WorkplaceService.ToContract(action)), cancellationToken);
            await PublishAsync(new InteractionUpdatedEvent(EventId(), workspaceId.Value, Sequence(), now, interaction.Id.Value, updated.Status), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not project input request {InputRequestId} for Flow Run {FlowRunId} into Workplace", request.Id, run.Id);
        }
    }

    private static IReadOnlyList<EntryFieldDefinition> Fields(InputRequest request) => request.Type switch
    {
        InputRequestType.Confirmation => [],
        InputRequestType.Choice =>
        [
            new EntryFieldDefinition
            {
                Name = "response",
                Label = request.Prompt,
                Type = EntryFieldType.Choice,
                Required = true,
                Options = request.Options.Select(value => new EntryFieldOption(value, value)).ToArray()
            }
        ],
        _ =>
        [
            new EntryFieldDefinition
            {
                Name = "response",
                Label = request.Prompt,
                Type = EntryFieldType.Textarea,
                Required = true
            }
        ]
    };

    private static WorkplaceAction ToAction(PendingAction action, string token) => action.Kind switch
    {
        PendingActionKind.ConfirmationRequired => new RequestConfirmationAction(action.Title, action.Description, action.Id, token),
        PendingActionKind.ChoiceRequired => new RequestChoiceAction(action.Title, action.Description, action.Fields.Single().Options, action.Id, token, action.Fields.Single().Name),
        _ => new RequestInputAction(action.Title, action.Description, action.Fields, action.Id, token)
    };

    private static bool HasAction(WorkplaceAction? action, PendingActionId id) => action switch
    {
        RequestInputAction value => value.PendingActionId == id,
        RequestChoiceAction value => value.PendingActionId == id,
        RequestConfirmationAction value => value.PendingActionId == id,
        _ => false
    };

    private Task PublishAsync(WorkplaceEventContract value, CancellationToken token) =>
        Task.WhenAll(eventSinks.Select(sink => sink.PublishAsync(value, token)));

    private long Sequence() => Interlocked.Increment(ref eventSequence);
    private static string EventId() => Guid.NewGuid().ToString("N");
    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static Guid DeterministicGuid(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));
}

public sealed class WorkplaceFlowInputResponder(FlowRunService flowRuns, WorkItemService workItems) : IWorkplaceExternalInputResponder
{
    public bool CanRespond(PendingAction action) => action.ExternalInputRequestId is not null && action.FlowRunId is not null;

    public async Task RespondAsync(
        PendingAction action,
        IReadOnlyDictionary<string, JsonElement> values,
        string principalId,
        CancellationToken cancellationToken)
    {
        var value = action.Kind == PendingActionKind.ConfirmationRequired
            ? values["confirmed"]
            : values["response"];
        await flowRuns.RespondAsync(action.FlowRunId!, action.ExternalInputRequestId!, value, principalId, cancellationToken);
        if (action.WorkTaskId is { } taskId)
        {
            var stored = await workItems.GetAsync(taskId.ToWorkItemId(), cancellationToken);
            if (stored?.Value.Status == WorkItemStatus.WaitingForInput)
                await workItems.ProvideInputAsync(stored.Value.Id, new WorkInput(Structured: value.Clone()), principalId, cancellationToken);
        }
    }
}
