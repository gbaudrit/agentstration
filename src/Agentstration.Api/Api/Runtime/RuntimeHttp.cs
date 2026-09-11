using Agentstration.Management.Abstractions;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;

namespace Agentstration.Web.Api.Runtime;

internal static class RuntimeHttp
{
    public static RuntimeRunScope CurrentScope(ICurrentRequestContext context)
    {
        var current = context.Current;
        return new RuntimeRunScope(current.TenantId, new WorkspaceId(current.WorkspaceId), current.PrincipalId);
    }

    public static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (RuntimeRunNotFoundException exception) { return Results.Problem(statusCode: 404, title: "run_not_found", detail: exception.Message); }
        catch (RuntimeRunNotTerminalException exception) { return Results.Problem(statusCode: 409, title: "run_not_terminal", detail: exception.Message); }
        catch (RuntimeRunConcurrencyException exception) { return Results.Problem(statusCode: 412, title: "precondition_failed", detail: exception.Message); }
        catch (RuntimeRunValidationException exception) { return Results.Problem(statusCode: 400, title: exception.Code, detail: exception.Message); }
        catch (ControlPlaneResourceNotFoundException exception) { return Results.Problem(statusCode: 404, title: "resource_not_found", detail: exception.Message); }
        catch (ControlPlaneConcurrencyException exception) { return Results.Problem(statusCode: 409, title: "resource_version_conflict", detail: exception.Message); }
        catch (ArgumentException exception) { return Results.Problem(statusCode: 400, title: "validation_failed", detail: exception.Message); }
        catch (InvalidOperationException exception) { return Results.Problem(statusCode: 409, title: "operation_conflict", detail: exception.Message); }
    }

    public static IResult RunResult(StoredRuntimeRun stored, HttpResponse response, int statusCode)
    {
        response.Headers.ETag = stored.ETag;
        return Results.Json(stored.Value, statusCode: statusCode);
    }
}
