using System.Text.Json;
using Agentstration.Flow;
using Agentstration.Flow.Application;
using Agentstration.Flow.Contracts;
using Agentstration.Flow.Storage.Abstractions;
using Agentstration.Management.Abstractions;
using Agentstration.Resources;
using Agentstration.Web.Security;

namespace Agentstration.Web;

public static class FlowEndpoints
{
    public static IEndpointRouteBuilder MapAgentstrationFlowApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/flows").RequireAuthorization(AgentstrationPolicies.Authenticated);
        group.MapPost("/", CreateAsync);
        group.MapGet("/", ListAsync);
        group.MapGet("/{id}", GetAsync);
        group.MapPut("/{id}", UpdateAsync);
        group.MapDelete("/{id}", DeleteAsync);
        group.MapGet("/{id}/versions", ListVersionsAsync);
        group.MapGet("/{id}/versions/{version}", GetVersionAsync);
        group.MapPost("/{id}/versions", CreateVersionAsync);
        group.MapPost("/{id}/runs", CreateRunAsync).RequireAuthorization(AgentstrationPolicies.CanRunFlows);
        group.MapGet("/{id}/runs", ListFlowRunsAsync).RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        group.MapGet("/{id}/runs/{runId}", GetFlowRunAsync).RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        group.MapPost("/{id}/runs/{runId}/cancel", CancelFlowRunAsync).RequireAuthorization(AgentstrationPolicies.CanRunFlows);
        group.MapPost("/drafts", CreateDraftAsync);
        group.MapGet("/{id}/draft", GetDraftAsync);
        group.MapPut("/{id}/draft", SaveDraftAsync);
        group.MapPost("/{id}/validate", ValidateDraftAsync);
        group.MapGet("/{id}/draft/source", GetDraftSourceAsync);
        group.MapPut("/{id}/draft/source", ReplaceDraftSourceAsync);
        group.MapPost("/{id}/publish", PublishDraftAsync);
        group.MapPost("/{id}/draft/runs", CreateDraftRunAsync).RequireAuthorization(AgentstrationPolicies.CanRunFlows);
        group.MapPost("/{id}/versions/{version}/draft", CreateDraftFromVersionAsync);
        var namespaced = endpoints.MapGroup("/api/namespaces/{namespace}/flows").RequireAuthorization(AgentstrationPolicies.Authenticated);
        namespaced.MapPost("/", CreateNamespacedAsync);
        namespaced.MapGet("/", ListNamespacedAsync);
        namespaced.MapGet("/{id}", GetNamespacedAsync);
        namespaced.MapPut("/{id}", UpdateNamespacedAsync);
        namespaced.MapDelete("/{id}", DeleteNamespacedAsync);
        namespaced.MapGet("/{id}/versions", ListNamespacedVersionsAsync);
        namespaced.MapGet("/{id}/versions/{version}", GetNamespacedVersionAsync);
        namespaced.MapPost("/{id}/runs", CreateNamespacedRunAsync).RequireAuthorization(AgentstrationPolicies.CanRunFlows);
        namespaced.MapGet("/{id}/runs", ListNamespacedFlowRunsAsync).RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        var runs = endpoints.MapGroup("/api/flowRuns").RequireAuthorization(AgentstrationPolicies.Authenticated);
        runs.MapGet("/", ListRunsAsync).RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        runs.MapGet("/{runId}", GetRunAsync).RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        runs.MapGet("/{runId}/events", ObserveRunAsync).RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        runs.MapGet("/{runId}/eventHistory", ListRunEventsAsync).RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        runs.MapGet("/{runId}/inputs", ListInputsAsync).RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        runs.MapGet("/{runId}/inputs/{inputId}", GetInputAsync).RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        runs.MapPost("/{runId}/inputs/{inputId}/response", RespondToInputAsync).RequireAuthorization(AgentstrationPolicies.CanRunFlows);
        runs.MapPost("/{runId}/cancel", CancelRunAsync).RequireAuthorization(AgentstrationPolicies.CanRunFlows);
        return endpoints;
    }

    private static Task<IResult> CreateAsync(CreateFlowRequest body, HttpResponse response, FlowService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var stored = await service.CreateAsync(CurrentWorkspace(requestContext), new CreateFlowCommand(body.Name, body.Description, body.Version, body.Enabled, body.Definition, body.Metadata), body.Namespace, token);
        response.Headers.ETag = stored.ETag;
        response.Headers.Location = $"/api/flows/{stored.Value.Id}";
        return Results.Json(ToResponse(stored.Value), statusCode: StatusCodes.Status201Created);
    });

    private static Task<IResult> CreateNamespacedAsync(string @namespace, CreateFlowRequest body, HttpResponse response, FlowService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var parsed = ResourceNamespace.Parse(@namespace);
        if (body.Namespace != parsed) throw new FlowValidationException("route_namespace_mismatch", "The Flow namespace must match the route namespace.");
        var stored = await service.CreateAsync(CurrentWorkspace(requestContext), new CreateFlowCommand(body.Name, body.Description, body.Version, body.Enabled, body.Definition, body.Metadata), parsed, token);
        response.Headers.ETag = stored.ETag;
        response.Headers.Location = $"/api/namespaces/{parsed.Value}/flows/{stored.Value.Id.Value}";
        return Results.Json(ToResponse(stored.Value), statusCode: StatusCodes.Status201Created);
    });

    private static Task<IResult> GetNamespacedAsync(string @namespace, string id, HttpResponse response, FlowService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var flowId = new FlowId(id, ResourceNamespace.Parse(@namespace));
        var stored = await service.GetAsync(CurrentWorkspace(requestContext), flowId, token) ?? throw new FlowNotFoundException(flowId);
        response.Headers.ETag = stored.ETag;
        return Results.Ok(ToResponse(stored.Value));
    });

    private static Task<IResult> UpdateNamespacedAsync(string @namespace, string id, UpdateFlowRequest body, HttpRequest request, HttpResponse response, FlowService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var flowId = new FlowId(id, ResourceNamespace.Parse(@namespace));
        var workspaceId = CurrentWorkspace(requestContext);
        var current = await service.GetAsync(workspaceId, flowId, token) ?? throw new FlowNotFoundException(flowId);
        var stored = await service.UpdateAsync(workspaceId, flowId, new UpdateFlowCommand(body.Description, body.Version, body.Enabled, body.Definition, body.Metadata), request.Headers.IfMatch.FirstOrDefault() ?? current.ETag, token);
        response.Headers.ETag = stored.ETag;
        return Results.Ok(ToResponse(stored.Value));
    });

    private static Task<IResult> DeleteNamespacedAsync(string @namespace, string id, HttpRequest request, FlowService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        await service.DeleteAsync(CurrentWorkspace(requestContext), new FlowId(id, ResourceNamespace.Parse(@namespace)), request.Headers.IfMatch.FirstOrDefault(), token);
        return Results.NoContent();
    });

    private static Task<IResult> GetNamespacedVersionAsync(string @namespace, string id, string version, HttpResponse response, FlowService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var flowId = new FlowId(id, ResourceNamespace.Parse(@namespace));
        var stored = await service.GetVersionAsync(CurrentWorkspace(requestContext), flowId, version, token) ?? throw new FlowNotFoundException(flowId);
        response.Headers.ETag = stored.ETag;
        return Results.Ok(ToVersion(stored.Value));
    });

    private static Task<IResult> ListAsync(bool? allNamespaces, int? skip, int? top, FlowService service, ICurrentRequestContext requestContext, CancellationToken token) =>
        ListCoreAsync(allNamespaces is true ? null : ResourceNamespace.Default, skip, top, service, requestContext, token);

    private static Task<IResult> ListNamespacedAsync(string @namespace, int? skip, int? top, FlowService service, ICurrentRequestContext requestContext, CancellationToken token) =>
        ListCoreAsync(ResourceNamespace.Parse(@namespace), skip, top, service, requestContext, token);

    private static Task<IResult> ListCoreAsync(ResourceNamespace? @namespace, int? skip, int? top, FlowService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var actualSkip = Math.Max(0, skip ?? 0);
        var actualTop = Math.Clamp(top ?? 50, 1, 200);
        var workspaceId = CurrentWorkspace(requestContext);
        var page = @namespace is null
            ? await service.ListAllAsync(workspaceId, actualSkip, actualTop, token)
            : await service.ListAsync(workspaceId, @namespace.Value, actualSkip, actualTop, token);
        var prefix = @namespace is null
            ? "/api/flows?allNamespaces=true&"
            : @namespace.Value.IsDefault
                ? "/api/flows?"
                : $"/api/namespaces/{@namespace.Value.Value}/flows?";
        var next = page.HasMore ? $"{prefix}skip={actualSkip + actualTop}&top={actualTop}" : null;
        return Results.Ok(new FlowPageResponse(page.Items.Select(item => ToSummary(item.Value)).ToArray(), next));
    });

    private static Task<IResult> GetAsync(string id, HttpResponse response, FlowService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var stored = await RequiredAsync(CurrentWorkspace(requestContext), id, service, token);
        response.Headers.ETag = stored.ETag;
        return Results.Ok(ToResponse(stored.Value));
    });

    private static Task<IResult> UpdateAsync(string id, UpdateFlowRequest body, HttpRequest request, HttpResponse response, FlowService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var workspaceId = CurrentWorkspace(requestContext);
        var current = await RequiredAsync(workspaceId, id, service, token);
        var etag = request.Headers.IfMatch.FirstOrDefault() ?? current.ETag;
        var stored = await service.UpdateAsync(workspaceId, new FlowId(id), new UpdateFlowCommand(body.Description, body.Version, body.Enabled, body.Definition, body.Metadata), etag, token);
        response.Headers.ETag = stored.ETag;
        return Results.Ok(ToResponse(stored.Value));
    });

    private static Task<IResult> DeleteAsync(string id, HttpRequest request, FlowService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        await service.DeleteAsync(CurrentWorkspace(requestContext), new FlowId(id), request.Headers.IfMatch.FirstOrDefault(), token);
        return Results.NoContent();
    });

    private static Task<IResult> CreateVersionAsync(string id, CreateFlowVersionRequest body, HttpResponse response, FlowService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var stored = await service.PublishVersionAsync(CurrentWorkspace(requestContext), new FlowId(id), body.Version, body.Activate, token);
        response.Headers.ETag = stored.ETag;
        response.Headers.Location = $"/api/flows/{id}/versions/{body.Version}";
        return Results.Json(ToVersion(stored.Value), statusCode: StatusCodes.Status201Created);
    });

    private static Task<IResult> GetVersionAsync(string id, string version, HttpResponse response, FlowService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var stored = await service.GetVersionAsync(CurrentWorkspace(requestContext), new FlowId(id), version, token) ?? throw new FlowNotFoundException(new FlowId($"{id}:{version}"));
        response.Headers.ETag = stored.ETag;
        return Results.Ok(ToVersion(stored.Value));
    });

    private static Task<IResult> ListVersionsAsync(string id, FlowService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var workspaceId = CurrentWorkspace(requestContext);
        _ = await RequiredAsync(workspaceId, id, service, token);
        return Results.Ok((await service.ListVersionsAsync(workspaceId, new FlowId(id), token)).Select(item => ToVersion(item.Value)));
    });

    private static Task<IResult> ListNamespacedVersionsAsync(string @namespace, string id, FlowService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var flowId = new FlowId(id, ResourceNamespace.Parse(@namespace));
        var workspaceId = CurrentWorkspace(requestContext);
        _ = await service.GetAsync(workspaceId, flowId, token) ?? throw new FlowNotFoundException(flowId);
        return Results.Ok((await service.ListVersionsAsync(workspaceId, flowId, token)).Select(item => ToVersion(item.Value)));
    });

    private static Task<IResult> CreateRunAsync(string id, CreateFlowRunRequest body, HttpContext context, HttpResponse response, FlowRunService service, ICurrentRequestContext requestContext, CancellationToken token) =>
        CreateRunCoreAsync(new FlowId(id), body, context, response, service, requestContext, token);

    private static Task<IResult> CreateNamespacedRunAsync(string @namespace, string id, CreateFlowRunRequest body, HttpContext context, HttpResponse response, FlowRunService service, ICurrentRequestContext requestContext, CancellationToken token) =>
        CreateRunCoreAsync(new FlowId(id, ResourceNamespace.Parse(@namespace)), body, context, response, service, requestContext, token);

    private static Task<IResult> CreateRunCoreAsync(FlowId flowId, CreateFlowRunRequest body, HttpContext context, HttpResponse response, FlowRunService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var scope = CurrentScope(requestContext);
        var startedBy = context.Features.Get<ResolvedPrincipalFeature>()?.Principal.DisplayName ?? scope.PrincipalId.ToString("D");
        var stored = await service.CreateAsync(flowId, body.Version, body.DeploymentResourceId, body.Trigger, startedBy, body.CorrelationId, body.Input, scope, token);
        response.Headers.Location = $"/api/flowRuns/{stored.Value.Id}";
        response.Headers.ETag = stored.ETag;
        return Results.Accepted($"/api/flowRuns/{stored.Value.Id}", stored.Value);
    });

    private static Task<IResult> ListFlowRunsAsync(string id, FlowRunStatus? status, int? skip, int? top, FlowRunService service, ICurrentRequestContext requestContext, CancellationToken token) =>
        ListRunsCoreAsync(new FlowId(id), status, skip, top, service, requestContext, token);

    private static Task<IResult> ListNamespacedFlowRunsAsync(string @namespace, string id, FlowRunStatus? status, int? skip, int? top, FlowRunService service, ICurrentRequestContext requestContext, CancellationToken token) =>
        ListRunsCoreAsync(new FlowId(id, ResourceNamespace.Parse(@namespace)), status, skip, top, service, requestContext, token);

    private static Task<IResult> ListRunsAsync(string? flowId, FlowRunStatus? status, int? skip, int? top, FlowRunService service, ICurrentRequestContext requestContext, CancellationToken token) =>
        ListRunsCoreAsync(string.IsNullOrWhiteSpace(flowId) ? null : new FlowId(flowId), status, skip, top, service, requestContext, token);

    private static Task<IResult> ListRunsCoreAsync(FlowId? flowId, FlowRunStatus? status, int? skip, int? top, FlowRunService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var actualSkip = Math.Max(0, skip ?? 0);
        var actualTop = Math.Clamp(top ?? 50, 1, 200);
        var page = await service.ListAsync(flowId, status, actualSkip, actualTop, CurrentScope(requestContext), token);
        var next = page.HasMore ? $"/api/flowRuns?skip={actualSkip + actualTop}&top={actualTop}" : null;
        return Results.Ok(new FlowRunPageResponse(page.Items.Select(item => item.Value).ToArray(), next));
    });

    private static Task<IResult> GetFlowRunAsync(string id, string runId, HttpResponse response, FlowRunService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var stored = await RequiredRunAsync(runId, service, CurrentScope(requestContext), token);
        if (stored.Value.FlowId != new FlowId(id)) throw new FlowRunNotFoundException(runId);
        response.Headers.ETag = stored.ETag;
        return Results.Ok(stored.Value);
    });

    private static Task<IResult> GetRunAsync(string runId, HttpResponse response, FlowRunService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var stored = await RequiredRunAsync(runId, service, CurrentScope(requestContext), token);
        response.Headers.ETag = stored.ETag;
        return Results.Ok(stored.Value);
    });

    private static Task<IResult> CancelFlowRunAsync(string id, string runId, FlowRunService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var scope = CurrentScope(requestContext);
        var stored = await RequiredRunAsync(runId, service, scope, token);
        if (stored.Value.FlowId != new FlowId(id)) throw new FlowRunNotFoundException(runId);
        return Results.Ok((await service.CancelAsync(runId, scope, token)).Value);
    });

    private static Task<IResult> CancelRunAsync(string runId, FlowRunService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
        Results.Ok((await service.CancelAsync(runId, CurrentScope(requestContext), token)).Value));

    private static Task<IResult> ListInputsAsync(string runId, InputRequestStatus? status, FlowRunService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var scope = CurrentScope(requestContext);
        var inputs = await service.ListInputsAsync(runId, status, scope, token);
        return Results.Ok(inputs.Select(value => value.Value));
    });

    private static Task<IResult> GetInputAsync(string runId, string inputId, HttpResponse response, FlowRunService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var scope = CurrentScope(requestContext);
        var input = await service.GetInputAsync(runId, inputId, scope, token)
            ?? throw new FlowValidationException("input_request_not_found", $"Input Request '{inputId}' was not found.");
        response.Headers.ETag = input.ETag;
        return Results.Ok(input.Value);
    });

    private static Task<IResult> RespondToInputAsync(
        string runId,
        string inputId,
        SubmitInputResponseRequest body,
        HttpContext context,
        FlowRunService service,
        ICurrentRequestContext requestContext,
        CancellationToken token) => ExecuteAsync(async () =>
    {
        var scope = CurrentScope(requestContext);
        var principal = context.Features.Get<ResolvedPrincipalFeature>()?.Principal.Id.ToString("D")
            ?? requestContext.Current.PrincipalId.ToString("D");
        return Results.Accepted($"/api/flowRuns/{runId}", (await service.RespondAsync(runId, inputId, body.Value, principal, scope, token)).Value);
    });

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

    private static async Task ObserveRunAsync(string runId, HttpResponse response, FlowRunService service, ICurrentRequestContext requestContext, CancellationToken token)
    {
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        await foreach (var run in service.ObserveAsync(runId, CurrentScope(requestContext), token))
        {
            await response.WriteAsync($"data: {JsonSerializer.Serialize(run, JsonOptions)}\n\n", token);
            await response.Body.FlushAsync(token);
        }
    }

    private static Task<IResult> ListRunEventsAsync(string runId, long? afterSequence, FlowRunService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        _ = await RequiredRunAsync(runId, service, CurrentScope(requestContext), token);
        return Results.Ok(await service.ListEventsAsync(CurrentScope(requestContext), runId, Math.Max(0, afterSequence ?? 0), token));
    });

    private static async Task<StoredFlow> RequiredAsync(WorkspaceId workspaceId, string id, FlowService service, CancellationToken token) =>
        await service.GetAsync(workspaceId, new FlowId(id), token) ?? throw new FlowNotFoundException(new FlowId(id));
    private static async Task<StoredFlowRun> RequiredRunAsync(string id, FlowRunService service, FlowRunScope scope, CancellationToken token) =>
        await service.GetAsync(id, scope, token) ?? throw new FlowRunNotFoundException(id);
    private static FlowRunScope CurrentScope(ICurrentRequestContext requestContext)
    {
        if (!requestContext.IsInitialized)
            throw new FlowValidationException("flow_run_context_required", "A Flow Run requires an authenticated Workspace context.");
        var current = requestContext.Current;
        return new(current.TenantId, new WorkspaceId(current.WorkspaceId), current.PrincipalId);
    }
    private static WorkspaceId CurrentWorkspace(ICurrentRequestContext requestContext) => CurrentScope(requestContext).WorkspaceId;

    private static FlowResponse ToResponse(FlowResource value) => new(value.Id.Value, value.Name, value.Description, value.Version, value.Enabled, value.ActiveVersion, value.Definition, value.Metadata, value.CreatedAt, value.UpdatedAt, value.Graph) { Namespace = value.Id.Namespace };
    private static FlowSummaryResponse ToSummary(FlowResource value) => new(value.Id.Value, value.Name, value.Description, value.Definition.Kind, value.Version, value.Enabled, value.ActiveVersion, value.UpdatedAt) { Namespace = value.Id.Namespace };
    private static FlowVersionResponse ToVersion(FlowVersion value) => new(value.FlowId.Value, value.Version, value.Description, value.Definition, value.Metadata, value.PublishedAt, value.Graph, value.DefinitionHash, value.ReleaseNotes) { Namespace = value.FlowId.Namespace };

    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (FlowNotFoundException exception) { return Results.Problem(statusCode: 404, title: "flow_not_found", detail: exception.Message); }
        catch (FlowRunNotFoundException exception) { return Results.Problem(statusCode: 404, title: "flow_run_not_found", detail: exception.Message); }
        catch (InputRequestAlreadyResolvedException exception) { return Results.Problem(statusCode: 409, title: "input_request_already_resolved", detail: exception.Message); }
        catch (FlowConcurrencyException exception) { return Results.Problem(statusCode: 412, title: "precondition_failed", detail: exception.Message); }
        catch (FlowValidationException exception) { return Results.Problem(statusCode: 400, title: exception.Code, detail: exception.Message); }
        catch (ArgumentException exception) { return Results.Problem(statusCode: 400, title: "validation_failed", detail: exception.Message); }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static string RequiredIfMatch(HttpRequest request) => request.Headers.IfMatch.FirstOrDefault()
        ?? throw new FlowValidationException("if_match_required", "Saving a Flow Draft requires an If-Match ETag.");
}
