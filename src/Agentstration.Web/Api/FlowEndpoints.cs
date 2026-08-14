using System.Text.Json;
using Agentstration.Flow;
using Agentstration.Flow.Application;
using Agentstration.Flow.Contracts;
using Agentstration.Flow.Storage.Abstractions;

namespace Agentstration.Web;

public static class FlowEndpoints
{
    public static IEndpointRouteBuilder MapAgentstrationFlowApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/flows");
        group.MapPost("/", CreateAsync);
        group.MapGet("/", ListAsync);
        group.MapGet("/{id}", GetAsync);
        group.MapPut("/{id}", UpdateAsync);
        group.MapDelete("/{id}", DeleteAsync);
        group.MapGet("/{id}/versions", ListVersionsAsync);
        group.MapGet("/{id}/versions/{version}", GetVersionAsync);
        group.MapPost("/{id}/versions", CreateVersionAsync);
        group.MapPost("/{id}/runs", CreateRunAsync);
        group.MapGet("/{id}/runs", ListFlowRunsAsync);
        group.MapGet("/{id}/runs/{runId}", GetFlowRunAsync);
        group.MapPost("/{id}/runs/{runId}/cancel", CancelFlowRunAsync);
        group.MapPost("/drafts", CreateDraftAsync);
        group.MapGet("/{id}/draft", GetDraftAsync);
        group.MapPut("/{id}/draft", SaveDraftAsync);
        group.MapPost("/{id}/validate", ValidateDraftAsync);
        group.MapGet("/{id}/draft/source", GetDraftSourceAsync);
        group.MapPut("/{id}/draft/source", ReplaceDraftSourceAsync);
        group.MapPost("/{id}/publish", PublishDraftAsync);
        group.MapPost("/{id}/draft/runs", CreateDraftRunAsync);
        group.MapPost("/{id}/versions/{version}/draft", CreateDraftFromVersionAsync);
        var runs = endpoints.MapGroup("/api/flowRuns");
        runs.MapGet("/", ListRunsAsync);
        runs.MapGet("/{runId}", GetRunAsync);
        runs.MapGet("/{runId}/events", ObserveRunAsync);
        runs.MapGet("/{runId}/eventHistory", ListRunEventsAsync);
        runs.MapPost("/{runId}/cancel", CancelRunAsync);
        return endpoints;
    }

    private static Task<IResult> CreateAsync(CreateFlowRequest body, HttpResponse response, FlowService service, CancellationToken token) => ExecuteAsync(async () =>
    {
        var stored = await service.CreateAsync(new CreateFlowCommand(body.Name, body.Description, body.Version, body.Enabled, body.Definition, body.Metadata), token);
        response.Headers.ETag = stored.ETag;
        response.Headers.Location = $"/api/flows/{stored.Value.Id}";
        return Results.Json(ToResponse(stored.Value), statusCode: StatusCodes.Status201Created);
    });

    private static Task<IResult> ListAsync(int? skip, int? top, FlowService service, CancellationToken token) => ExecuteAsync(async () =>
    {
        var actualSkip = Math.Max(0, skip ?? 0);
        var actualTop = Math.Clamp(top ?? 50, 1, 200);
        var page = await service.ListAsync(actualSkip, actualTop, token);
        var next = page.HasMore ? $"/api/flows?skip={actualSkip + actualTop}&top={actualTop}" : null;
        return Results.Ok(new FlowPageResponse(page.Items.Select(item => ToSummary(item.Value)).ToArray(), next));
    });

    private static Task<IResult> GetAsync(string id, HttpResponse response, FlowService service, CancellationToken token) => ExecuteAsync(async () =>
    {
        var stored = await RequiredAsync(id, service, token);
        response.Headers.ETag = stored.ETag;
        return Results.Ok(ToResponse(stored.Value));
    });

    private static Task<IResult> UpdateAsync(string id, UpdateFlowRequest body, HttpRequest request, HttpResponse response, FlowService service, CancellationToken token) => ExecuteAsync(async () =>
    {
        var current = await RequiredAsync(id, service, token);
        var etag = request.Headers.IfMatch.FirstOrDefault() ?? current.ETag;
        var stored = await service.UpdateAsync(new FlowId(id), new UpdateFlowCommand(body.Description, body.Version, body.Enabled, body.Definition, body.Metadata), etag, token);
        response.Headers.ETag = stored.ETag;
        return Results.Ok(ToResponse(stored.Value));
    });

    private static Task<IResult> DeleteAsync(string id, HttpRequest request, FlowService service, CancellationToken token) => ExecuteAsync(async () =>
    {
        await service.DeleteAsync(new FlowId(id), request.Headers.IfMatch.FirstOrDefault(), token);
        return Results.NoContent();
    });

    private static Task<IResult> CreateVersionAsync(string id, CreateFlowVersionRequest body, HttpResponse response, FlowService service, CancellationToken token) => ExecuteAsync(async () =>
    {
        var stored = await service.PublishVersionAsync(new FlowId(id), body.Version, body.Activate, token);
        response.Headers.ETag = stored.ETag;
        response.Headers.Location = $"/api/flows/{id}/versions/{body.Version}";
        return Results.Json(ToVersion(stored.Value), statusCode: StatusCodes.Status201Created);
    });

    private static Task<IResult> GetVersionAsync(string id, string version, HttpResponse response, FlowService service, CancellationToken token) => ExecuteAsync(async () =>
    {
        var stored = await service.GetVersionAsync(new FlowId(id), version, token) ?? throw new FlowNotFoundException(new FlowId($"{id}:{version}"));
        response.Headers.ETag = stored.ETag;
        return Results.Ok(ToVersion(stored.Value));
    });

    private static Task<IResult> ListVersionsAsync(string id, FlowService service, CancellationToken token) => ExecuteAsync(async () =>
    {
        _ = await RequiredAsync(id, service, token);
        return Results.Ok((await service.ListVersionsAsync(new FlowId(id), token)).Select(item => ToVersion(item.Value)));
    });

    private static Task<IResult> CreateRunAsync(string id, CreateFlowRunRequest body, HttpResponse response, FlowRunService service, CancellationToken token) => ExecuteAsync(async () =>
    {
        var stored = await service.CreateAsync(new FlowId(id), body.Version, body.DeploymentResourceId, body.Trigger, body.StartedBy, body.CorrelationId, body.Input, token);
        response.Headers.Location = $"/api/flowRuns/{stored.Value.Id}";
        response.Headers.ETag = stored.ETag;
        return Results.Accepted($"/api/flowRuns/{stored.Value.Id}", stored.Value);
    });

    private static Task<IResult> ListFlowRunsAsync(string id, FlowRunStatus? status, int? skip, int? top, FlowRunService service, CancellationToken token) =>
        ListRunsCoreAsync(new FlowId(id), status, skip, top, service, token);

    private static Task<IResult> ListRunsAsync(string? flowId, FlowRunStatus? status, int? skip, int? top, FlowRunService service, CancellationToken token) =>
        ListRunsCoreAsync(string.IsNullOrWhiteSpace(flowId) ? null : new FlowId(flowId), status, skip, top, service, token);

    private static Task<IResult> ListRunsCoreAsync(FlowId? flowId, FlowRunStatus? status, int? skip, int? top, FlowRunService service, CancellationToken token) => ExecuteAsync(async () =>
    {
        var actualSkip = Math.Max(0, skip ?? 0);
        var actualTop = Math.Clamp(top ?? 50, 1, 200);
        var page = await service.ListAsync(flowId, status, actualSkip, actualTop, token);
        var next = page.HasMore ? $"/api/flowRuns?skip={actualSkip + actualTop}&top={actualTop}" : null;
        return Results.Ok(new FlowRunPageResponse(page.Items.Select(item => item.Value).ToArray(), next));
    });

    private static Task<IResult> GetFlowRunAsync(string id, string runId, HttpResponse response, FlowRunService service, CancellationToken token) => ExecuteAsync(async () =>
    {
        var stored = await RequiredRunAsync(runId, service, token);
        if (stored.Value.FlowId != new FlowId(id)) throw new FlowRunNotFoundException(runId);
        response.Headers.ETag = stored.ETag;
        return Results.Ok(stored.Value);
    });

    private static Task<IResult> GetRunAsync(string runId, HttpResponse response, FlowRunService service, CancellationToken token) => ExecuteAsync(async () =>
    {
        var stored = await RequiredRunAsync(runId, service, token);
        response.Headers.ETag = stored.ETag;
        return Results.Ok(stored.Value);
    });

    private static Task<IResult> CancelFlowRunAsync(string id, string runId, FlowRunService service, CancellationToken token) => ExecuteAsync(async () =>
    {
        var stored = await RequiredRunAsync(runId, service, token);
        if (stored.Value.FlowId != new FlowId(id)) throw new FlowRunNotFoundException(runId);
        return Results.Ok((await service.CancelAsync(runId, token)).Value);
    });

    private static Task<IResult> CancelRunAsync(string runId, FlowRunService service, CancellationToken token) => ExecuteAsync(async () =>
        Results.Ok((await service.CancelAsync(runId, token)).Value));

    private static Task<IResult> CreateDraftAsync(CreateFlowDraftRequest body, HttpResponse response, FlowDraftService service, CancellationToken token) => ExecuteAsync(async () =>
    {
        var stored = await service.CreateAsync(new CreateFlowDraftCommand(body.Name, body.DisplayName, body.Description, body.Tags, body.Template), token);
        response.Headers.ETag = stored.ETag;
        response.Headers.Location = $"/api/flows/{stored.Value.FlowId.Value}/draft";
        return Results.Json(new FlowDraftResponse(stored.Value, stored.ETag), statusCode: StatusCodes.Status201Created);
    });

    private static Task<IResult> GetDraftAsync(string id, HttpResponse response, FlowDraftService service, CancellationToken token) => ExecuteAsync(async () =>
    {
        var stored = await service.GetAsync(new FlowId(id), token) ?? throw new FlowNotFoundException(new FlowId(id));
        response.Headers.ETag = stored.ETag;
        return Results.Ok(new FlowDraftResponse(stored.Value, stored.ETag));
    });

    private static Task<IResult> SaveDraftAsync(string id, UpdateFlowDraftRequest body, HttpRequest request, HttpResponse response, FlowDraftService service, CancellationToken token) => ExecuteAsync(async () =>
    {
        var etag = RequiredIfMatch(request);
        var stored = await service.SaveAsync(new FlowId(id), new UpdateFlowDraftCommand(body.DisplayName, body.Description, body.Tags, body.Definition, body.UpdatedBy), etag, token);
        response.Headers.ETag = stored.ETag;
        return Results.Ok(new FlowDraftResponse(stored.Value, stored.ETag));
    });

    private static Task<IResult> ValidateDraftAsync(string id, FlowDraftService service, CancellationToken token) => ExecuteAsync(async () =>
    {
        var validation = await service.ValidateAsync(new FlowId(id), token);
        return Results.Ok(new FlowValidationResponse(validation.IsValid, validation.Issues));
    });

    private static Task<IResult> GetDraftSourceAsync(string id, string? format, FlowDraftService service, CancellationToken token) => ExecuteAsync(async () =>
    {
        var actualFormat = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase) ? "json" : "yaml";
        var draft = await service.GetAsync(new FlowId(id), token) ?? throw new FlowNotFoundException(new FlowId(id));
        return Results.Ok(new FlowSourceResponse(await service.GetSourceAsync(new FlowId(id), actualFormat, token), actualFormat, draft.Value.Revision));
    });

    private static Task<IResult> ReplaceDraftSourceAsync(string id, ReplaceFlowSourceRequest body, HttpRequest request, HttpResponse response, FlowDraftService service, CancellationToken token) => ExecuteAsync(async () =>
    {
        var current = await service.GetAsync(new FlowId(id), token) ?? throw new FlowNotFoundException(new FlowId(id));
        var definition = service.ParseSource(body.Source, body.Format);
        var stored = await service.SaveAsync(new FlowId(id), new UpdateFlowDraftCommand(current.Value.DisplayName, current.Value.Description, current.Value.Tags, definition, body.UpdatedBy), RequiredIfMatch(request), token);
        response.Headers.ETag = stored.ETag;
        return Results.Ok(new FlowDraftResponse(stored.Value, stored.ETag));
    });

    private static Task<IResult> PublishDraftAsync(string id, PublishFlowDraftRequest body, HttpResponse response, FlowDraftService service, CancellationToken token) => ExecuteAsync(async () =>
    {
        var stored = await service.PublishAsync(new FlowId(id), body.Version, body.ReleaseNotes, body.Activate, token);
        response.Headers.ETag = stored.ETag;
        response.Headers.Location = $"/api/flows/{id}/versions/{body.Version}";
        return Results.Json(ToVersion(stored.Value), statusCode: StatusCodes.Status201Created);
    });

    private static Task<IResult> CreateDraftRunAsync(string id, CreateFlowRunRequest body, HttpResponse response, FlowDraftService drafts, FlowRunService runs, CancellationToken token) => ExecuteAsync(async () =>
    {
        var draft = await drafts.GetAsync(new FlowId(id), token) ?? throw new FlowNotFoundException(new FlowId(id));
        var validation = await drafts.ValidateAsync(new FlowId(id), token);
        if (!validation.IsValid) throw new FlowValidationException("flow_validation_failed", "The Flow Draft contains validation errors and cannot run.");
        var stored = await runs.CreateDraftAsync(draft.Value, body.Trigger, body.StartedBy, body.CorrelationId, body.Input, token);
        response.Headers.Location = $"/api/flowRuns/{stored.Value.Id}";
        return Results.Accepted($"/api/flowRuns/{stored.Value.Id}", stored.Value);
    });

    private static Task<IResult> CreateDraftFromVersionAsync(string id, string version, HttpResponse response, FlowDraftService service, CancellationToken token) => ExecuteAsync(async () =>
    {
        var stored = await service.CreateFromVersionAsync(new FlowId(id), version, "local-user", token);
        response.Headers.ETag = stored.ETag;
        return Results.Ok(new FlowDraftResponse(stored.Value, stored.ETag));
    });

    private static async Task ObserveRunAsync(string runId, HttpResponse response, FlowRunService service, CancellationToken token)
    {
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        await foreach (var run in service.ObserveAsync(runId, token))
        {
            await response.WriteAsync($"data: {JsonSerializer.Serialize(run, JsonOptions)}\n\n", token);
            await response.Body.FlushAsync(token);
        }
    }

    private static Task<IResult> ListRunEventsAsync(string runId, long? afterSequence, FlowRunService service, CancellationToken token) => ExecuteAsync(async () =>
    {
        _ = await RequiredRunAsync(runId, service, token);
        return Results.Ok(await service.ListEventsAsync(runId, Math.Max(0, afterSequence ?? 0), token));
    });

    private static async Task<StoredFlow> RequiredAsync(string id, FlowService service, CancellationToken token) =>
        await service.GetAsync(new FlowId(id), token) ?? throw new FlowNotFoundException(new FlowId(id));
    private static async Task<StoredFlowRun> RequiredRunAsync(string id, FlowRunService service, CancellationToken token) =>
        await service.GetAsync(id, token) ?? throw new FlowRunNotFoundException(id);

    private static FlowResponse ToResponse(FlowResource value) => new(value.Id.Value, value.Name, value.Description, value.Version, value.Enabled, value.ActiveVersion, value.Definition, value.Metadata, value.CreatedAt, value.UpdatedAt);
    private static FlowSummaryResponse ToSummary(FlowResource value) => new(value.Id.Value, value.Name, value.Description, value.Definition.Kind, value.Version, value.Enabled, value.ActiveVersion, value.UpdatedAt);
    private static FlowVersionResponse ToVersion(FlowVersion value) => new(value.FlowId.Value, value.Version, value.Description, value.Definition, value.Metadata, value.PublishedAt, value.Graph, value.DefinitionHash, value.ReleaseNotes);

    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (FlowNotFoundException exception) { return Results.Problem(statusCode: 404, title: "flow_not_found", detail: exception.Message); }
        catch (FlowRunNotFoundException exception) { return Results.Problem(statusCode: 404, title: "flow_run_not_found", detail: exception.Message); }
        catch (FlowConcurrencyException exception) { return Results.Problem(statusCode: 412, title: "precondition_failed", detail: exception.Message); }
        catch (FlowValidationException exception) { return Results.Problem(statusCode: 400, title: exception.Code, detail: exception.Message); }
        catch (ArgumentException exception) { return Results.Problem(statusCode: 400, title: "validation_failed", detail: exception.Message); }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static string RequiredIfMatch(HttpRequest request) => request.Headers.IfMatch.FirstOrDefault()
        ?? throw new FlowValidationException("if_match_required", "Saving a Flow Draft requires an If-Match ETag.");
}
