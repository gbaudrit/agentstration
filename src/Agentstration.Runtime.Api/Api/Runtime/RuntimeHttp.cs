using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;

namespace Agentstration.Web.Api.Runtime;

internal static class RuntimeHttp
{
    public static RuntimeRunScope CurrentScope(ICurrentRequestContext context, ResourceScopeRef? requestedScopeRef = null)
    {
        var current = context.Current;
        var requested = requestedScopeRef ?? ResourceScopeRef.Workspace(current.WorkspaceId);
        return requested.Kind switch
        {
            ResourceScopeKind.Instance => RuntimeRunScope.Instance(current.PrincipalId),
            ResourceScopeKind.Tenant when requested.TargetId == current.TenantId =>
                RuntimeRunScope.Tenant(current.TenantId, current.PrincipalId),
            ResourceScopeKind.Workspace when requested.TargetId == current.WorkspaceId =>
                RuntimeRunScope.Workspace(current.TenantId, new WorkspaceId(current.WorkspaceId), current.PrincipalId),
            _ => throw new RuntimeRunValidationException("execution_scope_not_authorized",
                "The requested execution scope is not the current Instance, Tenant, or Workspace context.")
        };
    }

    public static ResourceScopeRef RequestedScope(ICurrentRequestContext context, string? scopeRef) =>
        CurrentScope(context, string.IsNullOrWhiteSpace(scopeRef) ? null : ResourceScopeRef.Parse(scopeRef)).ScopeRef;

    public static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (RuntimeRunNotFoundException exception) { return Results.Problem(statusCode: 404, title: "run_not_found", detail: exception.Message); }
        catch (RuntimeRunNotTerminalException exception) { return Results.Problem(statusCode: 409, title: "run_not_terminal", detail: exception.Message); }
        catch (RuntimeRunConcurrencyException exception) { return Results.Problem(statusCode: 412, title: "precondition_failed", detail: exception.Message); }
        catch (RuntimeRunValidationException exception) { return Results.Problem(statusCode: 400, title: exception.Code, detail: exception.Message); }
        catch (ResourceNotFoundException exception) { return Results.Problem(statusCode: 404, title: "resource_not_found", detail: exception.Message); }
        catch (ResourceConcurrencyException exception) { return Results.Problem(statusCode: 409, title: "resource_version_conflict", detail: exception.Message); }
        catch (ArgumentException exception) { return Results.Problem(statusCode: 400, title: "validation_failed", detail: exception.Message); }
        catch (InvalidOperationException exception) { return Results.Problem(statusCode: 409, title: "operation_conflict", detail: exception.Message); }
    }

    public static IResult RunResult(StoredRuntimeRun stored, HttpResponse response, int statusCode)
    {
        response.Headers.ETag = stored.ETag;
        return Results.Json(stored.Value, statusCode: statusCode);
    }

    public static void RequireApiVersion(HttpRequest request)
    {
        if (request.Query.TryGetValue("api-version", out var version)
            && !string.Equals(version, ResourceApiVersions.CoreV1, StringComparison.Ordinal))
            throw new RuntimeRunValidationException("api_version_not_supported", $"Only api-version={ResourceApiVersions.CoreV1} is supported.");
    }
}
