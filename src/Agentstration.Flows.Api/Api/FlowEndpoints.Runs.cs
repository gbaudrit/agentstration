using System.Text.Json;
using Agentstration.Application.Work;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Flows.Contracts;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Security;

namespace Agentstration.Web;

public static partial class FlowEndpoints
{
    private static Task<IResult> CreateRunAsync(string id, CreateFlowRunRequest body, HttpContext context, HttpResponse response, RootFlowSubmissionService service, IFlowRunExecutionScope executionScopes, ICurrentRequestContext requestContext, CancellationToken token) =>
        CreateRunCoreAsync(new FlowId(id), body, context, response, service, executionScopes, requestContext, token);

    private static Task<IResult> CreateNamespacedRunAsync(string @namespace, string id, CreateFlowRunRequest body, HttpContext context, HttpResponse response, RootFlowSubmissionService service, IFlowRunExecutionScope executionScopes, ICurrentRequestContext requestContext, CancellationToken token) =>
        CreateRunCoreAsync(new FlowId(id, ResourceNamespace.Parse(@namespace)), body, context, response, service, executionScopes, requestContext, token);

    private static Task<IResult> CreateRunCoreAsync(FlowId flowId, CreateFlowRunRequest body, HttpContext context, HttpResponse response, RootFlowSubmissionService service, IFlowRunExecutionScope executionScopes, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var scope = CurrentScope(requestContext, body.ExecutionScopeRef);
        await executionScopes.ValidateAsync(scope, token);
        using var activeScope = executionScopes.Enter(scope);
        var startedBy = context.Features.Get<ResolvedPrincipalFeature>()?.DisplayName ?? scope.PrincipalId.ToString("D");
        var origin = string.Equals(context.Request.Headers["X-Agentstration-Origin"].FirstOrDefault(), "Console", StringComparison.OrdinalIgnoreCase)
            ? FlowInvocationOrigin.Console
            : FlowInvocationOrigin.Api;
        var submission = await service.SubmitAsync(new SubmitRootFlowCommand(
            scope.WorkspaceId,
            new FlowReference(flowId, body.Version, body.Version is null, flowId.Namespace, body.DefinitionScopeRef),
            body.Input,
            origin,
            startedBy,
            body.Trigger,
            context.Request.Headers["Idempotency-Key"].FirstOrDefault(),
            context.Request.Headers["X-Causation-Id"].FirstOrDefault(),
            body.CorrelationId,
            Type: "flow-api",
            Instruction: $"Execute Flow '{flowId}'.")
        {
            ExecutionScopeRef = scope.ScopeRef
        }, token);
        response.Headers.Location = $"/api/flowRuns/{submission.FlowRun.Run.Id}";
        response.Headers.ETag = submission.FlowRun.ETag;
        return Results.Accepted($"/api/flowRuns/{submission.FlowRun.Run.Id}", submission.FlowRun.Run);
    });

    private static Task<IResult> ListFlowRunsAsync(string id, string? scopeRef, FlowRunStatus? status, int? skip, int? top, FlowRunService service, IFlowRunExecutionScope executionScopes, ICurrentRequestContext requestContext, CancellationToken token) =>
        ListRunsCoreAsync(new FlowId(id), scopeRef, status, skip, top, $"/api/flows/{Uri.EscapeDataString(id)}/runs", false, service, executionScopes, requestContext, token);

    private static Task<IResult> ListNamespacedFlowRunsAsync(string @namespace, string id, string? scopeRef, FlowRunStatus? status, int? skip, int? top, FlowRunService service, IFlowRunExecutionScope executionScopes, ICurrentRequestContext requestContext, CancellationToken token) =>
        ListRunsCoreAsync(new FlowId(id, ResourceNamespace.Parse(@namespace)), scopeRef, status, skip, top,
            $"/api/namespaces/{Uri.EscapeDataString(@namespace)}/flows/{Uri.EscapeDataString(id)}/runs", false, service, executionScopes, requestContext, token);

    private static Task<IResult> ListRunsAsync(string? flowId, string? scopeRef, FlowRunStatus? status, int? skip, int? top, FlowRunService service, IFlowRunExecutionScope executionScopes, ICurrentRequestContext requestContext, CancellationToken token) =>
        ListRunsCoreAsync(string.IsNullOrWhiteSpace(flowId) ? null : new FlowId(flowId), scopeRef, status, skip, top, "/api/flowRuns", true, service, executionScopes, requestContext, token);

    private static Task<IResult> ListRunsCoreAsync(FlowId? flowId, string? scopeRef, FlowRunStatus? status, int? skip, int? top, string route, bool includeFlowIdFilter, FlowRunService service, IFlowRunExecutionScope executionScopes, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var actualSkip = Math.Max(0, skip ?? 0);
        var actualTop = Math.Clamp(top ?? 50, 1, 200);
        var scope = CurrentScope(requestContext, ParseScope(scopeRef));
        await executionScopes.ValidateAsync(scope, token);
        var page = await service.ListAsync(flowId, status, actualSkip, actualTop, scope, token);
        var next = page.HasMore ? FlowRunNextLink(route, includeFlowIdFilter ? flowId : null, status, scope.ScopeRef, actualSkip + actualTop, actualTop) : null;
        return Results.Ok(new FlowRunPageResponse(page.Items.Select(item => item.Value).ToArray(), next));
    });

    private static string FlowRunNextLink(string route, FlowId? flowId, FlowRunStatus? status, ResourceScopeRef scopeRef, int skip, int top)
    {
        var filters = new List<string>();
        if (flowId is not null) filters.Add($"flowId={Uri.EscapeDataString(flowId.Value.Value)}");
        if (status is not null) filters.Add($"status={Uri.EscapeDataString(status.Value.ToString())}");
        filters.Add($"scopeRef={Uri.EscapeDataString(scopeRef.Value)}");
        filters.Add($"skip={skip}");
        filters.Add($"top={top}");
        return $"{route}?{string.Join('&', filters)}";
    }

    private static Task<IResult> GetFlowRunAsync(string id, string runId, string? scopeRef, HttpResponse response, FlowRunService service, IFlowRunExecutionScope executionScopes, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var scope = CurrentScope(requestContext, ParseScope(scopeRef));
        await executionScopes.ValidateAsync(scope, token);
        var stored = await RequiredRunAsync(runId, service, scope, token);
        if (stored.Value.FlowId != new FlowId(id)) throw new FlowRunNotFoundException(runId);
        response.Headers.ETag = stored.ETag;
        return Results.Ok(stored.Value);
    });

    private static Task<IResult> GetRunAsync(string runId, string? scopeRef, HttpResponse response, FlowRunService service, IFlowRunExecutionScope executionScopes, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var scope = CurrentScope(requestContext, ParseScope(scopeRef));
        await executionScopes.ValidateAsync(scope, token);
        var stored = await RequiredRunAsync(runId, service, scope, token);
        response.Headers.ETag = stored.ETag;
        return Results.Ok(stored.Value);
    });

    private static Task<IResult> GetRunCausalityAsync(
        string runId,
        string? scopeRef,
        int? skip,
        int? top,
        FlowRunService service,
        IFlowRunExecutionScope executionScopes,
        ICurrentRequestContext requestContext,
        CancellationToken token) => ExecuteAsync(async () =>
    {
        var actualSkip = Math.Max(0, skip ?? 0);
        var actualTop = Math.Clamp(top ?? 25, 1, 100);
        var scope = CurrentScope(requestContext, ParseScope(scopeRef));
        await executionScopes.ValidateAsync(scope, token);
        var page = await service.GetCausalityAsync(runId, actualSkip, actualTop, scope, token);
        var next = page.HasMore
            ? $"/api/flowRuns/{Uri.EscapeDataString(runId)}/causality?scopeRef={Uri.EscapeDataString(scope.ScopeRef.Value)}&skip={actualSkip + page.Items.Count}&top={actualTop}"
            : null;
        return Results.Ok(new FlowRunCausalityPageResponse(page.Origin, page.Items, page.TotalCount, next));
    });

    private static Task<IResult> DeleteRunAsync(string runId, string? scopeRef, HttpRequest request, FlowRunService service, IFlowRunExecutionScope executionScopes, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var expectedETag = request.Headers.IfMatch.FirstOrDefault()
            ?? throw new FlowValidationException("if_match_required", "Deleting a Flow Run requires an If-Match ETag.");
        var scope = CurrentScope(requestContext, ParseScope(scopeRef));
        await executionScopes.ValidateAsync(scope, token);
        await service.DeleteAsync(runId, expectedETag, scope, token);
        return Results.NoContent();
    });

    private static Task<IResult> CancelFlowRunAsync(string id, string runId, string? scopeRef, FlowRunService service, IFlowRunExecutionScope executionScopes, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var scope = CurrentScope(requestContext, ParseScope(scopeRef));
        await executionScopes.ValidateAsync(scope, token);
        var stored = await RequiredRunAsync(runId, service, scope, token);
        if (stored.Value.FlowId != new FlowId(id)) throw new FlowRunNotFoundException(runId);
        return Results.Ok((await service.CancelAsync(runId, scope, token)).Value);
    });

    private static Task<IResult> CancelRunAsync(string runId, string? scopeRef, FlowRunService service, IFlowRunExecutionScope executionScopes, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var scope = CurrentScope(requestContext, ParseScope(scopeRef));
        await executionScopes.ValidateAsync(scope, token);
        return Results.Ok((await service.CancelAsync(runId, scope, token)).Value);
    });

    private static Task<IResult> ListInputsAsync(string runId, string? scopeRef, InputRequestStatus? status, FlowRunService service, IFlowRunExecutionScope executionScopes, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var scope = CurrentScope(requestContext, ParseScope(scopeRef));
        await executionScopes.ValidateAsync(scope, token);
        var inputs = await service.ListInputsAsync(runId, status, scope, token);
        return Results.Ok(inputs.Select(value => value.Value));
    });

    private static Task<IResult> GetInputAsync(string runId, string inputId, string? scopeRef, HttpResponse response, FlowRunService service, IFlowRunExecutionScope executionScopes, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var scope = CurrentScope(requestContext, ParseScope(scopeRef));
        await executionScopes.ValidateAsync(scope, token);
        var input = await service.GetInputAsync(runId, inputId, scope, token)
            ?? throw new FlowValidationException("input_request_not_found", $"Input Request '{inputId}' was not found.");
        response.Headers.ETag = input.ETag;
        return Results.Ok(input.Value);
    });

    private static Task<IResult> RespondToInputAsync(
        string runId,
        string inputId,
        string? scopeRef,
        SubmitInputResponseRequest body,
        HttpContext context,
        FlowRunService service,
        IFlowRunExecutionScope executionScopes,
        ICurrentRequestContext requestContext,
        CancellationToken token) => ExecuteAsync(async () =>
    {
        var scope = CurrentScope(requestContext, ParseScope(scopeRef));
        await executionScopes.ValidateAsync(scope, token);
        var principal = context.Features.Get<ResolvedPrincipalFeature>()?.PrincipalId.ToString("D")
            ?? requestContext.Current.PrincipalId.ToString("D");
        return Results.Accepted($"/api/flowRuns/{runId}", (await service.RespondAsync(runId, inputId, body.Value, principal, scope, token)).Value);
    });

    private static Task<IResult> ObserveRunAsync(string runId, string? scopeRef, HttpResponse response, FlowRunService service, IFlowRunExecutionScope executionScopes, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var scope = CurrentScope(requestContext, ParseScope(scopeRef));
        await executionScopes.ValidateAsync(scope, token);
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        await foreach (var run in service.ObserveAsync(runId, scope, token))
        {
            await response.WriteAsync($"data: {JsonSerializer.Serialize(run, JsonOptions)}\n\n", token);
            await response.Body.FlushAsync(token);
        }
        return Results.Empty;
    });

    private static Task<IResult> ListRunEventsAsync(string runId, string? scopeRef, long? afterSequence, FlowRunService service, IFlowRunExecutionScope executionScopes, ICurrentRequestContext requestContext, CancellationToken token) => ExecuteAsync(async () =>
    {
        var scope = CurrentScope(requestContext, ParseScope(scopeRef));
        await executionScopes.ValidateAsync(scope, token);
        return Results.Ok(await service.ListEventsAsync(scope, runId, Math.Max(0, afterSequence ?? 0), token));
    });

    private static async Task<StoredFlowRun> RequiredRunAsync(string id, FlowRunService service, FlowRunScope scope, CancellationToken token) =>
        await service.GetAsync(id, scope, token) ?? throw new FlowRunNotFoundException(id);

    private static FlowRunScope CurrentScope(ICurrentRequestContext requestContext, ResourceScopeRef? requestedScopeRef = null)
        => FlowRunHttp.CurrentScope(requestContext, requestedScopeRef);

    private static ResourceScopeRef? ParseScope(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : ResourceScopeRef.Parse(value);
}
