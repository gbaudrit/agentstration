using System.Security.Claims;
using System.Text.Encodings.Web;
using Agentstration.Identity.Contracts;
using Agentstration.Web.Hosting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Agentstration.Identity.Api.Security;

public sealed class InternalDelegationAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    InternalDelegationKeys keys,
    IIdentityStore identities,
    TimeProvider timeProvider)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorization = Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            || !authorization.AsSpan("Bearer ".Length)
                .StartsWith(InternalDelegationDefaults.TokenPrefix, StringComparison.Ordinal))
            return AuthenticateResult.NoResult();
        var encoded = authorization[("Bearer " + InternalDelegationDefaults.TokenPrefix).Length..];
        if (encoded.Length is < 100 or > 4096) return Invalid();

        var audience = AudienceForPath(Request.Path);
        if (audience is null) return Invalid();
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = InternalDelegationDefaults.Issuer,
            ValidateAudience = true,
            ValidAudience = audience,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = keys.ValidationKeys,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ClockSkew = TimeSpan.Zero,
            LifetimeValidator = (notBefore, expires, _, _) =>
                notBefore is not null && expires is not null
                && notBefore <= timeProvider.GetUtcNow()
                && expires > timeProvider.GetUtcNow()
        };
        var validation = await new JsonWebTokenHandler { MapInboundClaims = false }
            .ValidateTokenAsync(encoded, parameters);
        if (!validation.IsValid || validation.ClaimsIdentity is null) return Invalid();

        var claims = validation.ClaimsIdentity;
        if (!Guid.TryParse(claims.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, out var principalId)
            || !Guid.TryParse(claims.FindFirst(InternalDelegationDefaults.TenantClaim)?.Value, out var tenantId)
            || !Guid.TryParse(claims.FindFirst(InternalDelegationDefaults.WorkspaceClaim)?.Value, out var workspaceId)
            || string.IsNullOrWhiteSpace(claims.FindFirst(InternalDelegationDefaults.SessionClaim)?.Value)
            || !BffSessionAuthenticationMethods.Local.Equals(
                claims.FindFirst(InternalDelegationDefaults.MethodClaim)?.Value, StringComparison.Ordinal)
            || !string.IsNullOrEmpty(Request.Headers["X-Agentstration-Workspace"])
                && !string.Equals(Request.Headers["X-Agentstration-Workspace"], workspaceId.ToString("D"), StringComparison.OrdinalIgnoreCase)
            || Guid.TryParse(Request.Cookies[RequestContextMiddleware.WorkspaceCookie], out var cookieWorkspace)
                && cookieWorkspace != workspaceId
            || Request.RouteValues.TryGetValue("workspaceId", out var routeWorkspaceValue)
                && Guid.TryParse(routeWorkspaceValue?.ToString(), out var routeWorkspace)
                && routeWorkspace != workspaceId)
            return Invalid();

        var principal = await identities.GetPrincipalAsync(principalId, Context.RequestAborted);
        var workspace = await identities.GetWorkspaceAsync(workspaceId, Context.RequestAborted);
        var tenant = await identities.GetTenantAsync(tenantId, Context.RequestAborted);
        var platformAdministrator = await identities.IsPlatformAdministratorAsync(principalId, Context.RequestAborted);
        if (principal is not { Status: PrincipalStatus.Active, Kind: PrincipalKind.Human }
            || workspace is not { Status: WorkspaceStatus.Active }
            || workspace.TenantId != tenantId || tenant?.Status != TenantStatus.Active
            || !platformAdministrator && (await identities.FindWorkspaceMembershipAsync(
                workspaceId, principalId, Context.RequestAborted))?.Status != MembershipStatus.Active)
            return Invalid();

        var identity = new ClaimsIdentity(claims.Claims, Scheme.Name);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, principalId.ToString("D")));
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    private static string? AudienceForPath(PathString path)
    {
        if (!path.StartsWithSegments("/api") || path.StartsWithSegments("/api/internal")) return null;
        if (path.StartsWithSegments("/api/runtime")
            || path.StartsWithSegments("/api/namespaces")
                && path.Value?.Contains("/agents", StringComparison.OrdinalIgnoreCase) == true)
            return InternalDelegationAudiences.Runtime;
        if (path.StartsWithSegments("/api/flows") || path.StartsWithSegments("/api/flowRuns")
            || path.StartsWithSegments("/api/namespaces") && path.Value?.Contains("/flows", StringComparison.OrdinalIgnoreCase) == true)
            return InternalDelegationAudiences.Flow;
        if (path.StartsWithSegments("/api/work") || path.StartsWithSegments("/api/tasks")
            || path.StartsWithSegments("/api/workspaces") || path.StartsWithSegments("/api/workplace")
            || path.StartsWithSegments("/api/entries") || path.StartsWithSegments("/api/resources")
            || path.StartsWithSegments("/api/management/entries")
            || path.StartsWithSegments("/api/management/workspaces")
            || path.StartsWithSegments("/api/namespaces")
                && path.Value?.Contains("/entries", StringComparison.OrdinalIgnoreCase) == true)
            return InternalDelegationAudiences.Work;
        return InternalDelegationAudiences.Management;
    }

    private static AuthenticateResult Invalid() => AuthenticateResult.Fail("Invalid internal delegation.");
}
