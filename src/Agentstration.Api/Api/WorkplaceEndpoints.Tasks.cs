using Agentstration.Application.Work;
using Agentstration.Flow.Application;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.Resources;
using Agentstration.Web.Security;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Agentstration.Work.Storage.Abstractions;

namespace Agentstration.Web;

public static partial class WorkplaceEndpoints
{
    private static Task<IResult> ListTasksAsync(string workspaceName, WorkTaskStatus? status, WorkplaceService service, CancellationToken token) => ExecuteAsync(async () => Results.Ok(new WorkTaskPageResponse((await service.ListTasksAsync(WorkspaceId(workspaceName), status, token)).Select(ToResponse).ToArray())));

    private static Task<IResult> GetTaskAsync(string workspaceName, Guid taskId, WorkplaceService service, CancellationToken token) => ExecuteAsync(async () => Results.Ok(ToResponse(await service.GetTaskAsync(WorkspaceId(workspaceName), new(taskId), token))));

    private static Task<IResult> PauseTaskAsync(string workspaceName, Guid taskId, WorkplaceService service, CancellationToken token) => ExecuteAsync(async () => Results.Ok(ToResponse(await service.PauseTaskAsync(WorkspaceId(workspaceName), new(taskId), token))));

    private static Task<IResult> ResumeTaskAsync(string workspaceName, Guid taskId, WorkplaceService service, CancellationToken token) => ExecuteAsync(async () => Results.Ok(ToResponse(await service.ResumeTaskAsync(WorkspaceId(workspaceName), new(taskId), token))));

    private static Task<IResult> CancelTaskAsync(string workspaceName, Guid taskId, WorkplaceService service, CancellationToken token) => ExecuteAsync(async () => Results.Ok(ToResponse(await service.CancelTaskAsync(WorkspaceId(workspaceName), new(taskId), token))));

    private static Task<IResult> GetActivitiesAsync(string workspaceName, Guid taskId, WorkplaceService service, CancellationToken token) => ExecuteAsync(async () => { await service.GetTaskAsync(WorkspaceId(workspaceName), new(taskId), token); return Results.Ok(await service.ListActivitiesAsync(WorkspaceId(workspaceName), new(taskId), token)); });

    private static Task<IResult> GetResultsAsync(string workspaceName, Guid taskId, WorkplaceService service, CancellationToken token) => ExecuteAsync(async () => { await service.GetTaskAsync(WorkspaceId(workspaceName), new(taskId), token); return Results.Ok(await service.ListResultsAsync(WorkspaceId(workspaceName), new(taskId), token)); });

    private static Task<IResult> GetArtifactsAsync(string workspaceName, Guid taskId, WorkplaceService service, CancellationToken token) => ExecuteAsync(async () => { await service.GetTaskAsync(WorkspaceId(workspaceName), new(taskId), token); return Results.Ok(await service.ListArtifactsAsync(WorkspaceId(workspaceName), new(taskId), token)); });

    private static Task<IResult> GetArtifactContentAsync(string workspaceName, Guid taskId, Guid artifactId, WorkplaceService service, IArtifactStore store, CancellationToken token) => ExecuteAsync(async () => { var workspaceId = WorkspaceId(workspaceName); var value = await service.GetArtifactAsync(workspaceId, new(taskId), new(artifactId), token); var stream = await store.OpenReadAsync(workspaceId, new ArtifactReference(value.StorageKey, value.ContentType, value.Length), token); return Results.File(stream, value.ContentType, value.Name, enableRangeProcessing: true); });

    private static Task<IResult> GetNotificationsAsync(string workspaceName, bool? unreadOnly, WorkplaceService service, CancellationToken token) => ExecuteAsync(async () => Results.Ok(new WorkNotificationPageResponse(await service.ListNotificationsAsync(WorkspaceId(workspaceName), unreadOnly, token))));

    private static Task<IResult> GetUnreadCountAsync(string workspaceName, WorkplaceService service, CancellationToken token) => ExecuteAsync(async () => Results.Ok(new UnreadNotificationCountResponse(await service.UnreadCountAsync(WorkspaceId(workspaceName), token))));

    private static Task<IResult> MarkReadAsync(string workspaceName, Guid notificationId, WorkplaceService service, CancellationToken token) => ExecuteAsync(async () => Results.Ok(await service.MarkNotificationReadAsync(WorkspaceId(workspaceName), new(notificationId), token)));

    private static Task<IResult> MarkAllReadAsync(string workspaceName, WorkplaceService service, CancellationToken token) => ExecuteAsync(async () => { await service.MarkAllNotificationsReadAsync(WorkspaceId(workspaceName), token); return Results.NoContent(); });
}
