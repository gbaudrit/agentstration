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
        return endpoints;
    }

    private static Task<IResult> CreateAsync(CreateFlowRequest body, HttpResponse response, FlowService service, CancellationToken token) => ExecuteAsync(async () =>
    {
        var stored = await service.CreateAsync(new CreateFlowCommand(body.Name, body.Description, body.Kind, body.Version, body.Enabled, body.Spec, body.Metadata), token);
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
        var stored = await service.UpdateAsync(new FlowId(id), new UpdateFlowCommand(body.Description, body.Kind, body.Version, body.Enabled, body.Spec, body.Metadata), etag, token);
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

    private static async Task<StoredFlow> RequiredAsync(string id, FlowService service, CancellationToken token) =>
        await service.GetAsync(new FlowId(id), token) ?? throw new FlowNotFoundException(new FlowId(id));

    private static FlowResponse ToResponse(FlowDefinition value) => new(value.Id.Value, value.Name, value.Description, value.Kind, value.Version, value.Enabled, value.ActiveVersion, value.Spec, value.Metadata, value.CreatedAt, value.UpdatedAt);
    private static FlowSummaryResponse ToSummary(FlowDefinition value) => new(value.Id.Value, value.Name, value.Description, value.Kind, value.Version, value.Enabled, value.ActiveVersion, value.UpdatedAt);
    private static FlowVersionResponse ToVersion(FlowVersion value) => new(value.FlowId.Value, value.Version, value.Description, value.Kind, value.Spec, value.Metadata, value.PublishedAt);

    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (FlowNotFoundException exception) { return Results.Problem(statusCode: 404, title: "flow_not_found", detail: exception.Message); }
        catch (FlowConcurrencyException exception) { return Results.Problem(statusCode: 412, title: "precondition_failed", detail: exception.Message); }
        catch (FlowValidationException exception) { return Results.Problem(statusCode: 400, title: exception.Code, detail: exception.Message); }
        catch (ArgumentException exception) { return Results.Problem(statusCode: 400, title: "validation_failed", detail: exception.Message); }
    }
}
