using System.Security.Claims;
using Agentstration.Identity;
using Agentstration.Identity.Api.Security;
using Agentstration.Identity.Contracts;
using Agentstration.Web.Security;
using Microsoft.Extensions.Options;

namespace Agentstration.Identity.Api.Api;

public static class AwpWorkerEndpoints
{
    public static IEndpointRouteBuilder MapAwpWorkerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var enrollment = endpoints.MapGroup("/api/awp/enrollment").AllowAnonymous().RequireRateLimiting("awp-enrollment-public");
        enrollment.MapPost("/announce", (AnnounceAwpWorkerRequest body, AwpWorkerIdentityService service,
                IOptionsMonitor<AwpWorkerTrustOptions> options, CancellationToken token) =>
            ExecuteAsync(() => AnnounceAsync(body, service, options.CurrentValue, token)))
            .Produces<AwpWorkerEnrollmentView>()
            .WithSummary("Announce an AWP Runtime Worker for enrollment");
        enrollment.MapPost("/claim", (ClaimAwpWorkerRequest body, AwpWorkerIdentityService service,
                IOptionsMonitor<AwpWorkerTrustOptions> options, CancellationToken token) =>
            ExecuteAsync(() => service.ClaimAsync(body, EnabledInstance(options.CurrentValue), token)))
            .Produces<AwpWorkerCredential>()
            .WithSummary("Claim a one-time AWP Worker pairing code");

        var administration = endpoints.MapGroup("/api/identity/awp-workers")
            .RequireAuthorization(AgentstrationPolicies.PlatformAdmin);
        administration.MapGet("/", (AwpWorkerIdentityService service, CancellationToken token) => service.ListAsync(token))
            .Produces<AwpWorkerEnrollmentView[]>()
            .WithSummary("List AWP Runtime Worker identities");
        administration.MapPost("/{workerId:guid}/pairing-code", (Guid workerId, AwpWorkerIdentityService service, CancellationToken token) =>
            ExecuteAsync(() => service.IssuePairingCodeAsync(workerId, token)))
            .Produces<AwpWorkerPairingCode>()
            .WithSummary("Issue a fresh AWP Worker pairing code");
        administration.MapPost("/{workerId:guid}/credentials/rotate", (Guid workerId, AwpWorkerIdentityService service,
                IOptionsMonitor<AwpWorkerTrustOptions> options, CancellationToken token) =>
            ExecuteAsync(() => service.RotateAsync(workerId, EnabledInstance(options.CurrentValue), token)))
            .Produces<AwpWorkerCredential>()
            .WithSummary("Issue an overlapping AWP Worker credential");
        administration.MapPost("/{workerId:guid}/credentials/{credentialId:guid}/revoke",
            (Guid workerId, Guid credentialId, AwpWorkerIdentityService service, CancellationToken token) =>
                ExecuteNoContentAsync(() => service.RevokeAsync(workerId, credentialId, token)))
            .Produces(StatusCodes.Status204NoContent)
            .WithSummary("Revoke an AWP Worker credential");

        endpoints.MapPost("/api/awp/v1/session", ActivateSessionAsync)
            .RequireAuthorization(AgentstrationPolicies.AwpWorker)
            .Produces(StatusCodes.Status204NoContent)
            .WithSummary("Activate the current AWP Worker process session");
        endpoints.MapGet("/api/awp/v1/identity", (ClaimsPrincipal principal) =>
        {
            if (!AwpWorkerAuthentication.TryGetIdentity(principal, out var identity)) return Results.Unauthorized();
            return Results.Ok(new AwpWorkerIdentityView(
                identity.WorkerId, identity.WorkerSessionId, identity.ProtocolVersion));
        }).RequireAuthorization(AgentstrationPolicies.AwpWorker)
            .Produces<AwpWorkerIdentityView>()
            .WithSummary("Inspect the authenticated AWP Worker identity");
        return endpoints;
    }

    private static async Task<AwpWorkerEnrollmentView> AnnounceAsync(
        AnnounceAwpWorkerRequest body,
        AwpWorkerIdentityService service,
        AwpWorkerTrustOptions options,
        CancellationToken cancellationToken)
    {
        _ = EnabledInstance(options);
        return await service.AnnounceAsync(body, cancellationToken);
    }

    private static Task<IResult> ActivateSessionAsync(
        ClaimsPrincipal principal,
        AwpWorkerIdentityService service,
        CancellationToken cancellationToken)
    {
        if (!AwpWorkerAuthentication.TryGetIdentity(principal, out var identity))
            return Task.FromResult(Results.Unauthorized());
        return ExecuteNoContentAsync(() =>
            service.ActivateSessionAsync(identity.WorkerId, identity.WorkerSessionId, cancellationToken));
    }

    private static string EnabledInstance(AwpWorkerTrustOptions options)
    {
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.InstanceId))
            throw new AwpWorkerIdentityException("worker_trust_disabled", "AWP Worker trust is disabled.", 503);
        return options.InstanceId;
    }

    private static async Task<IResult> ExecuteAsync<T>(Func<Task<T>> action)
    {
        try { return Results.Ok(await action()); }
        catch (AwpWorkerIdentityException exception)
        {
            return Results.Problem(statusCode: exception.StatusCode, title: exception.Message,
                extensions: new Dictionary<string, object?> { ["code"] = exception.Code });
        }
        catch (ResourceManagement.ResourceConcurrencyException)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict,
                title: "The Worker identity changed concurrently.",
                extensions: new Dictionary<string, object?> { ["code"] = "concurrent_update" });
        }
    }

    private static async Task<IResult> ExecuteNoContentAsync(Func<Task> action)
    {
        try
        {
            await action();
            return Results.NoContent();
        }
        catch (AwpWorkerIdentityException exception)
        {
            return Results.Problem(statusCode: exception.StatusCode, title: exception.Message,
                extensions: new Dictionary<string, object?> { ["code"] = exception.Code });
        }
        catch (ResourceManagement.ResourceConcurrencyException)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict,
                title: "The Worker identity changed concurrently.",
                extensions: new Dictionary<string, object?> { ["code"] = "concurrent_update" });
        }
    }
}
