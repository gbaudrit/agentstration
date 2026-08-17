using System.Text;
using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Agentstration.Work.Storage.Abstractions;

namespace Agentstration.Application.Work;

public sealed class WorkplaceProjectionSink(
    IWorkplaceRepository repository,
    IArtifactStore artifacts,
    IEnumerable<IWorkplaceEventSink> eventSinks) : IWorkTaskEventSink
{
    private long sequence;

    public async Task PublishAsync(WorkItemSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (!TryContext(snapshot, out var workspaceId, out var interactionId, out var taskId)) return;
        var activityType = snapshot.History.LastOrDefault()?.Type switch
        {
            "WorkItemQueued" => WorkTaskActivityType.TaskCreated,
            "WorkItemStarted" => WorkTaskActivityType.TaskStarted,
            "WorkItemPaused" => WorkTaskActivityType.TaskPaused,
            "WorkItemResumed" => WorkTaskActivityType.TaskResumed,
            "WorkItemCancelled" => WorkTaskActivityType.TaskCancelled,
            "WorkItemInputRequested" or "WorkItemApprovalRequested" => WorkTaskActivityType.ActionRequired,
            "WorkItemCompleted" => WorkTaskActivityType.TaskCompleted,
            "WorkItemFailed" => WorkTaskActivityType.TaskFailed,
            _ => (WorkTaskActivityType?)null
        };
        if (activityType is not null)
        {
            var existing = await repository.ListActivitiesAsync(workspaceId, taskId, cancellationToken);
            var marker = $"{snapshot.Id}:{snapshot.Version}";
            if (!existing.Any(value => value.Metadata?.GetValueOrDefault("workVersion") == marker))
            {
                var activityTitle = snapshot.Metadata.ContainsKey("workplace.continuation") && activityType == WorkTaskActivityType.TaskCompleted ? "New version generated" : Title(activityType.Value);
                var activity = new WorkTaskActivity(WorkTaskActivityId.New(), workspaceId, taskId, activityType.Value, activityTitle, null, snapshot.UpdatedAt, Actor(activityType.Value), snapshot.Result?.Metadata.GetValueOrDefault("flowRunId"), new Dictionary<string, string> { ["workVersion"] = marker });
                await repository.AddActivityAsync(activity, cancellationToken);
                await EmitAsync(new TaskActivityAddedEvent(Id(), workspaceId.Value, Next(), snapshot.UpdatedAt, activity), cancellationToken);
            }
        }

        await EmitAsync(new TaskStatusChangedEvent(Id(), workspaceId.Value, Next(), snapshot.UpdatedAt, taskId.Value, WorkplaceService.ToTaskStatus(snapshot.Status), snapshot.Version), cancellationToken);
        if (snapshot.Status == WorkItemStatus.Running)
            await EmitAsync(new FlowRunStartedEvent(Id(), workspaceId.Value, Next(), snapshot.UpdatedAt, interactionId.Value, taskId.Value, snapshot.Metadata.GetValueOrDefault("workplace.parentFlowRunId")), cancellationToken);
        if (snapshot.Status == WorkItemStatus.Completed) await CompleteAsync(snapshot, workspaceId, interactionId, taskId, cancellationToken);
        if (snapshot.Status == WorkItemStatus.Failed)
        {
            await FailInteractionAsync(workspaceId, interactionId, taskId, snapshot.Error?.Message ?? "Agentstration could not complete the Task.", snapshot.UpdatedAt, cancellationToken);
            await NotifyAsync(workspaceId, WorkNotificationKind.TaskFailed, "Task failed", snapshot.Error?.Message ?? "Agentstration could not complete the Task.", taskId, snapshot.UpdatedAt, cancellationToken);
        }
    }

    private async Task CompleteAsync(WorkItemSnapshot snapshot, WorkspaceId workspaceId, InteractionId interactionId, WorkTaskId taskId, CancellationToken token)
    {
        var existingResults = await repository.ListResultsAsync(workspaceId, taskId, token);
        var content = snapshot.Result?.Contents.FirstOrDefault(); var structured = content?.Structured ?? JsonSerializer.SerializeToElement(content?.Text ?? string.Empty);
        var flowRunId = snapshot.Result?.Metadata.GetValueOrDefault("flowRunId");
        if (flowRunId is not null && existingResults.Any(value => value.FlowRunId == flowRunId)) return;
        var resultSequence = existingResults.Count + 1;
        var resultTitle = resultSequence switch { 1 => "Initial report", 2 => "Executive version", _ => $"Revised version {resultSequence}" };
        var result = new WorkTaskResult(WorkTaskResultId.New(), workspaceId, taskId, flowRunId, content?.Text is null ? WorkTaskResultKind.Structured : WorkTaskResultKind.Text, resultTitle, structured, snapshot.UpdatedAt, resultSequence);
        await repository.AddResultAsync(result, token); await EmitAsync(new TaskResultAddedEvent(Id(), workspaceId.Value, Next(), snapshot.UpdatedAt, result), token);
        var artifactText = content?.Text ?? structured.GetRawText(); await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(artifactText));
        var artifactName = resultSequence switch { 1 => "monthly-report.txt", 2 => "executive-summary.txt", _ => $"monthly-report-v{resultSequence}.txt" };
        var reference = await artifacts.SaveAsync(new ArtifactContent(artifactName, "text/plain; charset=utf-8", stream), token);
        var artifact = new WorkTaskArtifact(WorkTaskArtifactId.New(), workspaceId, taskId, flowRunId, artifactName, reference.ContentType, reference.Length, reference.StorageKey, snapshot.UpdatedAt, resultSequence);
        await repository.AddArtifactAsync(artifact, token); await EmitAsync(new TaskArtifactAddedEvent(Id(), workspaceId.Value, Next(), snapshot.UpdatedAt, new(artifact.Id.Value, artifact.WorkTaskId.Value, artifact.FlowRunId, artifact.Name, artifact.ContentType, artifact.Length, artifact.CreatedAt, artifact.Sequence)), token);
        if (flowRunId is not null) await EmitAsync(new FlowRunCompletedEvent(Id(), workspaceId.Value, Next(), snapshot.UpdatedAt, interactionId.Value, taskId.Value, flowRunId), token);
        await CompleteInteractionAsync(workspaceId, interactionId, taskId, flowRunId, resultTitle, snapshot.UpdatedAt, token);
        await NotifyAsync(workspaceId, WorkNotificationKind.TaskCompleted, resultSequence == 1 ? "Task completed" : "New version ready", $"{resultTitle} and its deliverable are ready.", taskId, snapshot.UpdatedAt, token);
    }

    private async Task CompleteInteractionAsync(WorkspaceId workspaceId, InteractionId interactionId, WorkTaskId taskId, string? flowRunId, string resultTitle, DateTimeOffset now, CancellationToken token)
    {
        var interaction = await repository.GetInteractionAsync(workspaceId, interactionId, token);
        if (interaction is null || interaction.Status == InteractionStatus.Closed) return;
        var message = new ConversationMessage(Guid.NewGuid(), workspaceId, interactionId, taskId, ConversationRole.Agentstration, $"{resultTitle} is ready. You can ask for another version or continue with a follow-up.", now);
        await repository.AddMessageAsync(message, token);
        await EmitAsync(new MessageAddedEvent(Id(), workspaceId.Value, Next(), now, message), token);
        var updated = interaction with { Status = InteractionStatus.Idle, LastFlowRunId = flowRunId ?? interaction.LastFlowRunId, LastActivityAt = now, ImmediateResult = new ShowResultAction(resultTitle, null), Messages = [.. interaction.Messages, message], Version = interaction.Version + 1 };
        try
        {
            await repository.SaveInteractionAsync(updated, interaction.Version, token);
            await EmitAsync(new InteractionUpdatedEvent(Id(), workspaceId.Value, Next(), now, interactionId.Value, InteractionStatus.Idle), token);
        }
        catch (WorkplaceConcurrencyException) { }
    }

    private async Task FailInteractionAsync(WorkspaceId workspaceId, InteractionId interactionId, WorkTaskId taskId, string error, DateTimeOffset now, CancellationToken token)
    {
        var interaction = await repository.GetInteractionAsync(workspaceId, interactionId, token);
        if (interaction is null || interaction.Status == InteractionStatus.Closed) return;
        var message = new ConversationMessage(Guid.NewGuid(), workspaceId, interactionId, taskId, ConversationRole.Agentstration, $"I couldn’t complete that update. {error}", now);
        await repository.AddMessageAsync(message, token);
        await EmitAsync(new MessageAddedEvent(Id(), workspaceId.Value, Next(), now, message), token);
        var updated = interaction with { Status = InteractionStatus.Failed, LastActivityAt = now, ImmediateResult = new ShowErrorAction("Task failed", error), Messages = [.. interaction.Messages, message], Version = interaction.Version + 1 };
        try
        {
            await repository.SaveInteractionAsync(updated, interaction.Version, token);
            await EmitAsync(new InteractionUpdatedEvent(Id(), workspaceId.Value, Next(), now, interactionId.Value, InteractionStatus.Failed), token);
        }
        catch (WorkplaceConcurrencyException) { }
    }

    private async Task NotifyAsync(WorkspaceId workspaceId, WorkNotificationKind kind, string title, string message, WorkTaskId taskId, DateTimeOffset now, CancellationToken token)
    {
        var notification = new WorkNotification { Id = WorkNotificationId.New(), WorkspaceId = workspaceId, Kind = kind, Title = title, Message = message, CreatedAt = now, WorkTaskId = taskId, ActionUrl = $"/tasks/{taskId}" };
        await repository.CreateNotificationAsync(notification, token); await EmitAsync(new NotificationCreatedEvent(Id(), workspaceId.Value, Next(), now, notification), token);
        var unread = (await repository.ListNotificationsAsync(workspaceId, true, token)).Count; await EmitAsync(new UnreadNotificationCountChangedEvent(Id(), workspaceId.Value, Next(), now, unread), token);
    }

    private async Task EmitAsync(WorkplaceEventContract value, CancellationToken token) { foreach (var sink in eventSinks) await sink.PublishAsync(value, token); }
    private long Next() => Interlocked.Increment(ref sequence);
    private static string Id() => Guid.NewGuid().ToString("N");
    private static bool TryContext(WorkItemSnapshot snapshot, out WorkspaceId workspaceId, out InteractionId interactionId, out WorkTaskId taskId)
    {
        taskId = snapshot.Metadata.TryGetValue("workplace.taskId", out var taskValue) && Guid.TryParse(taskValue, out var taskGuid) ? new(taskGuid) : WorkTaskId.FromWorkItem(snapshot.Id);
        interactionId = snapshot.Metadata.TryGetValue("workplace.interactionId", out var interactionValue) && Guid.TryParse(interactionValue, out var interactionGuid) ? new(interactionGuid) : default;
        if (snapshot.Metadata.TryGetValue("workplace.workspaceId", out var value) && Guid.TryParse(value, out var workspaceGuid) && interactionId != default) { workspaceId = new(workspaceGuid); return true; }
        workspaceId = default; return false;
    }
    private static WorkActorKind Actor(WorkTaskActivityType type) => type is WorkTaskActivityType.TaskPaused or WorkTaskActivityType.TaskResumed or WorkTaskActivityType.TaskCancelled ? WorkActorKind.User : WorkActorKind.Agentstration;
    private static string Title(WorkTaskActivityType type) => type switch { WorkTaskActivityType.TaskCreated => "Task created", WorkTaskActivityType.TaskStarted => "Work started", WorkTaskActivityType.TaskPaused => "Task paused", WorkTaskActivityType.TaskResumed => "Task resumed", WorkTaskActivityType.TaskCancelled => "Task cancelled", WorkTaskActivityType.ActionRequired => "Action required", WorkTaskActivityType.TaskCompleted => "Task completed", WorkTaskActivityType.TaskFailed => "Task failed", _ => type.ToString() };
}
