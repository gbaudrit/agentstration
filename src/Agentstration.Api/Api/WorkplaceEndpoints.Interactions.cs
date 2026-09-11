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
    private static Task<IResult> SubmitEntryAsync(string workspaceName, string entryName, CreateInteractionRequest request, WorkplaceService service, CancellationToken token) => SubmitCoreAsync(WorkspaceId(workspaceName), EntryResourceId(entryName), request, service, token);

    private static Task<IResult> SubmitNamespacedEntryAsync(string workspaceName, string @namespace, string entryName, CreateInteractionRequest request, WorkplaceService service, CancellationToken token) => SubmitCoreAsync(WorkspaceId(workspaceName), NamespacedEntryId(@namespace, entryName), request, service, token);

    private static Task<IResult> SubmitCoreAsync(WorkspaceId workspaceId, EntryId entryId, CreateInteractionRequest request, WorkplaceService service, CancellationToken token) => ExecuteAsync(async () =>
    {
        var attachments = request.Attachments?.Select(WorkEndpoints.ToWorkAttachment).ToArray(); var result = await service.SubmitAsync(new SubmitEntryCommand(workspaceId, entryId, request.Values, attachments), token);
        return Results.Created($"/api/workspaces/{WorkspaceName(workspaceId)}/interactions/{result.Interaction.Id}", new EntrySubmissionResponse(ToResponse(result.Interaction), result.Action, result.Task is null ? null : ToResponse(result.Task)));
    });

    private static Task<IResult> GetInteractionAsync(string workspaceName, Guid interactionId, WorkplaceService service, CancellationToken token) => ExecuteAsync(async () => Results.Ok(ToResponse(await service.GetInteractionAsync(WorkspaceId(workspaceName), new(interactionId), token))));

    private static Task<IResult> ListInteractionsAsync(string workspaceName, int? take, WorkplaceService service, CancellationToken token) => ExecuteAsync(async () => Results.Ok(new InteractionPageResponse((await service.ListInteractionsAsync(WorkspaceId(workspaceName), take ?? 20, token)).Select(ToResponse).ToArray())));

    private static Task<IResult> GetMessagesAsync(string workspaceName, Guid interactionId, WorkplaceService service, CancellationToken token) => ExecuteAsync(async () => Results.Ok(await service.ListMessagesAsync(WorkspaceId(workspaceName), new(interactionId), token)));

    private static Task<IResult> AddMessageAsync(string workspaceName, Guid interactionId, AddConversationMessageRequest request, WorkplaceService service, CancellationToken token) => ExecuteAsync(async () =>
    {
        var result = await service.AddMessageAsync(WorkspaceId(workspaceName), new(interactionId), request.Content, token);
        return Results.Accepted($"/api/workspaces/{workspaceName}/interactions/{interactionId}", new AddConversationMessageResponse(result.Message, ToResponse(result.Interaction), result.Action, result.Task is null ? null : ToResponse(result.Task)));
    });

    private static Task<IResult> GetPendingActionsAsync(string workspaceName, Guid interactionId, WorkplaceService service, CancellationToken token) => ExecuteAsync(async () => Results.Ok((await service.ListPendingActionsAsync(WorkspaceId(workspaceName), new(interactionId), token)).Select(WorkplaceService.ToContract)));

    private static Task<IResult> RespondPendingActionAsync(string workspaceName, Guid interactionId, Guid pendingActionId, PendingActionResponseRequest request, HttpContext context, WorkplaceService service, CancellationToken token) => ExecuteAsync(async () =>
    {
        var principalId = context.Features.Get<ResolvedPrincipalFeature>()?.Principal.Id.ToString("D") ?? "workplace-user";
        var result = await service.RespondAsync(WorkspaceId(workspaceName), new(interactionId), new(pendingActionId), request.ResumeToken, request.Values, principalId, token);
        return Results.Ok(new PendingActionResolutionResponse(WorkplaceService.ToContract(result.PendingAction), result.NextAction, ToResponse(result.Interaction!), result.Task is null ? null : ToResponse(result.Task)));
    });
}
