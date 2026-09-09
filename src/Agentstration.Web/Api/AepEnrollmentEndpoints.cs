using Agentstration.Aep.Abstractions;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.Web.Security;

namespace Agentstration.Web.Api;

public static class AepEnrollmentEndpoints
{
    public static IEndpointRouteBuilder MapAgentstrationAepEnrollment(this IEndpointRouteBuilder endpoints)
    {
        var publicEndpoints = endpoints.MapGroup("/api/aep/enrollments").AllowAnonymous().RequireRateLimiting("aep-enrollment-public");
        publicEndpoints.MapPost("/announce", (AepEnrollmentAnnouncement body, AepEnrollmentService service, CancellationToken token) =>
            ExecuteAsync(() => service.AnnounceAsync(body, token)));
        publicEndpoints.MapPost("/claim", (AepEnrollmentClaim body, AepEnrollmentService service, CancellationToken token) =>
            ExecuteAsync(() => service.ClaimAsync(body, token)));
        publicEndpoints.MapPost("/ready", (AepEnrollmentReady body, AepEnrollmentService service, CancellationToken token) =>
            ExecuteAsync(() => service.ReadyAsync(body, token)));

        var administration = endpoints.MapGroup("/api/aep/enrollments").RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        administration.MapGet("/", (ICurrentRequestContext current, AepEnrollmentService service, CancellationToken token) =>
            ExecuteAsync(() => service.ListAsync(current.Current, token)));
        administration.MapPost("/{requestId:guid}/rotate", (Guid requestId, ICurrentRequestContext current, AepEnrollmentService service, CancellationToken token) =>
            ExecuteAsync(() => service.RotateAsync(current.Current, requestId, token)));
        administration.MapPost("/{requestId:guid}/reject", (Guid requestId, ICurrentRequestContext current, AepEnrollmentService service, CancellationToken token) =>
            ExecuteNoContentAsync(() => service.CloseAsync(current.Current, requestId, AepEnrollmentState.Rejected, token)));
        administration.MapPost("/{requestId:guid}/cancel", (Guid requestId, ICurrentRequestContext current, AepEnrollmentService service, CancellationToken token) =>
            ExecuteNoContentAsync(() => service.CloseAsync(current.Current, requestId, AepEnrollmentState.Cancelled, token)));
        return endpoints;
    }

    private static async Task<IResult> ExecuteAsync<T>(Func<Task<T>> operation)
    {
        try { return Results.Ok(await operation()); }
        catch (AepEnrollmentException exception)
        {
            return Results.Json(new { error = new AepEnrollmentError(exception.Code, exception.Message) }, statusCode: exception.StatusCode);
        }
        catch (ControlPlaneConcurrencyException)
        {
            return Results.Json(new { error = new AepEnrollmentError("request_changed", "The enrollment request changed; retry the operation.") }, statusCode: 409);
        }
    }

    private static async Task<IResult> ExecuteNoContentAsync(Func<Task> operation)
    {
        try { await operation(); return Results.NoContent(); }
        catch (AepEnrollmentException exception)
        {
            return Results.Json(new { error = new AepEnrollmentError(exception.Code, exception.Message) }, statusCode: exception.StatusCode);
        }
        catch (ControlPlaneConcurrencyException)
        {
            return Results.Json(new { error = new AepEnrollmentError("request_changed", "The enrollment request changed; retry the operation.") }, statusCode: 409);
        }
    }
}
