using Agentstration.Runtime.Abstractions;

namespace Agentstration.Web.Api.Runtime;

internal static class RuntimeHttp
{
    public static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (RuntimeRunNotFoundException exception) { return Results.Problem(statusCode: 404, title: "run_not_found", detail: exception.Message); }
        catch (RuntimeRunConcurrencyException exception) { return Results.Problem(statusCode: 412, title: "precondition_failed", detail: exception.Message); }
        catch (RuntimeRunValidationException exception) { return Results.Problem(statusCode: 400, title: exception.Code, detail: exception.Message); }
        catch (ArgumentException exception) { return Results.Problem(statusCode: 400, title: "validation_failed", detail: exception.Message); }
        catch (InvalidOperationException exception) { return Results.Problem(statusCode: 409, title: "operation_conflict", detail: exception.Message); }
    }

    public static IResult RunResult(StoredRuntimeRun stored, HttpResponse response, int statusCode)
    {
        response.Headers.ETag = stored.ETag;
        return Results.Json(stored.Value, statusCode: statusCode);
    }
}
