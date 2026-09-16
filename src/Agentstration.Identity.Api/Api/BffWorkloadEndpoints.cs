using System.Security.Claims;
using Agentstration.Identity.Api.Security;
using Agentstration.Identity.Contracts;
using Agentstration.Security.AspNetCoreIdentity;
using Agentstration.Web.Configuration;
using Agentstration.Web.Security;
using Microsoft.Extensions.Options;

namespace Agentstration.Identity.Api;

public static class BffWorkloadEndpoints
{
    public static IEndpointRouteBuilder MapBffWorkloadEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/internal/bff/trust", (ClaimsPrincipal principal, IOptionsMonitor<BffWorkloadTrustOptions> options) =>
            Results.Ok(new
            {
                workloadId = principal.FindFirst(BffWorkloadAuthentication.WorkloadClaim)!.Value,
                credentialId = principal.FindFirst(BffWorkloadAuthentication.CredentialClaim)!.Value,
                instanceId = options.CurrentValue.InstanceId
            }))
            .RequireAuthorization(AgentstrationPolicies.BffWorkload)
            .ExcludeFromDescription();
        endpoints.MapPost("/api/internal/bff/sessions/local", async (
            BffLocalSessionRequest request,
            BffSessionAuthorityService sessions,
            IOptions<AgentstrationApiOptions> options,
            CancellationToken cancellationToken) =>
        {
            if (!ApiAuthenticationOptions.SupportsLocalAccounts(options.Value.Authentication.Mode))
                return Results.NotFound();
            var result = await sessions.AuthenticateLocalAsync(request, cancellationToken);
            return result.Outcome switch
            {
                LocalLoginOutcome.Succeeded when result.Identity is not null => Results.Ok(result.Identity),
                LocalLoginOutcome.LockedOut => Results.Problem(
                    statusCode: StatusCodes.Status423Locked,
                    title: "account_locked"),
                _ => Results.Unauthorized()
            };
        })
            .RequireAuthorization(AgentstrationPolicies.BffWorkload)
            .ExcludeFromDescription();
        endpoints.MapPost("/api/internal/bff/sessions/validate", async (
            BffSessionValidationRequest request,
            BffSessionAuthorityService sessions,
            CancellationToken cancellationToken) =>
            Results.Ok(await sessions.ValidateAsync(request, cancellationToken)))
            .RequireAuthorization(AgentstrationPolicies.BffWorkload)
            .ExcludeFromDescription();
        endpoints.MapPost("/api/internal/bff/delegations", async (
            BffDelegationRequest request,
            InternalDelegationService delegations,
            CancellationToken cancellationToken) =>
        {
            var result = await delegations.IssueAsync(request, cancellationToken);
            return result is null
                ? Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "delegation_denied")
                : Results.Ok(result);
        })
            .RequireAuthorization(AgentstrationPolicies.BffWorkload)
            .ExcludeFromDescription();
        endpoints.MapGet("/api/internal/bff/delegation-keys", (InternalDelegationKeys keys) =>
            Results.Ok(new { keys = keys.PublicKeys }))
            .RequireAuthorization(AgentstrationPolicies.BffWorkload)
            .ExcludeFromDescription();
        return endpoints;
    }
}
