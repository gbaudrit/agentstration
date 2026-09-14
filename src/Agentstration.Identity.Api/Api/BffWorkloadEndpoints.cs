using System.Security.Claims;
using Agentstration.Identity.Api.Security;
using Agentstration.Identity.Contracts;
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
        return endpoints;
    }
}
