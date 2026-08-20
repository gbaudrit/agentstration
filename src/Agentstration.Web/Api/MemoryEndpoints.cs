using Agentstration.Management.Abstractions;
using Agentstration.Memory;
using Agentstration.Memory.Application;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Core;
using Agentstration.Web.Security;

namespace Agentstration.Web;

public sealed record MemoryScopeRequest(string Kind, string Name, string? Namespace = null);
public sealed record WriteMemoryRequest(MemoryScopeRequest Scope, string Content, string Reason, IReadOnlyList<string>? Tags = null, DateTimeOffset? ExpiresAt = null);
public sealed record WriteRuntimeMemoryRequest(string Content, string Reason, IReadOnlyList<string>? Tags = null, DateTimeOffset? ExpiresAt = null);
public sealed record MemoryRecordPage(IReadOnlyList<MemoryRecord> Value, string? NextLink);

public static class MemoryEndpoints
{
    public static IEndpointRouteBuilder MapAgentstrationMemoryApi(this IEndpointRouteBuilder endpoints)
    {
        var records = endpoints.MapGroup("/api/memory/records").RequireAuthorization(AgentstrationPolicies.Authenticated);
        records.MapPost("/", WriteAsync).RequireAuthorization(AgentstrationPolicies.CanWriteMemory);
        records.MapGet("/", ListAsync).RequireAuthorization(AgentstrationPolicies.CanReadMemory);
        records.MapDelete("/{recordId:guid}", DeleteAsync).RequireAuthorization(AgentstrationPolicies.CanDeleteMemory);
        records.MapDelete("/", ClearAsync).RequireAuthorization(AgentstrationPolicies.CanDeleteMemory);
        endpoints.MapPost("/api/runtime/runs/{runId}/memory-records", WriteFromRunAsync)
            .RequireAuthorization(AgentstrationPolicies.CanWriteMemory);
        return endpoints;
    }

    private static Task<IResult> WriteAsync(
        WriteMemoryRequest body,
        Agentstration.Memory.Application.MemoryService memories,
        IControlPlaneStore controlPlane,
        ICurrentRequestContext requestContext,
        CancellationToken cancellationToken) => ExecuteAsync(async () =>
    {
        var current = requestContext.Current;
        var scope = await ResolveScopeAsync(body.Scope, controlPlane, cancellationToken);
        var value = await memories.WriteAsync(new WriteMemoryCommand(
            new WorkspaceId(current.WorkspaceId), scope, body.Content, body.Tags ?? [], MemorySourceKind.Manual,
            null, body.Reason, current.PrincipalId, body.ExpiresAt), cancellationToken);
        return Results.Created($"/api/memory/records/{value.Id}", value);
    });

    private static Task<IResult> WriteFromRunAsync(
        string runId,
        WriteRuntimeMemoryRequest body,
        Agentstration.Memory.Application.MemoryService memories,
        RuntimeRunService runs,
        IRuntimeAgentResolver agents,
        ICurrentRequestContext requestContext,
        CancellationToken cancellationToken) => ExecuteAsync(async () =>
    {
        var current = requestContext.Current;
        var workspaceId = new WorkspaceId(current.WorkspaceId);
        var run = await runs.GetAsync(workspaceId, runId, cancellationToken) ?? throw new RuntimeRunNotFoundException(runId);
        var agent = await agents.ResolveAsync(run.Value.Properties.Agent, cancellationToken);
        var value = await memories.WriteAsync(new WriteMemoryCommand(
            workspaceId, MemoryScope.ForAgent(agent.Definition.AgentId), body.Content, body.Tags ?? [], MemorySourceKind.RuntimeRun,
            runId, body.Reason, current.PrincipalId, body.ExpiresAt), cancellationToken);
        return Results.Created($"/api/memory/records/{value.Id}", value);
    });

    private static Task<IResult> ListAsync(
        string? scopeKind, string? scopeName, string? scopeNamespace, int? skip, int? top,
        Agentstration.Memory.Application.MemoryService memories,
        IControlPlaneStore controlPlane,
        ICurrentRequestContext requestContext,
        CancellationToken cancellationToken) => ExecuteAsync(async () =>
    {
        var actualSkip = Math.Max(0, skip ?? 0);
        var actualTop = Math.Clamp(top ?? 50, 1, MemoryLimits.MaximumAdministrationPageSize);
        var scope = scopeKind is null ? null : await ResolveScopeAsync(new(scopeKind, scopeName ?? string.Empty, scopeNamespace), controlPlane, cancellationToken);
        var values = await memories.ListAsync(new WorkspaceId(requestContext.Current.WorkspaceId), scope, actualSkip, actualTop, cancellationToken);
        var next = values.Count == actualTop ? BuildNextLink(scopeKind, scopeName, scopeNamespace, actualSkip + actualTop, actualTop) : null;
        return Results.Ok(new MemoryRecordPage(values, next));
    });

    private static Task<IResult> DeleteAsync(
        Guid recordId, Agentstration.Memory.Application.MemoryService memories, ICurrentRequestContext requestContext, CancellationToken cancellationToken) =>
        ExecuteAsync(async () => await memories.DeleteAsync(new WorkspaceId(requestContext.Current.WorkspaceId), new MemoryRecordId(recordId), cancellationToken)
            ? Results.NoContent() : Results.NotFound());

    private static Task<IResult> ClearAsync(
        string scopeKind, string scopeName, string? scopeNamespace,
        Agentstration.Memory.Application.MemoryService memories, IControlPlaneStore controlPlane, ICurrentRequestContext requestContext,
        CancellationToken cancellationToken) => ExecuteAsync(async () =>
    {
        var scope = await ResolveScopeAsync(new(scopeKind, scopeName, scopeNamespace), controlPlane, cancellationToken);
        return Results.Ok(new { deleted = await memories.ClearScopeAsync(new WorkspaceId(requestContext.Current.WorkspaceId), scope, cancellationToken) });
    });

    private static async Task<MemoryScope> ResolveScopeAsync(MemoryScopeRequest request, IControlPlaneStore controlPlane, CancellationToken cancellationToken)
    {
        if (string.Equals(request.Kind, "shared", StringComparison.OrdinalIgnoreCase))
        {
            var scope = MemoryScope.Shared(request.Name.Trim());
            MemoryValidator.ValidateScope(scope);
            return scope;
        }
        if (!string.Equals(request.Kind, "agent", StringComparison.OrdinalIgnoreCase))
            throw new MemoryValidationException("memory_scope_kind_invalid", "Memory scope kind must be 'agent' or 'shared'.");
        var @namespace = ResourceNamespace.Parse(request.Namespace);
        var agent = await controlPlane.GetAsync<AgentResource>(new ResourceKey(ResourceKinds.Agent, request.Name, @namespace), cancellationToken)
            ?? throw new KeyNotFoundException($"Agent '{@namespace}/{request.Name}' was not found.");
        return MemoryScope.ForAgent(agent.Value.Uid);
    }

    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (MemoryValidationException exception) { return Results.Problem(statusCode: 400, title: exception.Code, detail: exception.Message); }
        catch (RuntimeRunNotFoundException exception) { return Results.Problem(statusCode: 404, title: "run_not_found", detail: exception.Message); }
        catch (RuntimeAgentResolutionException exception) { return Results.Problem(statusCode: 409, title: exception.Code, detail: exception.Message); }
        catch (KeyNotFoundException exception) { return Results.Problem(statusCode: 404, title: "resource_not_found", detail: exception.Message); }
        catch (ArgumentException exception) { return Results.Problem(statusCode: 400, title: "validation_failed", detail: exception.Message); }
    }

    private static string BuildNextLink(string? scopeKind, string? scopeName, string? scopeNamespace, int skip, int top)
    {
        var link = $"/api/memory/records?skip={skip}&top={top}";
        if (scopeKind is null) return link;
        link += $"&scopeKind={Uri.EscapeDataString(scopeKind)}&scopeName={Uri.EscapeDataString(scopeName ?? string.Empty)}";
        return scopeNamespace is null ? link : $"{link}&scopeNamespace={Uri.EscapeDataString(scopeNamespace)}";
    }
}
