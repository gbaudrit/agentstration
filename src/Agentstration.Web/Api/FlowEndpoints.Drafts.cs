using System.Text.Json;
using Agentstration.Flow;
using Agentstration.Flow.Application;
using Agentstration.Flow.Contracts;
using Agentstration.Flow.Storage.Abstractions;
using Agentstration.Management.Abstractions;
using Agentstration.Resources;
using Agentstration.Web.Security;

namespace Agentstration.Web;

public static partial class FlowEndpoints
{
    private static Task<IResult> CreateDraftAsync(CreateFlowDraftRequest body, HttpResponse response, FlowDraftService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var stored = await service.CreateAsync(CurrentWorkspace(requestContext), new CreateFlowDraftCommand(body.Name, body.DisplayName, body.Description, body.Tags, body.Template), token);
        response.Headers.ETag = stored.ETag;
        response.Headers.Location = $"/api/flows/{stored.Value.FlowId.Value}/draft";
        return Results.Json(new FlowDraftResponse(stored.Value, stored.ETag), statusCode: StatusCodes.Status201Created);
    });

    private static Task<IResult> GetDraftAsync(string id, HttpResponse response, FlowDraftService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var stored = await service.GetAsync(CurrentWorkspace(requestContext), new FlowId(id), token) ?? throw new FlowNotFoundException(new FlowId(id));
        response.Headers.ETag = stored.ETag;
        return Results.Ok(new FlowDraftResponse(stored.Value, stored.ETag));
    });

    private static Task<IResult> SaveDraftAsync(string id, UpdateFlowDraftRequest body, HttpRequest request, HttpResponse response, FlowDraftService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var etag = RequiredIfMatch(request);
        var stored = await service.SaveAsync(CurrentWorkspace(requestContext), new FlowId(id), new UpdateFlowDraftCommand(body.DisplayName, body.Description, body.Tags, body.Definition, body.UpdatedBy), etag, token);
        response.Headers.ETag = stored.ETag;
        return Results.Ok(new FlowDraftResponse(stored.Value, stored.ETag));
    });

    private static Task<IResult> ValidateDraftAsync(string id, FlowDraftService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var validation = await service.ValidateAsync(CurrentWorkspace(requestContext), new FlowId(id), token);
        return Results.Ok(new FlowValidationResponse(validation.IsValid, validation.Issues));
    });

    private static Task<IResult> GetDraftSourceAsync(string id, string? format, FlowDraftService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var actualFormat = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase) ? "json" : "yaml";
        var workspaceId = CurrentWorkspace(requestContext);
        var draft = await service.GetAsync(workspaceId, new FlowId(id), token) ?? throw new FlowNotFoundException(new FlowId(id));
        return Results.Ok(new FlowSourceResponse(await service.GetSourceAsync(workspaceId, new FlowId(id), actualFormat, token), actualFormat, draft.Value.Revision));
    });

    private static Task<IResult> ReplaceDraftSourceAsync(string id, ReplaceFlowSourceRequest body, HttpRequest request, HttpResponse response, FlowDraftService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var workspaceId = CurrentWorkspace(requestContext);
        var current = await service.GetAsync(workspaceId, new FlowId(id), token) ?? throw new FlowNotFoundException(new FlowId(id));
        var definition = service.ParseSource(body.Source, body.Format);
        var stored = await service.SaveAsync(workspaceId, new FlowId(id), new UpdateFlowDraftCommand(current.Value.DisplayName, current.Value.Description, current.Value.Tags, definition, body.UpdatedBy), RequiredIfMatch(request), token);
        response.Headers.ETag = stored.ETag;
        return Results.Ok(new FlowDraftResponse(stored.Value, stored.ETag));
    });

    private static Task<IResult> PublishDraftAsync(string id, PublishFlowDraftRequest body, HttpResponse response, FlowDraftService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var stored = await service.PublishAsync(CurrentWorkspace(requestContext), new FlowId(id), body.Version, body.ReleaseNotes, body.Activate, token);
        response.Headers.ETag = stored.ETag;
        response.Headers.Location = $"/api/flows/{id}/versions/{body.Version}";
        return Results.Json(ToVersion(stored.Value), statusCode: StatusCodes.Status201Created);
    });

    private static Task<IResult> CreateDraftRunAsync(string id, CreateFlowRunRequest body, HttpContext context, HttpResponse response, FlowDraftService drafts, FlowRunService runs, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var workspaceId = CurrentWorkspace(requestContext);
        var draft = await drafts.GetAsync(workspaceId, new FlowId(id), token) ?? throw new FlowNotFoundException(new FlowId(id));
        var validation = await drafts.ValidateAsync(workspaceId, new FlowId(id), token);
        if (!validation.IsValid) throw new FlowValidationException("flow_validation_failed", "The Flow Draft contains validation errors and cannot run.");
        var scope = CurrentScope(requestContext);
        var startedBy = context.Features.Get<ResolvedPrincipalFeature>()?.Principal.DisplayName ?? scope.PrincipalId.ToString("D");
        var stored = await runs.CreateDraftAsync(draft.Value, body.Trigger, startedBy, body.CorrelationId, body.Input, scope, token);
        response.Headers.Location = $"/api/flowRuns/{stored.Value.Id}";
        return Results.Accepted($"/api/flowRuns/{stored.Value.Id}", stored.Value);
    });

    private static Task<IResult> CreateDraftFromVersionAsync(string id, string version, HttpResponse response, FlowDraftService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var stored = await service.CreateFromVersionAsync(CurrentWorkspace(requestContext), new FlowId(id), version, "local-user", token);
        response.Headers.ETag = stored.ETag;
        return Results.Ok(new FlowDraftResponse(stored.Value, stored.ETag));
    });
}
