using Agentstration.Aep.Abstractions;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.Resources;
using Agentstration.Web.Security;

namespace Agentstration.Web.Api;

public static class AepEnrollmentEndpoints
{
    public static IEndpointRouteBuilder MapAgentstrationAepEnrollment(this IEndpointRouteBuilder endpoints)
    {
        var publicEndpoints = endpoints.MapGroup("/api/aep/enrollments").AllowAnonymous().RequireRateLimiting("aep-enrollment-public");
        publicEndpoints.MapPost("/announce", (AepEnrollmentAnnouncement body, HttpRequest request, AepEnrollmentService service, CancellationToken token) =>
            ExecuteAsync(() => service.AnnounceAsync(body, EnrollmentProof(request), token)))
            .Produces<AepEnrollmentAnnouncementResponse>()
            .WithSummary("Announce an AEP extension for enrollment");
        publicEndpoints.MapPost("/claim", (AepEnrollmentClaim body, AepEnrollmentService service, CancellationToken token) =>
            ExecuteAsync(() => service.ClaimAsync(body, token)))
            .Produces<AepEnrollmentCredential>()
            .WithSummary("Claim a one-time AEP pairing code");
        publicEndpoints.MapPost("/ready", (AepEnrollmentReady body, AepEnrollmentService service, CancellationToken token) =>
            ExecuteAsync(() => service.ReadyAsync(body, token)))
            .Produces<AepEnrollmentReadyResponse>()
            .WithSummary("Complete AEP pairing and identity verification");

        var administration = endpoints.MapGroup("/api/aep/enrollments").RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        administration.MapGet("/settings", (AepEnrollmentSettingsService service, CancellationToken token) =>
            ExecuteAsync(() => service.GetAsync(token)))
            .Produces<AepEnrollmentSettingsSnapshot>()
            .WithSummary("Get the effective AEP enrollment-mode policy");
        administration.MapPut("/settings", (PutAepEnrollmentSettingsRequest body, ICurrentRequestContext current, AepEnrollmentSettingsService service, CancellationToken token) =>
            ExecuteAsync(() => service.UpdateAsync(current.Current, body.PairingCodeEnabled, body.SharedKeyFileEnabled, body.ETag, token)))
            .Produces<AepEnrollmentSettingsSnapshot>()
            .WithSummary("Enable or disable configurable AEP enrollment modes");
        administration.MapGet("/", (ICurrentRequestContext current, AepEnrollmentService service, CancellationToken token) =>
            ExecuteAsync(() => service.ListAsync(current.Current, token)))
            .Produces<IEnumerable<AepEnrollmentRequestResource>>()
            .WithSummary("List AEP enrollment requests");
        administration.MapPost("/{requestId:guid}/assign", (Guid requestId, AssignAepEnrollmentRequest body, ICurrentRequestContext current, AepEnrollmentService service, CancellationToken token) =>
            ExecuteNoContentAsync(() => service.AssignAsync(current.Current, requestId, body.ScopeRef, token)))
            .Produces(StatusCodes.Status204NoContent)
            .WithSummary("Assign an AEP enrollment request to an instance, tenant, or workspace scope");
        administration.MapPost("/{requestId:guid}/rotate", (Guid requestId, ICurrentRequestContext current, AepEnrollmentService service, CancellationToken token) =>
            ExecuteAsync(() => service.RotateAsync(current.Current, requestId, token)))
            .Produces<AepPairingCodeResult>()
            .WithSummary("Issue a fresh AEP pairing code");
        administration.MapPost("/{requestId:guid}/reject", (Guid requestId, ICurrentRequestContext current, AepEnrollmentService service, CancellationToken token) =>
            ExecuteNoContentAsync(() => service.CloseAsync(current.Current, requestId, AepEnrollmentState.Rejected, token)))
            .Produces(StatusCodes.Status204NoContent)
            .WithSummary("Reject an AEP enrollment request");
        administration.MapPost("/{requestId:guid}/cancel", (Guid requestId, ICurrentRequestContext current, AepEnrollmentService service, CancellationToken token) =>
            ExecuteNoContentAsync(() => service.CloseAsync(current.Current, requestId, AepEnrollmentState.Cancelled, token)))
            .Produces(StatusCodes.Status204NoContent)
            .WithSummary("Cancel an AEP enrollment request");
        administration.MapPost("/{requestId:guid}/rotate-credential", (Guid requestId, ICurrentRequestContext current, AepEnrollmentService service, CancellationToken token) =>
            ExecuteAsync(() => service.RotateCredentialAsync(current.Current, requestId, token)))
            .Produces<AepCredentialLifecycleResponse>()
            .WithSummary("Rotate an active AEP credential without downtime");
        administration.MapPost("/{requestId:guid}/revoke", (Guid requestId, ICurrentRequestContext current, AepEnrollmentService service, CancellationToken token) =>
            ExecuteNoContentAsync(() => service.RevokeCredentialAsync(current.Current, requestId, token)))
            .Produces(StatusCodes.Status204NoContent)
            .WithSummary("Revoke an active AEP credential");
        return endpoints;
    }

    private static AepEnrollmentProof? EnrollmentProof(HttpRequest request) =>
        long.TryParse(request.Headers["X-AEP-Enrollment-Timestamp"], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var timestamp)
        && request.Headers["X-AEP-Enrollment-Signature"].ToString() is { Length: > 0 } signature
            ? new(timestamp, signature)
            : null;

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

public sealed record PutAepEnrollmentSettingsRequest(
    bool PairingCodeEnabled,
    bool SharedKeyFileEnabled,
    string? ETag);

public sealed record AssignAepEnrollmentRequest(ResourceScopeRef ScopeRef);
