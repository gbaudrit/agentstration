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
        ListRunsCoreAsync(new FlowId(id), status, skip, top, $"/api/flows/{Uri.EscapeDataString(id)}/runs", false, service, requestContext, token);

    private static Task<IResult> ListNamespacedFlowRunsAsync(string @namespace, string id, FlowRunStatus? status, int? skip, int? top, FlowRunService service, ICurrentRequestContext requestContext, CancellationToken token) =>
        ListRunsCoreAsync(new FlowId(id, ResourceNamespace.Parse(@namespace)), status, skip, top,
            $"/api/namespaces/{Uri.EscapeDataString(@namespace)}/flows/{Uri.EscapeDataString(id)}/runs", false, service, requestContext, token);

    private static Task<IResult> ListRunsAsync(string? flowId, FlowRunStatus? status, int? skip, int? top, FlowRunService service, ICurrentRequestContext requestContext, CancellationToken token) =>
        ListRunsCoreAsync(string.IsNullOrWhiteSpace(flowId) ? null : new FlowId(flowId), status, skip, top, "/api/flowRuns", true, service, requestContext, token);

    private static Task<IResult> ListRunsCoreAsync(FlowId? flowId, FlowRunStatus? status, int? skip, int? top, string route, bool includeFlowIdFilter, FlowRunService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var actualSkip = Math.Max(0, skip ?? 0);
        var actualTop = Math.Clamp(top ?? 50, 1, 200);
        var page = await service.ListAsync(flowId, status, actualSkip, actualTop, CurrentScope(requestContext), token);
        var next = page.HasMore ? FlowRunNextLink(route, includeFlowIdFilter ? flowId : null, status, actualSkip + actualTop, actualTop) : null;
        return Results.Ok(new FlowRunPageResponse(page.Items.Select(item => item.Value).ToArray(), next));
    });

    private static string FlowRunNextLink(string route, FlowId? flowId, FlowRunStatus? status, int skip, int top)
    {
        var filters = new List<string>();
        if (flowId is not null) filters.Add($"flowId={Uri.EscapeDataString(flowId.Value.Value)}");
        if (status is not null) filters.Add($"status={Uri.EscapeDataString(status.Value.ToString())}");
        filters.Add($"skip={skip}");
        filters.Add($"top={top}");
        return $"{route}?{string.Join('&', filters)}";
    }

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

    private static Task<IResult> DeleteRunAsync(string runId, HttpRequest request, FlowRunService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var expectedETag = request.Headers.IfMatch.FirstOrDefault()
            ?? throw new FlowValidationException("if_match_required", "Deleting a Flow Run requires an If-Match ETag.");
        await service.DeleteAsync(runId, expectedETag, CurrentScope(requestContext), token);
        return Results.NoContent();
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

    private static Task<IResult> ObserveRunAsync(string runId, HttpResponse response, FlowRunService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        await foreach (var run in service.ObserveAsync(runId, CurrentScope(requestContext), token))
        {
            await response.WriteAsync($"data: {JsonSerializer.Serialize(run, JsonOptions)}\n\n", token);
            await response.Body.FlushAsync(token);
        }
        return Results.Empty;
    });

    private static Task<IResult> ListRunEventsAsync(string runId, long? afterSequence, FlowRunService service, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        return Results.Ok(await service.ListEventsAsync(CurrentScope(requestContext), runId, Math.Max(0, afterSequence ?? 0), token));
    });

    private static async Task<StoredFlowRun> RequiredRunAsync(string id, FlowRunService service, FlowRunScope scope, CancellationToken token) =>
        await service.GetAsync(id, scope, token) ?? throw new FlowRunNotFoundException(id);

    private static FlowRunScope CurrentScope(ICurrentRequestContext requestContext)
    {
        if (!requestContext.IsInitialized)
            throw new FlowValidationException("flow_run_context_required", "A Flow Run requires an authenticated Workspace context.");
        var current = requestContext.Current;
        return new(current.TenantId, new WorkspaceId(current.WorkspaceId), current.PrincipalId);
    }
}
